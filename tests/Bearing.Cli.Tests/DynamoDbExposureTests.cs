using System.Runtime.CompilerServices;
using Bearing.Cli.Tools;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Bearing.Core.Schema;
using Bearing.Core.Workspace;
using Bearing.Data.DynamoDb;
using Bearing.Persistence;
using Bearing.Sessions;
using Xunit;

namespace Bearing.Cli.Tests;

/// <summary>
/// What the <c>bearing</c> command does with an exposed <b>Amazon DynamoDB</b> connection (SC19, Behaviour 12):
/// only a <c>SELECT</c> Bearing can show is keyed runs — a full scan and one it could not check are refused in
/// <see cref="ScanGuard"/>'s words, <i>whatever the connection's "Confirm full table scans" says</i> — and an
/// export follows the read's continuation to the end.
/// <para>
/// No AWS and no container: the real host, the real session manager and the real PartiQL dialect, over a
/// hand-rolled provider whose executor pages a fixed table by continuation and records every call (§4.1). The
/// executor's call count is the assertion that matters for a refusal — a refused statement never reaches it.
/// </para>
/// </summary>
public class DynamoDbExposureTests : IAsyncLifetime
{
    private const string Name = "agent-dynamo";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bearing-dynamodb-exposure", Guid.NewGuid().ToString("N"));

    private readonly FakeDynamoProvider _provider = new();
    private readonly RecordingQueryLog _log = new();
    private ConnectionSessionManager? _sessions;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_sessions is not null) await _sessions.DisposeAsync();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    // ---- keyed reads run --------------------------------------------------------------------

    [Fact]
    public async Task A_keyed_select_runs_and_returns_the_first_page_with_its_truncated_flag()
    {
        var host = await HostAsync();

        var response = (QueryResponse)await host.QueryAsync(
            new RunRequest(Name, """select * from "Orders" where "customerId" = 'c1'"""), CancellationToken.None);

        var set = Assert.Single(response.Results);
        Assert.Equal(FakeDynamoExecutor.PageSize, set.RowCount);
        Assert.True(set.Truncated);
        Assert.Equal(1, _provider.Executor.Executes);
        Assert.Equal(0, _provider.Executor.Continuations);   // a plain query is the first page, not the read
        Assert.Equal(0, _provider.Executor.Streams);

        var entry = Assert.Single(_log.Entries);
        Assert.True(entry.Success);
        Assert.Equal(QueryOrigin.Cli, entry.Origin);
    }

    /// <summary>An index is read through its own partition key, looked up in the table's details.</summary>
    [Fact]
    public async Task A_select_keyed_on_an_index_partition_key_runs()
    {
        var host = await HostAsync();

        await host.QueryAsync(
            new RunRequest(Name, """select * from "Orders"."byStatus" where "status" = 'open'"""),
            CancellationToken.None);

        Assert.Equal(1, _provider.Executor.Executes);
    }

    // ---- everything else is refused, whatever the setting --------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_full_scan_is_refused_in_behaviour_12s_words_whatever_the_confirm_setting(bool confirmFullScans)
    {
        var host = await HostAsync(confirmFullScans);
        const string sql = """select * from "Orders" where "status" = 'open'""";

        var failure = await Assert.ThrowsAsync<CommandFailure>(
            () => host.QueryAsync(new RunRequest(Name, sql), CancellationToken.None));

        Assert.Equal(
            "'agent-dynamo': this SELECT would read the whole of \"Orders\" — the WHERE has no = or IN on its "
            + "partition key \"customerId\". The bearing command runs keyed reads only; add a partition-key "
            + "condition, or run it in Bearing.",
            failure.Message);
        AssertRefusalLogged(sql, failure.Message);
        Assert.Equal(0, _provider.Executor.Calls);
    }

    [Fact]
    public async Task A_select_on_an_index_without_its_partition_key_is_refused_naming_the_index()
    {
        var host = await HostAsync(confirmFullScans: false);

        var failure = await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(
            new RunRequest(Name, """select * from "Orders"."byStatus" where "customerId" = 'c1'"""),
            CancellationToken.None));

        Assert.Equal(
            "'agent-dynamo': this SELECT would read the whole of \"Orders\".\"byStatus\" — the WHERE has no = or "
            + "IN on its partition key \"status\". The bearing command runs keyed reads only; add a partition-key "
            + "condition, or run it in Bearing.",
            failure.Message);
        Assert.Equal(0, _provider.Executor.Calls);
    }

    [Fact]
    public async Task A_select_on_a_table_bearing_does_not_know_is_refused_as_unchecked_not_as_a_scan()
    {
        var host = await HostAsync(confirmFullScans: false);
        const string sql = """select * from "Missing" where "customerId" = 'c1'""";

        var failure = await Assert.ThrowsAsync<CommandFailure>(
            () => host.QueryAsync(new RunRequest(Name, sql), CancellationToken.None));

        Assert.Equal(
            "'agent-dynamo': Bearing could not check whether this SELECT on \"Missing\" is keyed: the table list "
            + "Bearing loaded has no table named \"Missing\". The bearing command runs keyed reads only, so it was "
            + "not run; run it in Bearing.",
            failure.Message);
        Assert.DoesNotContain("whole of", failure.Message);
        AssertRefusalLogged(sql, failure.Message);
        Assert.Equal(0, _provider.Executor.Calls);
    }

    /// <summary>A catalog that could not be read is a fact missing, not a reason to run: the statement is
    /// refused as one Bearing could not check, even though it pins what is in fact the partition key.</summary>
    [Fact]
    public async Task A_catalog_that_cannot_be_read_refuses_rather_than_running_unchecked()
    {
        _provider.SnapshotThrows = true;
        var host = await HostAsync(confirmFullScans: false);

        var failure = await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(
            new RunRequest(Name, """select * from "Orders" where "customerId" = 'c1'"""), CancellationToken.None));

        Assert.Equal(
            "'agent-dynamo': Bearing could not check whether this SELECT on \"Orders\" is keyed: the table list for "
            + "this connection was not loaded. The bearing command runs keyed reads only, so it was not run; run it "
            + "in Bearing.",
            failure.Message);
        Assert.Equal(0, _provider.Executor.Calls);
    }

    [Fact]
    public async Task A_select_the_reduction_cannot_follow_is_refused_as_unchecked()
    {
        var host = await HostAsync(confirmFullScans: false);

        var failure = await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(
            new RunRequest(Name, """select * from "Orders" o where o."customerId" = 'c1'"""),
            CancellationToken.None));

        Assert.StartsWith("'agent-dynamo': Bearing could not check whether this SELECT", failure.Message);
        Assert.DoesNotContain("whole of", failure.Message);
        Assert.Equal(0, _provider.Executor.Calls);
    }

    /// <summary>One sentence per statement that is not keyed; the keyed one beside them does not run either.</summary>
    [Fact]
    public async Task A_batch_with_one_unkeyed_select_runs_nothing()
    {
        var host = await HostAsync(confirmFullScans: false);

        var failure = await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(
            new RunRequest(Name, """select * from "Orders" where "customerId" = 'c1'; select * from "Orders" """),
            CancellationToken.None));

        Assert.Contains("would read the whole of \"Orders\"", failure.Message);
        Assert.Equal(0, _provider.Executor.Calls);
    }

    [Theory]
    [InlineData("""insert into "Orders" value {'customerId': 'c9', 'orderDate': '2026-01-01'}""")]
    [InlineData("""update "Orders" set "status" = 'closed' where "customerId" = 'c1' and "orderDate" = 'd1'""")]
    [InlineData("""delete from "Orders" where "customerId" = 'c1' and "orderDate" = 'd1'""")]
    [InlineData("begin transaction")]
    [InlineData("commit")]
    [InlineData("""exists(select * from "Orders" where "customerId" = 'c1')""")]
    public async Task Writes_and_every_verb_but_select_are_refused_and_never_sent(string sql)
    {
        var host = await HostAsync(confirmFullScans: false);

        var failure = await Assert.ThrowsAsync<CommandFailure>(
            () => host.QueryAsync(new RunRequest(Name, sql), CancellationToken.None));

        AssertRefusalLogged(sql, failure.Message);
        Assert.Equal(0, _provider.Executor.Calls);
    }

    /// <summary>The existing <c>SupportsExplainPlan</c> = false refusal, reached on this engine too.</summary>
    [Fact]
    public async Task Explain_is_refused_as_not_served_and_nothing_is_sent()
    {
        var host = await HostAsync();

        var failure = await Assert.ThrowsAsync<CommandFailure>(() => host.ExplainAsync(
            new RunRequest(Name, """select * from "Orders" where "customerId" = 'c1'"""), CancellationToken.None));

        Assert.Contains("query plans", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _provider.Executor.Calls);
    }

    // ---- an export follows the continuation ----------------------------------------------------

    [Fact]
    public async Task A_csv_export_follows_the_continuation_to_the_last_row()
    {
        var host = await HostAsync();
        var path = Path.Combine(_root, "orders.csv");

        var export = (ExportResponse)await host.QueryAsync(
            new RunRequest(Name, """select * from "Orders" where "customerId" = 'c1'""") { OutPath = path },
            CancellationToken.None);

        Assert.Equal(FakeDynamoExecutor.Items, export.Rows);
        Assert.False(export.Truncated);
        var lines = File.ReadAllLines(path);
        Assert.Equal(FakeDynamoExecutor.Items + 1, lines.Length);   // the header and every item
        Assert.Equal(FakeDynamoExecutor.Items, lines.Skip(1).Distinct().Count());
        Assert.Equal(1, _provider.Executor.Executes);
        Assert.True(_provider.Executor.Continuations >= 2);
        Assert.Equal(0, _provider.Executor.Streams);           // never the offset-free stream from the start

        var entry = Assert.Single(_log.Entries);
        Assert.True(entry.Success);
        Assert.Equal(FakeDynamoExecutor.Items, entry.RowCount);
    }

    [Fact]
    public async Task An_xlsx_export_follows_the_continuation_to_the_last_row()
    {
        var host = await HostAsync();
        var path = Path.Combine(_root, "orders.xlsx");

        var export = (ExportResponse)await host.QueryAsync(
            new RunRequest(Name, """select * from "Orders" where "customerId" = 'c1'""") { OutPath = path },
            CancellationToken.None);

        Assert.Equal(1, export.Results);
        Assert.Equal(FakeDynamoExecutor.Items, export.Rows);
        Assert.False(export.Truncated);
        Assert.True(File.Exists(path));
        Assert.True(_provider.Executor.Continuations >= 2);

        Assert.Equal(FakeDynamoExecutor.Items, Assert.Single(_log.Entries).RowCount);
    }

    [Theory]
    [InlineData("orders.csv")]
    [InlineData("orders.xlsx")]
    public async Task An_export_capped_by_max_rows_stops_there_and_says_it_was_cut_short(string file)
    {
        var host = await HostAsync();
        var path = Path.Combine(_root, file);

        var export = (ExportResponse)await host.QueryAsync(
            new RunRequest(Name, """select * from "Orders" where "customerId" = 'c1'""")
            {
                OutPath = path,
                MaxRows = 15,
            },
            CancellationToken.None);

        Assert.Equal(15, export.Rows);
        Assert.True(export.Truncated);
        Assert.All(_provider.Executor.ContinuationAsks, asked => Assert.True(asked <= 15));
    }

    /// <summary>A continuation that fails mid-read leaves no file — a fraction of the answer would look like
    /// the whole of it — and the failure is in the history.</summary>
    [Theory]
    [InlineData("orders.csv")]
    [InlineData("orders.xlsx")]
    public async Task A_continuation_that_fails_writes_no_file_and_is_logged_as_a_failure(string file)
    {
        _provider.Executor.FailContinuation = true;
        var host = await HostAsync();
        var path = Path.Combine(_root, file);

        var failure = await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(
            new RunRequest(Name, """select * from "Orders" where "customerId" = 'c1'""") { OutPath = path },
            CancellationToken.None));

        Assert.Contains(FakeDynamoExecutor.ContinuationFailure, failure.Message);
        Assert.False(File.Exists(path));
        Assert.False(Assert.Single(_log.Entries).Success);
    }

    // ---- fixtures -----------------------------------------------------------------------------

    private void AssertRefusalLogged(string sql, string reason)
    {
        var entry = Assert.Single(_log.Entries);
        Assert.False(entry.Success);
        Assert.Equal(sql, entry.SqlText);
        Assert.Equal(reason, entry.ErrorMessage);
        Assert.Equal(QueryOrigin.Cli, entry.Origin);
    }

    private async Task<BearingHost> HostAsync(bool confirmFullScans = true)
    {
        var directory = Path.Combine(_root, "project");
        var store = new JsonProjectStore();
        var project = await store.CreateAsync(directory, "Agent", CancellationToken.None);

        project.Manifest.Connections.Add(new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = Name,
            ProviderId = DynamoDbProvider.ProviderId,
            Host = "eu-west-1",
            Port = 0,
            Database = "eu-west-1",
            User = "analytics",
            CredentialKind = CredentialKind.AwsProfile,
            ConfirmFullScans = confirmFullScans,
            ExternalAccess = ExternalAccess.ReadOnly,
        });

        await store.SaveAsync(project, CancellationToken.None);

        _sessions = new ConnectionSessionManager(
            _provider, () => null, idleTimeout: null, clock: null, runSweepTimer: false);

        return new BearingHost(store, directory, _provider, _sessions, _log);
    }
}

/// <summary>
/// A provider registered as <c>dynamodb</c> — so the host takes DynamoDB's traits and PartiQL dialect — over a
/// catalog shaped the way <c>DynamoDbMetadataReader</c> builds one: <c>Orders</c> with partition key
/// <c>customerId</c> (ordinal 1) and sort key <c>orderDate</c> (ordinal 2), and a GSI <c>byStatus</c> on
/// <c>status</c>.
/// </summary>
internal sealed class FakeDynamoProvider : IDbProvider, IProviderRegistry
{
    private const long OrdersId = 1;

    public FakeDynamoExecutor Executor { get; } = new();

    /// <summary>Set to make the catalog read fail, as a table list that cannot be loaded.</summary>
    public bool SnapshotThrows { get; set; }

    public string Id => DynamoDbProvider.ProviderId;
    public string DisplayName => "Amazon DynamoDB";
    public IReadOnlyList<ConnectionField> ConnectionFields => [];
    public bool EnforcesReadOnlyOnServer => false;
    public bool SupportsSessionTimeZone => false;
    public bool SupportsIntegratedAuth => false;
    public bool SupportsEntraToken => false;
    public bool SupportsPasswordCredentials => false;
    public bool SupportsAwsProfile => true;
    public bool SupportsManualCommit => false;
    public bool SupportsTlsModes => false;
    public bool SupportsServerActivity => false;

    public IDbProvider Get(string providerId) => this;
    public IReadOnlyCollection<IDbProvider> All => [this];

    public DbErrorKind Classify(QueryError error) => DbErrorKind.Unknown;
    public DbErrorKind ClassifyException(Exception exception) => DbErrorKind.Unknown;

    public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password) => new Factory();
    public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory) => new Metadata(this);
    public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory) => Executor;

    public ITransactionScope CreateTransactionScope(IDbConnectionFactory factory)
        => throw new NotSupportedException("No transactions on this engine.");

    public IServerActivity CreateServerActivity(IDbConnectionFactory factory) => new NoActivity();

    private static readonly SchemaSnapshot Snapshot = new(
        "eu-west-1",
        [""],
        [new TableInfo(OrdersId, "", "Orders", RelationKind.Table)],
        [
            new ColumnInfo(OrdersId, 1, "customerId", "S", NotNull: true, IsPrimaryKey: true),
            new ColumnInfo(OrdersId, 2, "orderDate", "S", NotNull: true, IsPrimaryKey: true),
            new ColumnInfo(OrdersId, 3, "status", "S", NotNull: false, IsPrimaryKey: false),
        ],
        [],
        searchPath: [""]);

    private static readonly TableDetails OrdersDetails = new(
        [],
        [
            new IndexInfo(10, "PRIMARY", IsUnique: true, IsPrimary: true, IsValid: true, [1, 2], "primary key"),
            new IndexInfo(11, "byStatus", IsUnique: false, IsPrimary: false, IsValid: true, [3, 2], "gsi"),
        ],
        [],
        []);

    private sealed class Factory : IDbConnectionFactory
    {
        public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Metadata(FakeDynamoProvider owner) : IMetadataReader
    {
        public Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(["eu-west-1"]);

        public Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct)
            => owner.SnapshotThrows
                ? throw new InvalidOperationException("ListTables failed.")
                : Task.FromResult<ISchemaSnapshot>(Snapshot);

        public Task<TableDetails> GetTableDetailsAsync(long tableId, CancellationToken ct)
            => Task.FromResult(tableId == OrdersId ? OrdersDetails : new TableDetails([], [], [], []));

        public Task<IReadOnlyList<RoutineInfo>> GetRoutinesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RoutineInfo>>([]);

        public Task<string> GetViewDefinitionAsync(long tableId, CancellationToken ct) => Task.FromResult("");

        public Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RelationSize>>([]);

        public Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DatabaseSize>>([]);

        public Task<string> GetRoutineDefinitionAsync(long routineId, CancellationToken ct) => Task.FromResult("");

        public Task<DatabaseObjectKinds> GetDatabaseObjectsAsync(CancellationToken ct)
            => Task.FromResult(DatabaseObjectKinds.Empty);

        public Task<IReadOnlyList<RoleInfo>> GetRolesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RoleInfo>>([]);

        public Task<RoleGrants> GetRoleGrantsAsync(string roleName, CancellationToken ct)
            => Task.FromResult(RoleGrants.NotVisible);

        public Task<IReadOnlyList<SchemaObjectInfo>> GetTablespacesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SchemaObjectInfo>>([]);
    }

    private sealed class NoActivity : IServerActivity
    {
        public Task<ServerActivity> GetActivityAsync(ActivityFilter filter, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<bool> CancelBackendAsync(int pid, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> TerminateBackendAsync(int pid, CancellationToken ct) => throw new NotSupportedException();
    }
}

/// <summary>
/// Pages a fixed table of <see cref="Items"/> rows by continuation, <see cref="PageSize"/> at most per call —
/// the shape of the real executor's contract (a token while more remain, the same columns on every page) —
/// and counts every call, so a refusal can assert that nothing was sent.
/// </summary>
internal sealed class FakeDynamoExecutor : IQueryExecutor
{
    public const int Items = 25;
    public const int PageSize = 10;
    public const string ContinuationFailure = "The continuation token has expired.";

    private static readonly IReadOnlyList<ColumnDescriptor> Columns =
    [
        new("customerId", "S", typeof(string)),
        new("orderDate", "S", typeof(string)),
    ];

    public int Executes { get; private set; }
    public int Continuations { get; private set; }
    public int Streams { get; private set; }
    public int Others { get; private set; }
    public int Calls => Executes + Continuations + Streams + Others;

    /// <summary>The row count each continuation call asked for, in order.</summary>
    public List<int> ContinuationAsks { get; } = [];

    /// <summary>Set to make every continuation call come back as an error result.</summary>
    public bool FailContinuation { get; set; }

    public Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        Executes++;

        return Task.FromResult<IReadOnlyList<QueryResult>>([Page(0, options.MaxRows ?? PageSize)]);
    }

    public Task<QueryResult> ExecuteContinuationAsync(string continuation, int maxRows, CancellationToken ct)
    {
        Continuations++;
        ContinuationAsks.Add(maxRows);

        if (FailContinuation)
        {
            return Task.FromResult(new QueryResult(
                [], [], 0, TimeSpan.Zero, null, new QueryError(ContinuationFailure, null, null), Truncated: false));
        }

        return Task.FromResult(Page(int.Parse(continuation["page:".Length..]), maxRows));
    }

    public IAsyncEnumerable<RowBatch> StreamRowsAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        Streams++;

        return Empty(ct);
    }

    public Task<QueryResult> ExecutePageAsync(string pageSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
    {
        Others++;

        throw new NotSupportedException();
    }

    public Task<long?> CountAsync(string countSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
    {
        Others++;

        return Task.FromResult<long?>(null);
    }

    public Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
    {
        Others++;

        throw new NotSupportedException();
    }

    private static QueryResult Page(int offset, int maxRows)
    {
        var take = Math.Min(Math.Min(Math.Max(1, maxRows), PageSize), Items - offset);
        var rows = Enumerable.Range(offset, take)
            .Select(i => new object?[] { "c1", $"d{i:D3}" })
            .ToList();
        var end = offset + take;

        return new QueryResult(Columns, rows, rows.Count, TimeSpan.FromMilliseconds(1), null, null, Truncated: end < Items)
        {
            StatementIndex = 0,
            Continuation = end < Items ? $"page:{end}" : null,
        };
    }

    private static async IAsyncEnumerable<RowBatch> Empty([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;

        yield break;
    }
}
