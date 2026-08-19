using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Bearing.App.Connections;
using Bearing.App.Formatting;
using Bearing.App.Results;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Bearing.Core.Schema;
using Bearing.Core.Workspace;
using Bearing.Sql;

namespace Bearing.App.ViewModels;

public sealed partial class ShellViewModel
{
    // ---- Project lifecycle -------------------------------------------------------------------

    public async Task InitializeAsync(string projectDirectory)
    {
        try
        {
            _project = await OpenOrCreate(projectDirectory);
            _ctx.GetOrAdd(_project);   // register before any tab exists — NewTab stamps tabs with this project
            await _recentProjects.AddAsync(_project.Directory, CancellationToken.None);
            await RefreshRecentAsync();

            RefreshConnections();
            RefreshScripts();

            var session = await _sessionStore.LoadAsync(_project.Directory, CancellationToken.None);
            SidePaneOpen = session?.SidePaneOpen ?? true;
            SidePaneWidth = session?.SidePaneWidth ?? 260;
            ResultsViewMode = session?.ResultsViewMode ?? Bearing.Core.Workspace.ResultsViewMode.Stacked;
            _ctx.DefaultConnectionId = session?.ActiveConnectionId
                                  ?? _project.Manifest.Connections.FirstOrDefault()?.Id;

            await _workspace.RestoreTabsAsync(session);
            foreach (var tab in _workspace.Tabs) ApplyConnectionDisplay(tab);

            UpdateTitle();
            OnPropertyChanged(nameof(ProjectDirectory));
            OnPropertyChanged(nameof(CurrentProjectName));
            StatusText = $"Project '{_project.Manifest.Name}'. " + SecretPosture();
        }
        catch (Exception ex)
        {
            StatusText = $"Project load error: {ex.Message}";
            if (_workspace.Tabs.Count == 0) _workspace.NewTab();
        }
    }

    /// <summary>Where this session's passwords live, for the status bar. Two postures: the OS keychain, or
    /// nothing at all — there is no on-disk fallback. "Couldn't be reached" rather than "not found" because
    /// the probe can't tell a missing keyring from a locked or not-yet-serving one; the connection dialog
    /// shows the reason it was given.</summary>
    private string SecretPosture() => SecretStorage.Secure
        ? "Secrets: OS keychain."
        : "⚠ No keyring reachable — passwords aren't saved; you'll be asked when connecting.";

    /// <summary>
    /// Startup entry: reopen the most-recently-used project that still exists on disk, falling back to
    /// <paramref name="fallbackDir"/> (the default project, created if absent) when the recent list is
    /// empty or every entry is missing/unreadable. Skips stale entries (a deleted or corrupt project)
    /// rather than resurrecting them.
    /// </summary>
    public async Task ResumeLastProjectAsync(string fallbackDir)
    {
        var recent = await _recentProjects.ListAsync(CancellationToken.None);
        foreach (var dir in recent)
        {
            // Probe: only resume a project whose manifest is actually openable; OpenOrCreate would
            // otherwise recreate a since-deleted directory, which isn't "the last project I used".
            try { await _projectStore.OpenAsync(dir, CancellationToken.None); }
            catch { continue; }
            await InitializeAsync(dir);
            return;
        }
        await InitializeAsync(fallbackDir);
    }

    /// <summary>
    /// Switch to another project. This is a <b>view</b> change, not a lifecycle event: the outgoing
    /// project's tabs are parked (same view-model instances, so their results, FK history, pending edits
    /// and in-flight queries all survive), its live sessions and schema readers stay in the pools under
    /// the usual idle/expiry rules, and switching back unparks exactly what was left behind. Nothing here
    /// closes a connection or discards a result — only closing a tab or quitting does that.
    /// <para>
    /// Sessions are keyed by connection id (§9.4) and ids are Guids, so two projects' pools coexist in the
    /// one manager without collision.
    /// </para>
    /// </summary>
    public async Task OpenProjectAsync(string projectDirectory)
    {
        if (_project is not null && string.Equals(Path.GetFullPath(projectDirectory), _project.Directory, StringComparison.Ordinal))
            return;

        await _workspace.FlushScratchAsync();   // land pending scratch writes before the tabs leave the strip
        SaveWorkspace();
        _ctx.Park(SidePaneOpen, SidePaneWidth, ResultsViewMode);
        _ctx.DefaultConnectionId = null;
        if (_ctx.Find(projectDirectory) is { } known) await ActivateAsync(known);
        else await InitializeAsync(projectDirectory);
    }

    /// <summary>Bring an already-open project back on screen from its parked state. Deliberately does not
    /// touch <c>session.json</c>: the in-memory tabs are newer than anything on disk (unsaved buffer edits
    /// included), and rebuilding them is exactly what would throw the results away.</summary>
    private async Task ActivateAsync(ProjectWorkspace known)
    {
        _ctx.Restore(known);           // sets _ctx.Project and re-selects the tab that was active
        SidePaneOpen = known.SidePaneOpen;
        SidePaneWidth = known.SidePaneWidth;
        ResultsViewMode = known.ResultsViewMode;
        _ctx.DefaultConnectionId = known.DefaultConnectionId
                              ?? _project?.Manifest.Connections.FirstOrDefault()?.Id;

        RefreshConnections();
        RefreshScripts();
        foreach (var tab in _workspace.Tabs) ApplyConnectionDisplay(tab);
        UpdateTitle();
        OnPropertyChanged(nameof(ProjectDirectory));
        OnPropertyChanged(nameof(CurrentProjectName));
        // Awaited, not fire-and-forget: this rebuilds RecentProjects, and a rebuild that lands *after* the
        // switch has been announced leaves the project switcher with no selection (see RefreshRecentAsync).
        await TouchRecentAsync(known.Project.Directory);
        StatusText = $"Project '{known.Project.Manifest.Name}'.";
    }

    /// <summary>Bump a project to the top of the recent list and re-read it.</summary>
    private async Task TouchRecentAsync(string directory)
    {
        await _recentProjects.AddAsync(directory, CancellationToken.None);
        await RefreshRecentAsync();
    }

    public async Task NewProjectAsync(string projectDirectory, string name)
    {
        await _workspace.FlushScratchAsync();   // as in OpenProjectAsync — don't drop a debounced write
        SaveWorkspace();
        _ctx.Park(SidePaneOpen, SidePaneWidth, ResultsViewMode);
        _ctx.DefaultConnectionId = null;
        _project = await _projectStore.CreateAsync(projectDirectory, name, CancellationToken.None);
        _ctx.GetOrAdd(_project);
        await _recentProjects.AddAsync(_project.Directory, CancellationToken.None);
        await RefreshRecentAsync();
        RefreshConnections();
        RefreshScripts();
        _workspace.NewTab();
        UpdateTitle();
        OnPropertyChanged(nameof(ProjectDirectory));
        OnPropertyChanged(nameof(CurrentProjectName));
        StatusText = $"Created project '{name}'.";
    }

    /// <summary>
    /// Recent projects that can be removed: everything except the ones currently open. Bound by the
    /// remove-project picker, so a project you're using is never offered as a thing to delete.
    /// </summary>
    public IReadOnlyList<RecentProjectItem> RemovableProjects
        => RecentProjects.Where(p => !IsOpen(p.Directory)).ToList();

    /// <summary>
    /// Remove a project the user is done with: drop its recent-list entry and, when
    /// <paramref name="deleteFromDisk"/>, delete its directory too. Pruning of <em>missing</em> folders
    /// already self-heals in <see cref="RefreshRecentAsync"/>; this is the deliberate half, for a project
    /// that is still there.
    /// <para>
    /// Refuses a project that is open — active or parked. Those have live tabs, buffers and sessions pointing
    /// at the files, so a delete would pull the ground out from under them, and a list removal would be undone
    /// by the next switch anyway (every activation touches the list).
    /// </para>
    /// </summary>
    /// <returns>True when the project was removed.</returns>
    public async Task<bool> RemoveRecentProjectAsync(string directory, bool deleteFromDisk)
    {
        var full = Path.GetFullPath(directory);
        var name = RecentProjects.FirstOrDefault(p =>
                       string.Equals(Path.GetFullPath(p.Directory), full, StringComparison.Ordinal))?.Name
                   ?? new DirectoryInfo(full).Name;

        if (IsOpen(full))
        {
            StatusText = $"'{name}' is open — switch to another project first.";
            return false;
        }

        if (deleteFromDisk)
        {
            // The store refuses anything that isn't a project, so this also catches a stale entry pointing
            // somewhere that has since become an ordinary folder.
            try { await _projectStore.DeleteAsync(full, CancellationToken.None); }
            catch (Exception ex)
            {
                StatusText = $"Could not delete '{name}': {ex.Message}";
                return false;
            }
        }

        await _recentProjects.RemoveAsync(full, CancellationToken.None);
        await RefreshRecentAsync();
        StatusText = deleteFromDisk
            ? $"Deleted project '{name}' and its folder."
            : $"Removed '{name}' from recent projects. Its files are untouched.";
        return true;
    }

    /// <summary>Whether a project is open this session — the active one, or one parked behind a switch.</summary>
    private bool IsOpen(string directory) => _ctx.Find(directory) is not null;

    private async Task RefreshRecentAsync()
    {
        var list = await _recentProjects.ListAsync(CancellationToken.None);
        RecentProjects.Clear();
        foreach (var p in list)
        {
            // Prune entries whose folder is gone (or was never a project): resume already skips these, so
            // offering them in the switcher only produced a dead menu item that recreated an empty project
            // when clicked. Dropped from the stored list too, so it self-heals instead of re-checking forever.
            if (!Directory.Exists(p))
            {
                await _recentProjects.RemoveAsync(p, CancellationToken.None);
                continue;
            }
            RecentProjects.Add(new RecentProjectItem(p, await ResolveProjectName(p)));
        }
        // Rebuilding the list drops the switcher's selection (clearing an ItemsSource nulls SelectedItem)
        // and the entries are fresh instances, so re-announce the current project: the switcher resolves
        // its selection from ProjectDirectory and would otherwise sit blank until the next project change.
        OnPropertyChanged(nameof(ProjectDirectory));
    }

    /// <summary>Display name for a recent project: its manifest name, falling back to the folder name.</summary>
    private async Task<string> ResolveProjectName(string dir)
    {
        if (_project is not null && string.Equals(_project.Directory, Path.GetFullPath(dir), StringComparison.Ordinal))
            return _project.Manifest.Name;
        try { return (await _projectStore.OpenAsync(dir, CancellationToken.None)).Manifest.Name; }
        catch { return new DirectoryInfo(dir).Name; }
    }

    /// <summary>Rename the current project (manifest name only; the folder path is unchanged).</summary>
    public async Task RenameProjectAsync(string newName)
    {
        if (_project is null || string.IsNullOrWhiteSpace(newName)) return;
        _project.Manifest = _project.Manifest with { Name = newName.Trim() };
        await _projectStore.SaveAsync(_project, CancellationToken.None);
        UpdateTitle();
        await RefreshRecentAsync();
        OnPropertyChanged(nameof(ProjectDirectory)); // re-sync the switcher selection to the renamed item
        OnPropertyChanged(nameof(CurrentProjectName));
        StatusText = $"Renamed project to '{newName.Trim()}'.";
    }

    private async Task<Project> OpenOrCreate(string dir)
    {
        try { return await _projectStore.OpenAsync(dir, CancellationToken.None); }
        catch (FileNotFoundException)
        {
            var name = new DirectoryInfo(dir).Name;
            return await _projectStore.CreateAsync(dir, string.IsNullOrEmpty(name) ? "Default" : name, CancellationToken.None);
        }
    }
}
