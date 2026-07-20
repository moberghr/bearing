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

/// <summary>
/// Shell view-model: owns the open project, its named connections, editor tabs, and query
/// execution + logging. Each tab targets its own connection; a <see cref="ConnectionSessionManager"/>
/// resolves that to a live, reusable session on first Run. Switching projects saves the current
/// session, disposes live connections, and restores the target project's tabs.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IProviderRegistry _providers;
    private readonly IProjectStore _projectStore;
    private readonly ISessionStore _sessionStore;
    private readonly IQueryLog _queryLog;
    private readonly IRecentProjects _recentProjects;
    private readonly IConnectionSessionManager _sessions;
    private readonly ISchemaBrowser _schemaBrowser;
    private ISecretStore? _secretStore;

    private Project? _project;
    private int _scratchCounter;

    public MainWindowViewModel(
        IProviderRegistry providers,
        IProjectStore projectStore,
        ISessionStore sessionStore,
        IQueryLog queryLog,
        IRecentProjects recentProjects,
        ISecretStore? secretStore = null)
    {
        _providers = providers;
        _projectStore = projectStore;
        _sessionStore = sessionStore;
        _queryLog = queryLog;
        _recentProjects = recentProjects;
        _secretStore = secretStore;
        _sessions = new ConnectionSessionManager(providers, () => _secretStore);
        _schemaBrowser = new SchemaBrowser(providers, () => _secretStore);
        History = new HistoryPanelViewModel(SearchHistoryAsync, ColorForConnection);
    }

    [ObservableProperty] private string _statusText = "Not connected.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunButtonText))]
    private bool _isBusy;

    [ObservableProperty] private bool _isConnected;

    /// <summary>The Run button doubles as Cancel while a query is in flight.</summary>
    public string RunButtonText => IsBusy ? "Cancel (Esc)" : "Run (Ctrl+Enter)";

    private CancellationTokenSource? _executionCts;
    [ObservableProperty] private string _title = "Squirrel";

    [ObservableProperty] private bool _sidePaneOpen = true;
    [ObservableProperty] private double _sidePaneWidth = 262;

    /// <summary>How a run's result sets are laid out in the dock (stacked vs tabbed). Persisted.</summary>
    [ObservableProperty] private Squirrel.Core.Workspace.ResultsViewMode _resultsViewMode = Squirrel.Core.Workspace.ResultsViewMode.Stacked;

    /// <summary>Which side panel the 262px column shows (driven by the left rail). The connection tree
    /// serves as both the Connections and Schema view, so it maps to <see cref="SidePanel.Schema"/>.</summary>
    [ObservableProperty] private SidePanel _activePanel = SidePanel.Schema;

    /// <summary>The inline history panel (day-grouped, filterable) shown when ActivePanel = History.</summary>
    public HistoryPanelViewModel History { get; }

    /// <summary>The Alt-toggled menu bar (File/Edit/View/Query/Help); hidden by default (design §).</summary>
    [ObservableProperty] private bool _isMenuVisible;

    /// <summary>Activate a side panel, or toggle the pane shut when its tile is re-clicked while open.</summary>
    public void ActivateOrTogglePanel(SidePanel panel)
    {
        if (ActivePanel == panel && SidePaneOpen) { SidePaneOpen = false; return; }
        ActivePanel = panel;
        SidePaneOpen = true;
    }

    partial void OnActivePanelChanged(SidePanel value)
    {
        SidePaneOpen = true; // selecting a rail tile always reveals the panel
        if (value == SidePanel.History) _ = History.ReloadAsync(CancellationToken.None);
    }

    /// <summary>Environment color of a connection by display name (for the history dot); null if unknown.</summary>
    private string? ColorForConnection(string name)
        => Connections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))?.EnvironmentColor;

    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();
    [ObservableProperty] private EditorTabViewModel? _selectedTab;

    /// <summary>The project's named connections (mirror of the manifest), shown in the side pane.</summary>
    public ObservableCollection<ConnectionInfo> Connections { get; } = new();

    /// <summary>Root nodes of the schema browser tree — one server node per connection.</summary>
    public ObservableCollection<ServerNodeViewModel> ServerNodes { get; } = new();

    /// <summary>Saved scripts under the project's scripts/ folder, shown in the side pane.</summary>
    public ObservableCollection<ScriptItem> Scripts { get; } = new();

    public ObservableCollection<RecentProjectItem> RecentProjects { get; } = new();

    /// <summary>Connection new tabs fall back to when there is no active tab to inherit from.</summary>
    public Guid? DefaultConnectionId { get; private set; }

    public string? ProjectDirectory => _project?.Directory;
    public string? ScriptsDirectory => _project?.ScriptsDirectory;
    public string? CurrentProjectName => _project?.Manifest.Name;

    /// <summary>Environment hex of the selected tab's connection (null = untagged/none). Drives the
    /// app-wide <c>ConnectionBrush</c>; the view recolors the accent when this changes.</summary>
    public string? ActiveConnectionColor => SelectedTab?.ConnectionColor;

    /// <summary>Two-way binding target for the per-tab connection picker.</summary>
    public ConnectionInfo? SelectedTabConnection
    {
        get => SelectedTab?.ConnectionId is { } id ? FindConnection(id) : null;
        set { if (SelectedTab is { } tab) SetTabConnection(tab, value?.Id); }
    }

    public void AttachSecretStore(ISecretStore secretStore) => _secretStore = secretStore;

    /// <summary>True when secrets go to a real OS keychain; false when they fall back to a plaintext-equivalent file.</summary>
    public bool SecretStorageSecure => _secretStore?.IsSecure == true;

    /// <summary>Dispose all live connections (query sessions + schema-browser pools); safe to call fire-and-forget from the close path.</summary>
    public async ValueTask DisposeSessionsAsync()
    {
        await _sessions.DisposeAsync();
        await _schemaBrowser.DisposeAsync();
    }

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
            DefaultConnectionId = session?.ActiveConnectionId
                                  ?? _project.Manifest.Connections.FirstOrDefault()?.Id;

            await RestoreTabsAsync(session);
            foreach (var tab in Tabs) ApplyConnectionDisplay(tab);

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
            if (Tabs.Count == 0) NewTab();
        }
    }

    /// <summary>Save the current session, dispose live connections, then switch project directory.</summary>
    public async Task OpenProjectAsync(string projectDirectory)
    {
        if (_project is not null && string.Equals(Path.GetFullPath(projectDirectory), _project.Directory, StringComparison.Ordinal))
            return;

        SaveWorkspace();
        await _sessions.DisposeAsync();
        await _schemaBrowser.DisposeAsync();
        IsConnected = false;
        DefaultConnectionId = null;
        Tabs.Clear();
        await InitializeAsync(projectDirectory);
    }

    public async Task NewProjectAsync(string projectDirectory, string name)
    {
        SaveWorkspace();
        await _sessions.DisposeAsync();
        await _schemaBrowser.DisposeAsync();
        IsConnected = false;
        DefaultConnectionId = null;
        Tabs.Clear();
        _project = await _projectStore.CreateAsync(projectDirectory, name, CancellationToken.None);
        await _recentProjects.AddAsync(_project.Directory, CancellationToken.None);
        await RefreshRecentAsync();
        RefreshConnections();
        RefreshScripts();
        NewTab();
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

    // ---- Tabs --------------------------------------------------------------------------------

    public EditorTabViewModel NewTab(string text = "", string? scriptPath = null)
    {
        var inherit = SelectedTab?.ConnectionId ?? DefaultConnectionId;
        var tab = new EditorTabViewModel($"Scratch {++_scratchCounter}", text, scriptPath)
        {
            ConnectionId = inherit,
        };
        ApplyConnectionDisplay(tab);
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    public void CloseTab(EditorTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        Tabs.Remove(tab);
        if (Tabs.Count == 0) { NewTab(); return; }
        if (ReferenceEquals(SelectedTab, tab) || SelectedTab is null)
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    private async Task RestoreTabsAsync(SessionState? session)
    {
        Tabs.Clear();
        _scratchCounter = 0;

        var editors = session?.OpenEditors ?? new List<OpenEditor>();
        foreach (var e in editors)
        {
            var abs = e.ScriptPath is { } rel && _project is not null ? Path.Combine(_project.Directory, rel) : null;
            EditorTabViewModel tab;
            if (abs is not null && File.Exists(abs))
            {
                // Restore the last editor buffer (which may hold unsaved edits), keeping the on-disk
                // content as the clean baseline so an unsaved script comes back marked modified.
                var disk = await File.ReadAllTextAsync(abs, CancellationToken.None);
                var buffer = e.ScratchText ?? disk;
                tab = NewTab(buffer, abs);
                tab.MarkSaved(disk);
                tab.CaretOffset = Math.Clamp(e.CaretOffset, 0, buffer.Length);
            }
            else
            {
                tab = NewTab(e.ScratchText ?? "");
                tab.CaretOffset = Math.Clamp(e.CaretOffset, 0, tab.Text.Length);
                if (e.ScratchName is { Length: > 0 } name) tab.DisplayName = name;
            }
            tab.ConnectionId = e.ConnectionId ?? DefaultConnectionId;
        }

        if (Tabs.Count == 0)
            NewTab("select 1;");

        var idx = session?.SelectedEditorIndex ?? 0;
        SelectedTab = Tabs[Math.Clamp(idx, 0, Tabs.Count - 1)];
    }

    partial void OnSelectedTabChanged(EditorTabViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedTabConnection));
        OnPropertyChanged(nameof(ActiveConnectionColor));
        OnPropertyChanged(nameof(SelectedTabDatabase));
        IsConnected = value?.ConnectionId is { } id && _sessions.TryGet(id) is not null;
        RefreshTabDatabases(value);
        WarmConnection(value);
    }

    // ---- Scripts -----------------------------------------------------------------------------

    /// <summary>The Scripts tree: folders (one level of subdirectories) then ungrouped root scripts.</summary>
    public ObservableCollection<object> ScriptNodes { get; } = new();

    /// <summary>Name filter for the Scripts tree (empty = show all).</summary>
    [ObservableProperty] private string _scriptFilter = "";

    partial void OnScriptFilterChanged(string value) => RefreshScripts();

    private void RefreshScripts()
    {
        Scripts.Clear();
        ScriptNodes.Clear();
        var dir = _project?.ScriptsDirectory;
        if (dir is null || !Directory.Exists(dir)) return;

        // Tabs with unsaved edits mark their backing script with a dot.
        var unsaved = Tabs.Where(t => t.IsDirty && t.ScriptPath is not null)
                          .Select(t => t.ScriptPath!)
                          .ToHashSet(StringComparer.Ordinal);
        var filter = ScriptFilter?.Trim() ?? "";
        bool Matches(string name) => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        ScriptItem Make(string path) => new(Path.GetFileName(path), path) { IsUnsaved = unsaved.Contains(path) };

        BuildScriptNodes(dir, ScriptNodes, filter, Matches, Make);
    }

    /// <summary>Recursively fill <paramref name="target"/> with subfolders (each nested) then scripts;
    /// returns how many scripts (matching the filter) are under this directory. Also feeds the flat
    /// <see cref="Scripts"/> list. Empty folders show when unfiltered; while filtering, a folder shows
    /// only if it has a matching descendant.</summary>
    private int BuildScriptNodes(string dir, System.Collections.Generic.IList<object> target,
        string filter, Func<string, bool> matches, Func<string, ScriptItem> make)
    {
        var total = 0;
        foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var folder = new ScriptFolderViewModel(Path.GetFileName(sub), sub) { IsExpanded = filter.Length > 0 };
            var n = BuildScriptNodes(sub, folder.Children, filter, matches, make);
            folder.Count = n;
            total += n;
            if (n > 0 || filter.Length == 0) target.Add(folder);
        }
        foreach (var path in Directory.EnumerateFiles(dir, "*.sql").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var item = make(path);
            Scripts.Add(item);
            if (matches(item.Name)) { target.Add(item); total++; }
        }
        return total;
    }

    /// <summary>Create a new folder under <paramref name="parentDir"/> (defaults to the scripts root).</summary>
    public void CreateScriptFolder(string name, string? parentDir = null)
    {
        var root = _project?.ScriptsDirectory;
        if (root is null || string.IsNullOrWhiteSpace(name)) return;
        var parent = parentDir ?? root;
        var safe = string.Concat(name.Trim().Split(Path.GetInvalidFileNameChars()));
        if (safe.Length == 0) return;
        try { Directory.CreateDirectory(Path.Combine(parent, safe)); }
        catch (Exception ex) { StatusText = $"Could not create folder: {ex.Message}"; return; }
        RefreshScripts();
        StatusText = $"Created folder {safe}.";
    }

    /// <summary>Create an empty .sql file in <paramref name="dir"/>; returns its path (null on clash/error).</summary>
    public async Task<string?> CreateScriptFileAsync(string dir, string name)
    {
        if (!Directory.Exists(dir) || string.IsNullOrWhiteSpace(name)) return null;
        if (!name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) name += ".sql";
        var path = Path.Combine(dir, name);
        if (File.Exists(path)) { StatusText = $"{name} already exists."; return null; }
        try { await File.WriteAllTextAsync(path, "", CancellationToken.None); }
        catch (Exception ex) { StatusText = $"Could not create script: {ex.Message}"; return null; }
        RefreshScripts();
        return path;
    }

    /// <summary>Move a script file into <paramref name="targetDir"/> (drag & drop between folders).</summary>
    public void MoveScript(string sourcePath, string targetDir)
    {
        if (!File.Exists(sourcePath) || !Directory.Exists(targetDir)) return;
        var dest = Path.Combine(targetDir, Path.GetFileName(sourcePath));
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(sourcePath), StringComparison.Ordinal)) return;
        if (File.Exists(dest)) { StatusText = $"{Path.GetFileName(dest)} already exists there."; return; }
        try { File.Move(sourcePath, dest); }
        catch (Exception ex) { StatusText = $"Move failed: {ex.Message}"; return; }

        foreach (var t in Tabs)
            if (string.Equals(t.ScriptPath, sourcePath, StringComparison.Ordinal)) t.ScriptPath = dest;
        RefreshScripts();
        StatusText = $"Moved {Path.GetFileName(sourcePath)}.";
    }

    /// <summary>Open a saved script: focus its existing tab, or load it into a new one.</summary>
    public async Task OpenScriptInNewTabAsync(string absolutePath)
    {
        var existing = Tabs.FirstOrDefault(t => string.Equals(t.ScriptPath, absolutePath, StringComparison.Ordinal));
        if (existing is not null) { SelectedTab = existing; return; }
        var text = await File.ReadAllTextAsync(absolutePath, CancellationToken.None);
        NewTab(text, absolutePath);
        StatusText = $"Opened {Path.GetFileName(absolutePath)}.";
    }

    public async Task LoadScriptIntoSelectedAsync(string absolutePath)
    {
        var text = await File.ReadAllTextAsync(absolutePath, CancellationToken.None);
        var tab = SelectedTab ?? NewTab();
        tab.Text = text;
        tab.ScriptPath = absolutePath;
        tab.MarkSaved(text);
        RefreshScripts();
        UpdateTitle();
        StatusText = $"Opened {Path.GetFileName(absolutePath)}.";
    }

    public async Task SaveSelectedScriptAsync(string absolutePath, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllTextAsync(absolutePath, text, CancellationToken.None);
        var tab = SelectedTab ?? NewTab(text);
        tab.Text = text;
        tab.ScriptPath = absolutePath;
        tab.MarkSaved(text);
        RefreshScripts();
        UpdateTitle();
        StatusText = $"Saved {Path.GetFileName(absolutePath)}.";
    }

    // ---- Rename ------------------------------------------------------------------------------

    /// <summary>Rename the selected tab: a scratch label, or the backing .sql file on disk.</summary>
    public async Task RenameTabAsync(EditorTabViewModel tab, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        if (tab.IsScratch) tab.DisplayName = newName.Trim();
        else if (tab.ScriptPath is { } path) await RenameScriptAsync(path, newName.Trim());
    }

    public async Task RenameScriptAsync(string oldPath, string newName)
    {
        var dir = Path.GetDirectoryName(oldPath);
        if (dir is null) return;
        if (!newName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) newName += ".sql";
        var newPath = Path.Combine(dir, newName);
        if (string.Equals(newPath, oldPath, StringComparison.Ordinal)) return;
        if (File.Exists(newPath)) { StatusText = $"A script named {newName} already exists."; return; }

        try { await Task.Run(() => File.Move(oldPath, newPath)); }
        catch (Exception ex) { StatusText = $"Rename failed: {ex.Message}"; return; }

        foreach (var t in Tabs)
            if (string.Equals(t.ScriptPath, oldPath, StringComparison.Ordinal)) t.ScriptPath = newPath;
        RefreshScripts();
        UpdateTitle();
        StatusText = $"Renamed to {newName}.";
    }

    // ---- Connections ------------------------------------------------------------------------

    private void RefreshConnections()
    {
        Connections.Clear();
        ServerNodes.Clear();
        if (_project is null) return;
        foreach (var c in _project.Manifest.Connections)
        {
            Connections.Add(c);
            ServerNodes.Add(new ServerNodeViewModel(c, _schemaBrowser));
        }
    }

    private ConnectionInfo? FindConnection(Guid id)
        => _project?.Manifest.Connections.FirstOrDefault(c => c.Id == id);

    private void ApplyConnectionDisplay(EditorTabViewModel tab)
    {
        var info = tab.ConnectionId is { } id ? FindConnection(id) : null;
        tab.ConnectionDisplay = info?.Name;
        tab.ConnectionColor = info?.EnvironmentColor;
        tab.DatabaseName ??= info?.Database; // default the active DB to the connection's own
    }

    /// <summary>The connection a tab actually runs against: its saved connection with the active
    /// database substituted in (the toolbar Database pill can point at another DB on the same server).
    /// Keeps the connection <c>Id</c> so the password (secret keyed by Id) is reused on connect.</summary>
    private ConnectionInfo? EffectiveConnection(EditorTabViewModel tab)
    {
        if (tab.ConnectionId is not { } id || FindConnection(id) is not { } info) return null;
        return tab.DatabaseName is { } db && !string.Equals(db, info.Database, StringComparison.Ordinal)
            ? info with { Database = db }
            : info;
    }

    public void SetTabConnection(EditorTabViewModel tab, Guid? id)
    {
        tab.ConnectionId = id;
        tab.DatabaseName = null;            // reset to the new connection's default DB
        ApplyConnectionDisplay(tab);
        if (ReferenceEquals(tab, SelectedTab))
        {
            OnPropertyChanged(nameof(SelectedTabConnection));
            OnPropertyChanged(nameof(ActiveConnectionColor));
            OnPropertyChanged(nameof(SelectedTabDatabase));
            IsConnected = id is { } cid && _sessions.TryGet(cid) is not null;
            RefreshTabDatabases(tab);
            WarmConnection(tab);
        }
    }

    // ---- Database selection (toolbar Database pill) ------------------------------------------

    /// <summary>Databases available on the selected tab's server (populates the Database pill).</summary>
    public ObservableCollection<string> TabDatabases { get; } = new();

    /// <summary>Two-way binding target for the Database pill; switching opens a session on that DB.
    /// Falls back to the connection's own database so the pill shows the DB actually in use even when
    /// no explicit override has been chosen.</summary>
    public string? SelectedTabDatabase
    {
        get
        {
            if (SelectedTab is not { } tab) return null;
            return tab.DatabaseName ?? (tab.ConnectionId is { } id ? FindConnection(id)?.Database : null);
        }
        set { if (SelectedTab is { } tab && value is not null) SetTabDatabase(tab, value); }
    }

    /// <summary>Point a tab at another database on its server. Reuses the connection's credentials;
    /// the session manager disposes the old DB's session and connects the new one on next use.</summary>
    public void SetTabDatabase(EditorTabViewModel tab, string database)
    {
        if (string.Equals(tab.DatabaseName, database, StringComparison.Ordinal)) return;
        tab.DatabaseName = database;
        if (ReferenceEquals(tab, SelectedTab))
        {
            OnPropertyChanged(nameof(SelectedTabDatabase));
            IsConnected = false;
            WarmConnection(tab);
        }
    }

    /// <summary>Load the server's database list into <see cref="TabDatabases"/> for the given tab.</summary>
    private async void RefreshTabDatabases(EditorTabViewModel? tab)
    {
        TabDatabases.Clear();
        if (tab?.ConnectionId is not { } id || FindConnection(id) is not { } info)
        {
            OnPropertyChanged(nameof(SelectedTabDatabase));
            return;
        }
        var current = tab.DatabaseName ?? info.Database;
        // Show the tab's current DB immediately (never leave the pill empty while offline), and
        // re-notify so the ComboBox selects it now that the item exists (the earlier notify from
        // OnSelectedTabChanged fired before this list was populated).
        TabDatabases.Add(current);
        OnPropertyChanged(nameof(SelectedTabDatabase));
        try
        {
            var dbs = await _schemaBrowser.GetDatabasesAsync(info, CancellationToken.None);
            if (!ReferenceEquals(tab, SelectedTab)) return;
            TabDatabases.Clear();
            foreach (var d in dbs) TabDatabases.Add(d);
            if (!TabDatabases.Contains(current)) TabDatabases.Insert(0, current); // keep the selection valid
            OnPropertyChanged(nameof(SelectedTabDatabase));
        }
        catch { /* offline — keep the single current-DB entry */ }
    }

    /// <summary>Fetch the stored password for the connection editor's edit mode (null if none).</summary>
    public async Task<string?> GetConnectionPasswordAsync(Guid id)
        => _secretStore is null ? null : await _secretStore.GetPasswordAsync(id, CancellationToken.None);

    /// <summary>Add or replace a connection in the manifest and its password in the secret store.</summary>
    public async Task AddOrUpdateConnectionAsync(ConnectionInfo conn, string? password)
    {
        if (_project is null) return;
        var list = _project.Manifest.Connections;
        var idx = list.FindIndex(c => c.Id == conn.Id);
        var networkChanged = true;
        if (idx >= 0) { networkChanged = !SameNetwork(list[idx], conn); list[idx] = conn; }
        else list.Add(conn);

        try
        {
            if (_secretStore is not null && password is not null)
            {
                if (password.Length == 0) await _secretStore.DeleteAsync(conn.Id, CancellationToken.None);
                else await _secretStore.SetPasswordAsync(conn.Id, password, CancellationToken.None);
            }
            await _projectStore.SaveAsync(_project, CancellationToken.None);
        }
        catch (Exception ex) { StatusText = $"Saved connection but secret/store failed: {ex.Message}"; }

        if (networkChanged) await _sessions.EvictAsync(conn.Id);
        DefaultConnectionId ??= conn.Id;
        RefreshConnections();
        foreach (var t in Tabs) if (t.ConnectionId == conn.Id) ApplyConnectionDisplay(t);
        OnPropertyChanged(nameof(SelectedTabConnection));
        StatusText = $"Saved connection '{conn.Name}'.";
    }

    public async Task DeleteConnectionAsync(Guid id)
    {
        if (_project is null) return;
        var removed = _project.Manifest.Connections.FirstOrDefault(c => c.Id == id);
        _project.Manifest.Connections.RemoveAll(c => c.Id == id);

        try
        {
            if (_secretStore is not null) await _secretStore.DeleteAsync(id, CancellationToken.None);
            await _projectStore.SaveAsync(_project, CancellationToken.None);
        }
        catch (Exception ex) { StatusText = $"Deleted connection but store failed: {ex.Message}"; }

        await _sessions.EvictAsync(id);
        foreach (var t in Tabs) if (t.ConnectionId == id) { t.ConnectionId = null; ApplyConnectionDisplay(t); }
        if (DefaultConnectionId == id) DefaultConnectionId = null;
        RefreshConnections();
        OnPropertyChanged(nameof(SelectedTabConnection));
        StatusText = removed is null ? "Connection deleted." : $"Deleted connection '{removed.Name}'.";
    }

    /// <summary>
    /// Refresh all cached metadata for a connection: drop the schema-browser's per-database readers,
    /// evict the live session so completion + editability reload its snapshot, reload the tree node,
    /// and re-warm the selected tab if it targets this connection.
    /// </summary>
    public async Task RefreshServerMetadataAsync(Guid connectionId)
    {
        await _schemaBrowser.InvalidateAsync(connectionId);
        await _sessions.EvictAsync(connectionId);

        var node = ServerNodes.FirstOrDefault(n => n.Connection.Id == connectionId);
        if (node is not null) await node.RefreshAsync();

        if (SelectedTab?.ConnectionId == connectionId) WarmConnection(SelectedTab);
        StatusText = "Schema metadata refreshed.";
    }

    private static bool SameNetwork(ConnectionInfo a, ConnectionInfo b)
        => a.ProviderId == b.ProviderId && a.Host == b.Host && a.Port == b.Port
           && a.Database == b.Database && a.User == b.User;

    /// <summary>Build a throwaway connection and test it (for the dialog's Test button); nothing is persisted.</summary>
    public async Task<bool> TestConnectionAsync(ConnectionInfo info, string? password, CancellationToken ct)
    {
        var provider = _providers.Get(info.ProviderId);
        var factory = provider.CreateConnectionFactory(info, password);
        try { return await factory.TestConnectionAsync(ct); }
        finally { await factory.DisposeAsync(); }
    }

    /// <summary>First-run convenience: seed a demo connection if the project has none, and target it.</summary>
    public async Task SeedDemoConnectionAsync(string host, int port, string database, string user, string password)
    {
        if (_project is null || _project.Manifest.Connections.Count > 0) return;
        var conn = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = $"{database} (local)",
            ProviderId = "postgres",
            Host = host, Port = port, Database = database, User = user,
            Environment = "local", EnvironmentColor = "#7AA89F",
        };
        await AddOrUpdateConnectionAsync(conn, password);
        DefaultConnectionId = conn.Id;
        foreach (var t in Tabs) if (t.ConnectionId is null) SetTabConnection(t, conn.Id);
        StatusText = $"Added demo connection '{conn.Name}'. Press F5 to run.";
    }

    /// <summary>Background connect + schema warm so completion is ready before the first Run. Quiet on failure.</summary>
    private async void WarmConnection(EditorTabViewModel? tab)
    {
        if (tab is null) return;
        var info = EffectiveConnection(tab);
        if (info is null) return;
        try
        {
            var session = await _sessions.GetOrConnectAsync(info, CancellationToken.None);
            if (ReferenceEquals(SelectedTab, tab)) IsConnected = true;
            var snapshot = await _sessions.EnsureSchemaAsync(session, CancellationToken.None);
            if (ReferenceEquals(SelectedTab, tab))
                StatusText = snapshot is null
                    ? $"Connected to {info.Name}."
                    : $"Connected to {info.Name} · {snapshot.Tables.Count} tables.";
        }
        catch (ConnectionFailedException) { /* Run will surface the error explicitly */ }
        catch { /* completion warming must never disrupt the UI */ }
    }

    // ---- Execution ---------------------------------------------------------------------------

    /// <summary>Default page size: first page and each "load more" fetch this many rows.</summary>
    public const int PageSize = 100;

    /// <summary>
    /// Confirm a write/destructive statement before it runs against a guarded connection. Set by the
    /// view to show a dialog; args are the target connection and the risky verbs found. Returns true to
    /// proceed. When unset, guarded writes proceed (headless/tests) — the guard is a UI affordance.
    /// </summary>
    public Func<ConnectionInfo, IReadOnlyList<string>, Task<bool>>? ConfirmDangerousWrite { get; set; }

    /// <summary>Execute SQL for the selected tab against that tab's connection; record it in the log.</summary>
    public async Task ExecuteAsync(string sql)
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(sql)) return;
        var tab = SelectedTab;
        if (tab is null) { StatusText = "No editor."; return; }
        if (tab.ConnectionId is null) { StatusText = "This tab has no connection — pick one."; return; }
        var info = EffectiveConnection(tab);
        if (info is null) { StatusText = "Connection no longer exists."; return; }

        // Production write-guard: confirm before writing data / altering schema on a guarded connection.
        if (info.RequireWriteConfirmation && ConfirmDangerousWrite is { } confirm)
        {
            var risky = Squirrel.Sql.WriteGuard.FindRiskyStatements(sql);
            if (risky.Count > 0 && !await confirm(info, risky))
            {
                StatusText = "Cancelled — write not confirmed.";
                return;
            }
        }

        IsBusy = true;
        _executionCts = new CancellationTokenSource();
        var ct = _executionCts.Token;
        try
        {
            ConnectionSession session;
            try { session = await _sessions.GetOrConnectAsync(info, ct); }
            catch (ConnectionFailedException ex) { IsConnected = false; StatusText = ex.Message; return; }

            IsConnected = true;
            _ = _sessions.EnsureSchemaAsync(session, CancellationToken.None); // warm completion, don't block Run

            StatusText = "Running…";
            // Fetch only the first page; a single row-returning statement is then pageable and
            // "load more"/"count" run against the original sql. Multi-statement runs are capped
            // per set at PageSize and shown truncated (no paging — see the pageable gate below).
            var results = await session.Executor.ExecuteAsync(sql, new QueryOptions { MaxRows = PageSize }, ct);
            tab.SetFreshResults(ResultSetBuilder.BuildResultSets(results, sql, session.Snapshot));
            LogExecution(info, sql, results);
            var summary = ResultSetBuilder.DescribeResults(results);
            // On success, lead with the connection so the status bar reads e.g. "pagila (local) · 88 ms".
            StatusText = results.Any(r => !r.Success) ? summary : $"{info.Name} · {summary}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Query cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Execution error: {ex.Message}";
        }
        finally
        {
            _executionCts.Dispose();
            _executionCts = null;
            IsBusy = false;
        }
    }

    /// <summary>Cancel the in-flight query, if any (Esc / the Run button while busy).</summary>
    public void CancelExecution()
    {
        try { _executionCts?.Cancel(); }
        catch (ObjectDisposedException) { /* completed between the null-check and Cancel */ }
    }

    /// <summary>Append the next page to a pageable result set (infinite-scroll "load more").</summary>
    public async Task LoadMoreAsync(ResultSetViewModel rs)
    {
        if (IsBusy || !rs.IsPageable || rs.SourceSql is null || !rs.HasMore) return;
        if (ResolveLiveSession() is not { } session) return;

        IsBusy = true;
        _executionCts = new CancellationTokenSource();
        var ct = _executionCts.Token;
        try
        {
            var page = await session.Executor.ExecutePageAsync(rs.SourceSql, rs.Loaded, PageSize, ct);
            rs.AppendPage(page.Rows, page.RowCount == PageSize);
            // No status update: auto-load fires on scroll and the count lives on the meta row.
        }
        catch (OperationCanceledException) { StatusText = "Load cancelled."; }
        catch (Exception ex) { StatusText = $"Load more failed: {ex.Message}"; }
        finally { _executionCts.Dispose(); _executionCts = null; IsBusy = false; }
    }

    /// <summary>Fill in the total row count for a pageable result set (the [Count] action).</summary>
    public async Task CountTotalAsync(ResultSetViewModel rs)
    {
        if (IsBusy || !rs.IsPageable || rs.SourceSql is null) return;
        if (ResolveLiveSession() is not { } session) return;

        IsBusy = true;
        _executionCts = new CancellationTokenSource();
        var ct = _executionCts.Token;
        try
        {
            rs.TotalCount = await session.Executor.CountAsync(rs.SourceSql, ct);
            StatusText = rs.TotalCount is not null ? "Counted total." : "Count unavailable for this query.";
        }
        catch (OperationCanceledException) { StatusText = "Count cancelled."; }
        catch (Exception ex) { StatusText = $"Count failed: {ex.Message}"; }
        finally { _executionCts.Dispose(); _executionCts = null; IsBusy = false; }
    }

    /// <summary>The already-connected session for the selected tab (paging runs post-execute, so
    /// the connection is live). Null — with a status set — if the tab lost its connection.</summary>
    private ConnectionSession? ResolveLiveSession()
    {
        if (SelectedTab?.ConnectionId is { } id && _sessions.TryGet(id) is { } session) return session;
        StatusText = "Not connected.";
        return null;
    }

    // ---- FK navigation -----------------------------------------------------------------------

    /// <summary>Navigate a foreign-key cell in place: run the lookup on the current tab's connection
    /// and swap the displayed result for the referenced row, stacking the previous result so Back can
    /// return to it. The query is never surfaced in the editor.</summary>
    public async Task NavigateForeignKeyAsync(ResultSetViewModel rs, int columnIndex, object?[] row)
    {
        if (IsBusy) return;
        if (columnIndex < 0 || columnIndex >= row.Length) return;
        if (row[columnIndex] is null) { StatusText = "Empty key — nothing to navigate to."; return; }
        if (SelectedTab is not { } tab) return;
        if (SnapshotForSelectedTab() is not { } snapshot) { StatusText = "Schema not loaded yet."; return; }
        if (ForeignKeyResolver.Resolve(snapshot, rs.Columns, columnIndex) is not { } target)
        { StatusText = "Not a foreign key."; return; }
        if (ResolveLiveSession() is not { } session) return;

        var sql = ResultEditModel.BuildForeignKeySelect(target, row);
        IsBusy = true;
        _executionCts = new CancellationTokenSource();
        var ct = _executionCts.Token;
        try
        {
            StatusText = "Opening referenced row…";
            var results = await session.Executor.ExecuteAsync(sql, new QueryOptions { MaxRows = PageSize }, ct);
            tab.PushResults(ResultSetBuilder.BuildResultSets(results, sql, session.Snapshot));
            StatusText = ResultSetBuilder.DescribeResults(results);
        }
        catch (OperationCanceledException) { StatusText = "Navigation cancelled."; }
        catch (Exception ex) { StatusText = $"Navigation failed: {ex.Message}"; }
        finally { _executionCts.Dispose(); _executionCts = null; IsBusy = false; }
    }

    // ---- Inline editing (Phase 3) ------------------------------------------------------------
    // The pure DML/edit logic lives in Results/ResultEditModel; these methods own the connection,
    // transaction, and status-bar concerns.

    /// <summary>Apply a result set's pending edits/inserts/deletes in one transaction, then update the
    /// affected rows in place (no reload — paged-in rows and scroll are preserved).</summary>
    public async Task SaveChangesAsync(ResultSetViewModel rs)
    {
        if (IsBusy) return;
        if (rs.EditTarget is not { } target || !rs.HasPendingChanges) return;
        if (ResolveLiveSession() is not { } session) return;

        var changes = ResultEditModel.BuildPendingChanges(rs, target);
        if (changes.Count == 0) { rs.ClearPending(); return; }

        IsBusy = true;
        _executionCts = new CancellationTokenSource();
        var ct = _executionCts.Token;
        try
        {
            StatusText = $"Saving {changes.Count} change(s)…";
            var results = await session.Executor.ExecuteWriteAsync(changes.Select(c => c.Command).ToList(), ct);
            if (results.FirstOrDefault(r => !r.Success) is { } failed)
            { StatusText = $"Save failed: {failed.Error?.Message}"; return; } // rows/pending untouched

            ResultEditModel.ApplySavedChanges(rs, target, changes, results);
            StatusText = $"Saved {changes.Count} change(s).";
        }
        catch (OperationCanceledException) { StatusText = "Save cancelled."; }
        catch (Exception ex) { StatusText = $"Save failed: {ex.Message}"; }
        finally { _executionCts.Dispose(); _executionCts = null; IsBusy = false; }
    }

    /// <summary>Discard all pending changes in place (restore edited cells, drop new rows, un-mark deletes).</summary>
    public Task DiscardChangesAsync(ResultSetViewModel rs)
    {
        if (rs.HasPendingChanges) { rs.RevertPending(); StatusText = "Changes discarded."; }
        return Task.CompletedTask;
    }

    /// <summary>Render the write statements a save would run, values inlined, wrapped in a transaction.
    /// Null when there's nothing pending. For preview only — the real save uses parameters.</summary>
    public string? PreviewChanges(ResultSetViewModel rs)
    {
        if (rs.EditTarget is not { } target || !rs.HasPendingChanges) return null;
        var changes = ResultEditModel.BuildPendingChanges(rs, target);
        if (changes.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("begin;");
        foreach (var c in changes) sb.Append("  ").Append(ResultEditModel.InlineParameters(c.Command)).AppendLine(";");
        sb.Append("commit;");
        return sb.ToString();
    }

    /// <summary>One generated write statement, tagged INSERT/UPDATE/DELETE for the floating script panel.</summary>
    public sealed record PendingStatement(string Kind, string Sql);

    /// <summary>The pending write statements, one per dirty row, values inlined and kind-tagged (for the
    /// color-coded pending-changes script panel). Empty when there's nothing pending. Preview only.</summary>
    public IReadOnlyList<PendingStatement> PreviewChangeStatements(ResultSetViewModel rs)
    {
        if (rs.EditTarget is not { } target || !rs.HasPendingChanges) return Array.Empty<PendingStatement>();
        return ResultEditModel.BuildPendingChanges(rs, target)
            .Select(c => new PendingStatement(c.Kind.ToString().ToUpperInvariant(), ResultEditModel.InlineParameters(c.Command) + ";"))
            .ToList();
    }

    /// <summary>Schema for the selected tab's connection (drives completion); null when not yet loaded.</summary>
    public ISchemaSnapshot? SnapshotForSelectedTab()
        => SelectedTab?.ConnectionId is { } id ? _sessions.TryGet(id)?.Snapshot : null;

    public Task<IReadOnlyList<QueryLogEntry>> SearchHistoryAsync(string? text, CancellationToken ct)
        => _queryLog.SearchAsync(new QueryLogQuery { Text = text }, ct);

    // History logs one entry per submitted run; a multi-statement run aggregates its sets.
    private void LogExecution(ConnectionInfo info, string sql, IReadOnlyList<QueryResult> results) => _queryLog.Append(new QueryLogEntry
    {
        ExecutedAt = DateTimeOffset.UtcNow,
        ProviderId = info.ProviderId,
        ConnectionName = info.Name,
        Database = info.Database,
        SqlText = sql,
        Duration = results[^1].Duration,
        RowCount = results.Sum(r => r.RowCount),
        Success = results.All(r => r.Success),
        ErrorMessage = results.FirstOrDefault(r => !r.Success)?.Error?.Message,
    });

    // ---- Session persistence (synchronous; safe on the close path) ---------------------------

    public void SaveWorkspace()
    {
        if (_project is null) return;
        try { _sessionStore.Save(_project.Directory, BuildSession()); }
        catch { /* best-effort on shutdown */ }
    }

    private SessionState BuildSession()
    {
        var editors = Tabs.Select(t => new OpenEditor
        {
            ScriptPath = t.ScriptPath is not null && _project is not null
                ? Path.GetRelativePath(_project.Directory, t.ScriptPath)
                : null,
            ScratchText = t.Text,
            ScratchName = t.IsScratch ? t.DisplayName : null,
            CaretOffset = t.CaretOffset,
            ConnectionId = t.ConnectionId,
        }).ToList();

        return new SessionState
        {
            ActiveConnectionId = SelectedTab?.ConnectionId ?? DefaultConnectionId,
            OpenEditors = editors,
            SelectedEditorIndex = SelectedTab is null ? 0 : Math.Max(0, Tabs.IndexOf(SelectedTab)),
            LastOpenedUtc = DateTime.UtcNow.ToString("o"),
            SidePaneOpen = SidePaneOpen,
            SidePaneWidth = SidePaneWidth,
            ResultsViewMode = ResultsViewMode,
        };
    }

    private void UpdateTitle()
        => Title = _project is null ? "Squirrel" : $"Squirrel — {_project.Manifest.Name}";
}
