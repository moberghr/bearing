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
        // Remembered so removing the last project still has somewhere to land (see RemoveCurrentProjectAsync).
        _fallbackProjectDirectory = fallbackDir;
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
    /// Sessions are keyed by connection id + database (§9.4) and ids are Guids, so two projects' pools
    /// coexist in the one manager without collision.
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
    /// Where a project browser should open: the folder that holds the current project, falling back to the
    /// one that holds the startup default. Projects are directories that sit next to each other, so "the
    /// projects folder" is simply the parent of the one you're in — no separate workspace concept needed to
    /// answer the question.
    /// </summary>
    public string? ProjectBrowseDirectory
    {
        get
        {
            var known = _project?.Directory ?? _fallbackProjectDirectory;
            if (known is null) return null;
            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(known));
                return parent is not null && Directory.Exists(parent) ? parent : null;
            }
            catch { return null; }   // unusable path: let the picker choose its own start
        }
    }

    /// <summary>The default project directory, from startup — where a removal lands when the recent list has
    /// nothing else in it. Null in tests that call <see cref="InitializeAsync"/> directly.</summary>
    private string? _fallbackProjectDirectory;

    /// <summary>
    /// Remove the project that is open: drop its recent-list entry and, when
    /// <paramref name="deleteFromDisk"/>, delete its directory too. The project is <b>closed</b> (not parked)
    /// and the app switches to the next most-recent one, since there is always a project on screen.
    /// <para>
    /// Deleting is the irreversible half and is ordered accordingly: pending autosaves are discarded first so
    /// nothing recreates a scratch file inside the folder as it goes, the delete happens while the project is
    /// still open (so a failure changes nothing at all), and only then is the project closed and forgotten.
    /// Forgetting instead flushes and saves the session, so reopening the folder later resumes where it left off.
    /// </para>
    /// </summary>
    /// <returns>True when the project was removed.</returns>
    public async Task<bool> RemoveCurrentProjectAsync(bool deleteFromDisk)
    {
        if (_project is not { } project) return false;
        var directory = project.Directory;
        var name = project.Manifest.Name;

        if (await SuccessorProjectAsync(directory) is not { } successor)
        {
            StatusText = $"'{name}' is the only project — open or create another one first.";
            return false;
        }

        // A query still running on one of its tabs is about to lose its project either way.
        foreach (var tab in _workspace.Tabs) tab.CancelRun();

        if (deleteFromDisk)
        {
            foreach (var tab in _workspace.Tabs) _workspace.Autosave.Discard(tab);
        }
        else
        {
            await _workspace.FlushScratchAsync();
            SaveWorkspace();
        }

        if (deleteFromDisk)
        {
            try { await _projectStore.DeleteAsync(directory, CancellationToken.None); }
            catch (Exception ex)
            {
                StatusText = $"Could not delete '{name}': {ex.Message}";
                return false;   // nothing has been closed or forgotten yet — the project is still usable
            }
            // The manifest is gone, so these connections can never be used again: drop their live sessions,
            // cached schema, and stored passwords rather than leaving orphans behind (§1.1).
            await ForgetConnectionsAsync(project);
        }

        _ctx.Close(directory);
        await _recentProjects.RemoveAsync(directory, CancellationToken.None);
        await OpenProjectAsync(successor);

        StatusText = deleteFromDisk
            ? $"Deleted project '{name}' and its folder. Now in '{CurrentProjectName}'."
            : $"Removed '{name}' from recent projects; its files are untouched. Now in '{CurrentProjectName}'.";
        return true;
    }

    /// <summary>
    /// Where to go after removing <paramref name="directory"/>: the most-recent other project that actually
    /// opens, else the startup default. Null when there is nowhere to land — the caller then refuses rather
    /// than leaving the app with no project.
    /// </summary>
    private async Task<string?> SuccessorProjectAsync(string directory)
    {
        var removed = Path.GetFullPath(directory);
        bool IsRemoved(string dir) => string.Equals(Path.GetFullPath(dir), removed, StringComparison.Ordinal);

        foreach (var candidate in await _recentProjects.ListAsync(CancellationToken.None))
        {
            if (IsRemoved(candidate) || !Directory.Exists(candidate)) continue;
            // Probe as ResumeLastProjectAsync does: don't switch to something that can't be opened.
            try { await _projectStore.OpenAsync(candidate, CancellationToken.None); }
            catch { continue; }
            return candidate;
        }
        return _fallbackProjectDirectory is { } fallback && !IsRemoved(fallback) ? fallback : null;
    }

    /// <summary>Drop everything cached for a deleted project's connections — live sessions, schema, and the
    /// keychain entries keyed by their ids. Best-effort: a keychain that refuses must not fail the removal.</summary>
    private async Task ForgetConnectionsAsync(Project project)
    {
        foreach (var connection in project.Manifest.Connections)
        {
            try
            {
                await _sessions.EvictConnectionAsync(connection.Id);   // every database it was open on
                _sessions.InvalidateSchema(connection.Id);
                await _schemaBrowser.InvalidateAsync(connection.Id);   // its own per-conn+db reader cache (§9.4)
                if (_secretStore is { } store) await store.DeleteAsync(connection.Id, CancellationToken.None);
            }
            catch { /* best-effort cleanup (§5.2); the project is already gone */ }
        }
    }

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
