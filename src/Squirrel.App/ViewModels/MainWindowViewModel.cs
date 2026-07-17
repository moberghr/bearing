using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Squirrel.App.Connections;
using Squirrel.Core.Data;
using Squirrel.Core.Logging;
using Squirrel.Core.Schema;
using Squirrel.Core.Workspace;

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
    [ObservableProperty] private double _sidePaneWidth = 260;

    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();
    [ObservableProperty] private EditorTabViewModel? _selectedTab;

    /// <summary>The project's named connections (mirror of the manifest), shown in the side pane.</summary>
    public ObservableCollection<ConnectionInfo> Connections { get; } = new();

    /// <summary>Saved scripts under the project's scripts/ folder, shown in the side pane.</summary>
    public ObservableCollection<ScriptItem> Scripts { get; } = new();

    public ObservableCollection<RecentProjectItem> RecentProjects { get; } = new();

    /// <summary>Connection new tabs fall back to when there is no active tab to inherit from.</summary>
    public Guid? DefaultConnectionId { get; private set; }

    public string? ProjectDirectory => _project?.Directory;
    public string? ScriptsDirectory => _project?.ScriptsDirectory;
    public string? CurrentProjectName => _project?.Manifest.Name;

    /// <summary>Two-way binding target for the per-tab connection picker.</summary>
    public ConnectionInfo? SelectedTabConnection
    {
        get => SelectedTab?.ConnectionId is { } id ? FindConnection(id) : null;
        set { if (SelectedTab is { } tab) SetTabConnection(tab, value?.Id); }
    }

    public void AttachSecretStore(ISecretStore secretStore) => _secretStore = secretStore;

    /// <summary>Dispose all live connections; safe to call fire-and-forget from the close path.</summary>
    public ValueTask DisposeSessionsAsync() => _sessions.DisposeAsync();

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
            DefaultConnectionId = session?.ActiveConnectionId
                                  ?? _project.Manifest.Connections.FirstOrDefault()?.Id;

            await RestoreTabsAsync(session);
            foreach (var tab in Tabs) ApplyConnectionDisplay(tab);

            UpdateTitle();
            OnPropertyChanged(nameof(ProjectDirectory));
            OnPropertyChanged(nameof(CurrentProjectName));
            StatusText = $"Project '{_project.Manifest.Name}'. " +
                         (_secretStore?.IsSecure == true ? "Secrets: OS keychain." : "Secrets: local file.");
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
        IsConnected = false;
        DefaultConnectionId = null;
        Tabs.Clear();
        await InitializeAsync(projectDirectory);
    }

    public async Task NewProjectAsync(string projectDirectory, string name)
    {
        SaveWorkspace();
        await _sessions.DisposeAsync();
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
        IsConnected = value?.ConnectionId is { } id && _sessions.TryGet(id) is not null;
        WarmConnection(value);
    }

    // ---- Scripts -----------------------------------------------------------------------------

    private void RefreshScripts()
    {
        Scripts.Clear();
        var dir = _project?.ScriptsDirectory;
        if (dir is null || !Directory.Exists(dir)) return;
        foreach (var path in Directory.EnumerateFiles(dir, "*.sql").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            Scripts.Add(new ScriptItem(Path.GetFileName(path), path));
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
        if (_project is null) return;
        foreach (var c in _project.Manifest.Connections) Connections.Add(c);
    }

    private ConnectionInfo? FindConnection(Guid id)
        => _project?.Manifest.Connections.FirstOrDefault(c => c.Id == id);

    private void ApplyConnectionDisplay(EditorTabViewModel tab)
    {
        var info = tab.ConnectionId is { } id ? FindConnection(id) : null;
        tab.ConnectionDisplay = info?.Name;
        tab.ConnectionColor = info?.EnvironmentColor;
    }

    public void SetTabConnection(EditorTabViewModel tab, Guid? id)
    {
        tab.ConnectionId = id;
        ApplyConnectionDisplay(tab);
        if (ReferenceEquals(tab, SelectedTab))
        {
            OnPropertyChanged(nameof(SelectedTabConnection));
            IsConnected = id is { } cid && _sessions.TryGet(cid) is not null;
            WarmConnection(tab);
        }
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
            Environment = "local", EnvironmentColor = "#3FB950",
        };
        await AddOrUpdateConnectionAsync(conn, password);
        DefaultConnectionId = conn.Id;
        foreach (var t in Tabs) if (t.ConnectionId is null) SetTabConnection(t, conn.Id);
        StatusText = $"Added demo connection '{conn.Name}'. Press F5 to run.";
    }

    /// <summary>Background connect + schema warm so completion is ready before the first Run. Quiet on failure.</summary>
    private async void WarmConnection(EditorTabViewModel? tab)
    {
        if (tab?.ConnectionId is not { } id) return;
        var info = FindConnection(id);
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

    /// <summary>Execute SQL for the selected tab against that tab's connection; record it in the log.</summary>
    public async Task ExecuteAsync(string sql)
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(sql)) return;
        var tab = SelectedTab;
        if (tab is null) { StatusText = "No editor."; return; }
        if (tab.ConnectionId is not { } id) { StatusText = "This tab has no connection — pick one."; return; }
        var info = FindConnection(id);
        if (info is null) { StatusText = "Connection no longer exists."; return; }

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
            var results = await session.Executor.ExecuteAsync(sql, new QueryOptions(), ct);
            tab.LastResults = results;
            LogExecution(info, sql, results);
            StatusText = DescribeResults(results);
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

    /// <summary>Schema for the selected tab's connection (drives completion); null when not yet loaded.</summary>
    public ISchemaSnapshot? SnapshotForSelectedTab()
        => SelectedTab?.ConnectionId is { } id ? _sessions.TryGet(id)?.Snapshot : null;

    public Task<IReadOnlyList<QueryLogEntry>> SearchHistoryAsync(string? text, CancellationToken ct)
        => _queryLog.SearchAsync(new QueryLogQuery { Text = text }, ct);

    /// <summary>One-line status for a run: the single set's shape, or an N-set summary.</summary>
    private static string DescribeResults(IReadOnlyList<QueryResult> results)
    {
        var firstError = results.FirstOrDefault(r => !r.Success);
        if (firstError is not null)
            return $"Error{(firstError.Error?.SqlState is { } s ? $" [{s}]" : "")}: {firstError.Error?.Message}";

        if (results.Count == 1)
        {
            var r = results[0];
            return $"{r.RowCount} row(s) in {r.Duration.TotalMilliseconds:0} ms"
                   + (r.Truncated ? " (truncated)" : "");
        }

        var totalRows = results.Sum(r => r.RowCount);
        var elapsed = results[^1].Duration.TotalMilliseconds;
        var truncated = results.Any(r => r.Truncated) ? " (truncated)" : "";
        return $"{results.Count} result sets · {totalRows} row(s) in {elapsed:0} ms{truncated}";
    }

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
        };
    }

    private void UpdateTitle()
        => Title = _project is null ? "Squirrel" : $"Squirrel — {_project.Manifest.Name}";
}
