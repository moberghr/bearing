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
using Squirrel.App.Connections;
using Squirrel.App.Formatting;
using Squirrel.App.Results;
using Squirrel.Core.Data;
using Squirrel.Core.Logging;
using Squirrel.Core.Schema;
using Squirrel.Core.Workspace;
using Squirrel.Sql;

namespace Squirrel.App.ViewModels;
public sealed partial class ShellViewModel
{
    // ---- Project lifecycle -------------------------------------------------------------------

    public async Task InitializeAsync(string projectDirectory)
    {
        try
        {
            _project = await OpenOrCreate(projectDirectory);
            await _recentProjects.AddAsync(_project.Directory, CancellationToken.None);
            await RefreshRecentAsync();

            RefreshConnections();
            RefreshScripts();

            var session = await _sessionStore.LoadAsync(_project.Directory, CancellationToken.None);
            SidePaneOpen = session?.SidePaneOpen ?? true;
            SidePaneWidth = session?.SidePaneWidth ?? 260;
            ResultsViewMode = session?.ResultsViewMode ?? Squirrel.Core.Workspace.ResultsViewMode.Stacked;
            _ctx.DefaultConnectionId = session?.ActiveConnectionId
                                  ?? _project.Manifest.Connections.FirstOrDefault()?.Id;

            await _workspace.RestoreTabsAsync(session);
            foreach (var tab in _workspace.Tabs) ApplyConnectionDisplay(tab);

            UpdateTitle();
            OnPropertyChanged(nameof(ProjectDirectory));
            OnPropertyChanged(nameof(CurrentProjectName));
            StatusText = $"Project '{_project.Manifest.Name}'. " +
                         (_secretStore?.IsSecure == true
                             ? "Secrets: OS keychain."
                             : "⚠ No keyring — passwords stored unencrypted on disk.");
        }
        catch (Exception ex)
        {
            StatusText = $"Project load error: {ex.Message}";
            if (_workspace.Tabs.Count == 0) _workspace.NewTab();
        }
    }

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

    /// <summary>Save the current session, dispose live connections, then switch project directory.</summary>
    public async Task OpenProjectAsync(string projectDirectory)
    {
        if (_project is not null && string.Equals(Path.GetFullPath(projectDirectory), _project.Directory, StringComparison.Ordinal))
            return;

        SaveWorkspace();
        await _sessions.DisposeAsync();
        await _schemaBrowser.DisposeAsync();
        _ctx.IsConnected = false;
        _ctx.DefaultConnectionId = null;
        _workspace.Tabs.Clear();
        await InitializeAsync(projectDirectory);
    }

    public async Task NewProjectAsync(string projectDirectory, string name)
    {
        SaveWorkspace();
        await _sessions.DisposeAsync();
        await _schemaBrowser.DisposeAsync();
        _ctx.IsConnected = false;
        _ctx.DefaultConnectionId = null;
        _workspace.Tabs.Clear();
        _project = await _projectStore.CreateAsync(projectDirectory, name, CancellationToken.None);
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

    private async Task RefreshRecentAsync()
    {
        var list = await _recentProjects.ListAsync(CancellationToken.None);
        RecentProjects.Clear();
        foreach (var p in list) RecentProjects.Add(new RecentProjectItem(p, await ResolveProjectName(p)));
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
