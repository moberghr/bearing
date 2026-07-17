using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Squirrel.Core.Data;
using Squirrel.Core.Logging;
using Squirrel.Core.Schema;
using Squirrel.Core.Workspace;

namespace Squirrel.App.ViewModels;

/// <summary>
/// Shell view-model: owns the open project, its editor tabs, the active connection (password via
/// the OS keychain), query execution + logging, and the completion schema. Switching projects
/// saves the current session and restores the target project's tabs.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IProviderRegistry _providers;
    private readonly IProjectStore _projectStore;
    private readonly ISessionStore _sessionStore;
    private readonly IQueryLog _queryLog;
    private readonly IRecentProjects _recentProjects;
    private ISecretStore? _secretStore;

    private IDbConnectionFactory? _factory;
    private IQueryExecutor? _executor;
    private IMetadataReader? _metadata;

    private Project? _project;
    private Guid? _activeConnectionId;
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

        // Demo defaults target the local pagila container; password is a first-run convenience only.
        Host = "localhost";
        Port = "5433";
        Database = "pagila";
        User = "postgres";
        Password = "squirrel";
    }

    // Connection fields
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "5432";
    [ObservableProperty] private string _database = "";
    [ObservableProperty] private string _user = "";
    [ObservableProperty] private string _password = "";

    [ObservableProperty] private string _statusText = "Not connected.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private ISchemaSnapshot? _currentSnapshot;
    [ObservableProperty] private string _title = "Squirrel";

    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();
    [ObservableProperty] private EditorTabViewModel? _selectedTab;

    public ObservableCollection<string> RecentProjects { get; } = new();

    public string? ProjectDirectory => _project?.Directory;
    public string? ScriptsDirectory => _project?.ScriptsDirectory;

    public void AttachSecretStore(ISecretStore secretStore) => _secretStore = secretStore;

    // ---- Project lifecycle -------------------------------------------------------------------

    public async Task InitializeAsync(string projectDirectory)
    {
        try
        {
            _project = await OpenOrCreate(projectDirectory);
            await _recentProjects.AddAsync(_project.Directory, CancellationToken.None);
            await RefreshRecentAsync();

            var session = await _sessionStore.LoadAsync(_project.Directory, CancellationToken.None);
            await RestoreTabsAsync(session);
            await RestoreConnectionAsync(session);

            UpdateTitle();
            StatusText = $"Project '{_project.Manifest.Name}'. " +
                         (_secretStore?.IsSecure == true ? "Secrets: OS keychain." : "Secrets: local file.");
        }
        catch (Exception ex)
        {
            StatusText = $"Project load error: {ex.Message}";
            if (Tabs.Count == 0) NewTab();
        }
    }

    /// <summary>Save the current session, then switch to another project directory.</summary>
    public async Task OpenProjectAsync(string projectDirectory)
    {
        if (_project is not null && string.Equals(Path.GetFullPath(projectDirectory), _project.Directory, StringComparison.Ordinal))
            return;

        SaveWorkspace(); // persist the outgoing project's tabs
        await DisconnectAsync();
        Tabs.Clear();
        await InitializeAsync(projectDirectory);
    }

    public async Task NewProjectAsync(string projectDirectory, string name)
    {
        SaveWorkspace();
        await DisconnectAsync();
        Tabs.Clear();
        _project = await _projectStore.CreateAsync(projectDirectory, name, CancellationToken.None);
        await _recentProjects.AddAsync(_project.Directory, CancellationToken.None);
        await RefreshRecentAsync();
        NewTab();
        UpdateTitle();
        StatusText = $"Created project '{name}'.";
    }

    private async Task RefreshRecentAsync()
    {
        var list = await _recentProjects.ListAsync(CancellationToken.None);
        RecentProjects.Clear();
        foreach (var p in list) RecentProjects.Add(p);
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
        var tab = new EditorTabViewModel($"Scratch {++_scratchCounter}", text, scriptPath);
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
            if (abs is not null && File.Exists(abs))
            {
                var text = await File.ReadAllTextAsync(abs, CancellationToken.None);
                var tab = NewTab(text, abs);
                tab.CaretOffset = Math.Clamp(e.CaretOffset, 0, text.Length);
            }
            else
            {
                var tab = NewTab(e.ScratchText ?? "");
                tab.CaretOffset = Math.Clamp(e.CaretOffset, 0, tab.Text.Length);
            }
        }

        if (Tabs.Count == 0)
            NewTab("select 1;");

        var idx = session?.SelectedEditorIndex ?? 0;
        SelectedTab = Tabs[Math.Clamp(idx, 0, Tabs.Count - 1)];
    }

    // ---- Scripts -----------------------------------------------------------------------------

    public async Task LoadScriptIntoSelectedAsync(string absolutePath)
    {
        var text = await File.ReadAllTextAsync(absolutePath, CancellationToken.None);
        var tab = SelectedTab ?? NewTab();
        tab.Text = text;
        tab.ScriptPath = absolutePath;
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
        UpdateTitle();
        StatusText = $"Saved {Path.GetFileName(absolutePath)}.";
    }

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
            CaretOffset = t.CaretOffset,
            ConnectionId = _activeConnectionId,
        }).ToList();

        return new SessionState
        {
            ActiveConnectionId = _activeConnectionId,
            OpenEditors = editors,
            SelectedEditorIndex = SelectedTab is null ? 0 : Math.Max(0, Tabs.IndexOf(SelectedTab)),
            LastOpenedUtc = DateTime.UtcNow.ToString("o"),
        };
    }

    // ---- Connection + execution --------------------------------------------------------------

    public async Task ConnectAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsConnected = false;
        try
        {
            var provider = _providers.Get("postgres");
            var conn = UpsertConnection();

            if (_factory is not null) await _factory.DisposeAsync();
            CurrentSnapshot = null;
            _factory = provider.CreateConnectionFactory(conn, Password);
            var ok = await _factory.TestConnectionAsync(CancellationToken.None);
            _executor = provider.CreateQueryExecutor(_factory);
            _metadata = provider.CreateMetadataReader(_factory);
            IsConnected = ok;

            if (ok)
            {
                _activeConnectionId = conn.Id;
                await PersistConnectionAsync(conn, Password);
                StatusText = $"Connected to {Host}:{Port}/{Database}.";
                _ = LoadSchemaAsync();
            }
            else
            {
                StatusText = "Connection failed.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Connect error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DisconnectAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        _factory = null;
        _executor = null;
        _metadata = null;
        _activeConnectionId = null;
        CurrentSnapshot = null;
        IsConnected = false;
    }

    public async Task LoadSchemaAsync()
    {
        if (_metadata is null) return;
        try
        {
            StatusText = "Loading schema…";
            var snapshot = await _metadata.LoadSnapshotAsync(Database, CancellationToken.None);
            CurrentSnapshot = snapshot;
            StatusText = $"Connected to {Host}:{Port}/{Database} · {snapshot.Tables.Count} tables.";
        }
        catch (Exception ex)
        {
            StatusText = $"Schema load error: {ex.Message}";
        }
    }

    /// <summary>Execute SQL for the selected tab and record it in the query log.</summary>
    public async Task ExecuteAsync(string sql)
    {
        if (IsBusy) return;
        if (_executor is null) { StatusText = "Connect first."; return; }
        if (string.IsNullOrWhiteSpace(sql)) return;
        var tab = SelectedTab;

        IsBusy = true;
        try
        {
            var result = await _executor.ExecuteAsync(sql, new QueryOptions(), CancellationToken.None);
            if (tab is not null) tab.LastResult = result;
            LogExecution(sql, result);
            StatusText = result.Success
                ? $"{result.RowCount} row(s) in {result.Duration.TotalMilliseconds:0} ms"
                  + (result.Truncated ? " (truncated)" : "")
                : $"Error{(result.Error?.SqlState is { } s ? $" [{s}]" : "")}: {result.Error?.Message}";
        }
        catch (Exception ex)
        {
            StatusText = $"Execution error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task<IReadOnlyList<QueryLogEntry>> SearchHistoryAsync(string? text, CancellationToken ct)
        => _queryLog.SearchAsync(new QueryLogQuery { Text = text }, ct);

    private void LogExecution(string sql, QueryResult result) => _queryLog.Append(new QueryLogEntry
    {
        ExecutedAt = DateTimeOffset.UtcNow,
        ProviderId = "postgres",
        ConnectionName = $"{Host}/{Database}",
        Database = Database,
        SqlText = sql,
        Duration = result.Duration,
        RowCount = result.RowCount,
        Success = result.Success,
        ErrorMessage = result.Error?.Message,
    });

    // ---- helpers -----------------------------------------------------------------------------

    private async Task RestoreConnectionAsync(SessionState? session)
    {
        var conn = _project?.Manifest.Connections.FirstOrDefault(c => c.Id == session?.ActiveConnectionId)
                   ?? _project?.Manifest.Connections.FirstOrDefault();
        if (conn is null) return;

        Host = conn.Host;
        Port = conn.Port.ToString();
        Database = conn.Database;
        User = conn.User;
        _activeConnectionId = conn.Id;
        Password = _secretStore is null
            ? ""
            : await _secretStore.GetPasswordAsync(conn.Id, CancellationToken.None) ?? "";
    }

    private ConnectionInfo UpsertConnection()
    {
        var port = int.TryParse(Port, out var p) ? p : 5432;
        var existing = _project?.Manifest.Connections.FirstOrDefault(c =>
            c.Host == Host && c.Port == port && c.Database == Database && c.User == User);
        if (existing is not null) return existing;

        var created = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = $"{Host}/{Database}",
            ProviderId = "postgres",
            Host = Host, Port = port, Database = Database, User = User,
        };
        _project?.Manifest.Connections.Add(created);
        return created;
    }

    private async Task PersistConnectionAsync(ConnectionInfo conn, string password)
    {
        if (_project is null) return;
        try
        {
            if (_secretStore is not null && !string.IsNullOrEmpty(password))
                await _secretStore.SetPasswordAsync(conn.Id, password, CancellationToken.None);
            await _projectStore.SaveAsync(_project, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = $"Saved connection but secret/store failed: {ex.Message}";
        }
    }

    private void UpdateTitle()
        => Title = _project is null ? "Squirrel" : $"Squirrel — {_project.Manifest.Name}";
}
