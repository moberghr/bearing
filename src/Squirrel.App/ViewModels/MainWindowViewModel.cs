using System;
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
/// Shell view-model: owns the open project + session, connects to Postgres (password via the OS
/// keychain), runs SQL, loads the schema for completion, and restores/saves "where I left off".
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IProviderRegistry _providers;
    private readonly IProjectStore _projectStore;
    private readonly ISessionStore _sessionStore;
    private readonly IQueryLog _queryLog;

    // Attached asynchronously after startup (probing the OS keychain must not block the UI thread).
    private ISecretStore? _secretStore;

    private IDbConnectionFactory? _factory;
    private IQueryExecutor? _executor;
    private IMetadataReader? _metadata;

    private Project? _project;
    private Guid? _activeConnectionId;

    public MainWindowViewModel(
        IProviderRegistry providers,
        IProjectStore projectStore,
        ISessionStore sessionStore,
        IQueryLog queryLog,
        ISecretStore? secretStore = null)
    {
        _providers = providers;
        _projectStore = projectStore;
        _sessionStore = sessionStore;
        _queryLog = queryLog;
        _secretStore = secretStore;

        // Demo defaults target the local pagila container. The password is only a first-run
        // convenience — once you connect, it is saved to the OS keychain and restored from there
        // (a saved project/connection overrides all of these during InitializeAsync).
        Host = "localhost";
        Port = "5433";
        Database = "pagila";
        User = "postgres";
        Password = "squirrel";
        Sql = "select film_id, title, release_year\nfrom film\norder by film_id\nlimit 100;";
        StatusText = "Not connected.";
    }

    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "5432";
    [ObservableProperty] private string _database = "";
    [ObservableProperty] private string _user = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _sql = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private QueryResult? _lastResult;
    [ObservableProperty] private ISchemaSnapshot? _currentSnapshot;

    public string? ProjectDirectory => _project?.Directory;

    /// <summary>Attach the secret store once it has been resolved off the UI thread.</summary>
    public void AttachSecretStore(ISecretStore secretStore) => _secretStore = secretStore;

    /// <summary>Open (or create) the project directory and restore the last session.</summary>
    public async Task InitializeAsync(string projectDirectory)
    {
        try
        {
            _project = await OpenOrCreate(projectDirectory);

            var session = await _sessionStore.LoadAsync(projectDirectory, CancellationToken.None);
            var scratch = session?.OpenEditors.FirstOrDefault(e => e.ScratchText is not null);
            if (scratch?.ScratchText is { } text) Sql = text;

            // Restore connection fields (+ password from keychain) from the active/first connection.
            var conn = _project.Manifest.Connections.FirstOrDefault(c => c.Id == session?.ActiveConnectionId)
                       ?? _project.Manifest.Connections.FirstOrDefault();
            if (conn is not null)
                await ApplyConnectionAsync(conn);

            StatusText = $"Project '{_project.Manifest.Name}'. " +
                         (_secretStore?.IsSecure == true ? "Secrets: OS keychain." : "Secrets: local file.");
        }
        catch (Exception ex)
        {
            StatusText = $"Project load error: {ex.Message}";
        }
    }

    /// <summary>Persist the current editor buffer + active connection as the session (synchronous;
    /// safe to call from a window-close handler on the UI thread without deadlocking).</summary>
    public void SaveWorkspace(string editorText, int caretOffset)
    {
        if (_project is null) return;
        try { _sessionStore.Save(_project.Directory, BuildSession(editorText, caretOffset)); }
        catch { /* best-effort on shutdown */ }
    }

    private SessionState BuildSession(string editorText, int caretOffset) => new()
    {
        ActiveConnectionId = _activeConnectionId,
        LastOpenedUtc = DateTime.UtcNow.ToString("o"),
        OpenEditors =
        {
            new OpenEditor { ScratchText = editorText, CaretOffset = caretOffset, ConnectionId = _activeConnectionId },
        },
    };

    public async Task ConnectAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsConnected = false;
        try
        {
            var provider = _providers.Get("postgres");
            var conn = UpsertConnection();
            var info = ToConnectionInfo(conn);

            if (_factory is not null) await _factory.DisposeAsync();
            CurrentSnapshot = null;
            _factory = provider.CreateConnectionFactory(info, Password);
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

    public async Task ExecuteAsync(string sql)
    {
        if (IsBusy) return;
        if (_executor is null) { StatusText = "Connect first."; return; }
        if (string.IsNullOrWhiteSpace(sql)) return;

        IsBusy = true;
        try
        {
            var result = await _executor.ExecuteAsync(sql, new QueryOptions(), CancellationToken.None);
            LastResult = result;
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

    /// <summary>Search the on-disk query history (for a future history panel / command).</summary>
    public Task<IReadOnlyList<QueryLogEntry>> SearchHistoryAsync(string? text, CancellationToken ct)
        => _queryLog.SearchAsync(new QueryLogQuery { Text = text }, ct);

    private void LogExecution(string sql, QueryResult result)
    {
        _queryLog.Append(new QueryLogEntry
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
    }

    // ---- helpers -----------------------------------------------------------------------------

    private async Task<Project> OpenOrCreate(string dir)
    {
        try { return await _projectStore.OpenAsync(dir, CancellationToken.None); }
        catch (FileNotFoundException)
        {
            var name = new DirectoryInfo(dir).Name;
            return await _projectStore.CreateAsync(dir, string.IsNullOrEmpty(name) ? "Default" : name, CancellationToken.None);
        }
    }

    private async Task ApplyConnectionAsync(ConnectionInfo conn)
    {
        Host = conn.Host;
        Port = conn.Port.ToString();
        Database = conn.Database;
        User = conn.User;
        _activeConnectionId = conn.Id;
        Password = _secretStore is null
            ? ""
            : await _secretStore.GetPasswordAsync(conn.Id, CancellationToken.None) ?? "";
    }

    /// <summary>Find a matching connection in the project (by host/port/db/user) or create a new record.</summary>
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
            Host = Host,
            Port = port,
            Database = Database,
            User = User,
        };
        _project?.Manifest.Connections.Add(created);
        return created;
    }

    private static ConnectionInfo ToConnectionInfo(ConnectionInfo conn) => conn;

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
}
