using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Squirrel.Core.Data;
using Squirrel.Core.Schema;

namespace Squirrel.App.ViewModels;

/// <summary>
/// M2 shell view-model: connect to a Postgres, run SQL, expose the result for the grid.
/// Deliberately thin; project/session/completion wiring lands in later milestones.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IProviderRegistry _providers;
    private IDbConnectionFactory? _factory;
    private IQueryExecutor? _executor;
    private IMetadataReader? _metadata;

    public MainWindowViewModel(IProviderRegistry providers)
    {
        _providers = providers;
        // Defaults target the local pagila container for a zero-friction first run.
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

    /// <summary>Live schema, loaded after connect; handed to the completion engine per keystroke.</summary>
    [ObservableProperty] private ISchemaSnapshot? _currentSnapshot;

    public async Task ConnectAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsConnected = false;
        try
        {
            var provider = _providers.Get("postgres");
            var info = new ConnectionInfo
            {
                Id = Guid.NewGuid(),
                Name = $"{Host}/{Database}",
                ProviderId = "postgres",
                Host = Host,
                Port = int.TryParse(Port, out var p) ? p : 5432,
                Database = Database,
                User = User,
            };

            if (_factory is not null) await _factory.DisposeAsync();
            CurrentSnapshot = null;
            _factory = provider.CreateConnectionFactory(info, Password);
            var ok = await _factory.TestConnectionAsync(CancellationToken.None);
            _executor = provider.CreateQueryExecutor(_factory);
            _metadata = provider.CreateMetadataReader(_factory);
            IsConnected = ok;
            StatusText = ok ? $"Connected to {Host}:{Port}/{Database}." : "Connection failed.";
            if (ok) _ = LoadSchemaAsync();
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
}
