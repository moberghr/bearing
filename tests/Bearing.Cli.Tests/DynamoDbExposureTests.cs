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
        // The exact asks: the first page is offered the whole cap (the fake pages at 10), and the one continuation
        // only the 5 rows still owed — never a full chunk past the cap.
        Assert.Equal([15], _provider.Executor.ExecuteAsks);
        Assert.Equal([5], _provider.Executor.ContinuationAsks);
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

    // ---- a page the request budget ended (W1b) -----------------------------------------------------

    /// <summary>DynamoDB ends a page after 10 requests however few rows it found. That page is as partial as one
    /// the cap ended — <c>truncated</c> stays true — but no row limit was reached, and the answer says what did
    /// stop it and how to read on, in the JSON and in the text.</summary>
    [Fact]
    public async Task A_page_the_request_budget_ended_says_so_and_never_claims_a_row_limit()
    {
        _provider.Executor.FirstPage = FakeDynamoExecutor.BudgetStoppedPage();
        var host = await HostAsync();

        var response = (QueryResponse)await host.QueryAsync(
            new RunRequest(Name, """select * from "Orders" where "customerId" = 'c1'"""), CancellationToken.None);

        var set = Assert.Single(response.Results);
        Assert.Equal(3, set.RowCount);
        Assert.True(set.Truncated);
        Assert.Equal(ResultSet.RequestBudget, set.StoppedBy);
        Assert.Equal(FakeDynamoExecutor.BudgetNotice, set.Message);

        Assert.Contains("\"stopped_by\": \"request_budget\"", CliRunner.Serialize(response));

        var text = TextTable.Render(response);
        Assert.Contains("(3 rows in 1 ms, stopped by the engine's request budget; use --out to read on)", text);
        Assert.DoesNotContain("row limit", text);
    }

    [Fact]
    public async Task A_page_the_cap_ended_still_says_the_row_limit_and_names_no_budget()
    {
        var host = await HostAsync();

        var response = (QueryResponse)await host.QueryAsync(
            new RunRequest(Name, """select * from "Orders" where "customerId" = 'c1'"""), CancellationToken.None);

        var set = Assert.Single(response.Results);
        Assert.True(set.Truncated);
        Assert.Null(set.StoppedBy);
        Assert.DoesNotContain("stopped_by", CliRunner.Serialize(response));
        Assert.Contains("cut short at the row limit", TextTable.Render(response));
    }

    // ---- describe a dotted table name (W2) -----------------------------------------------------------

    [Theory]
    [InlineData("app.orders")]
    [InlineData("\"app.orders\"")]
    [InlineData("  app.orders ")]
    public async Task A_table_whose_name_holds_a_dot_is_described_by_its_whole_name(string asked)
    {
        var host = await HostAsync();

        var table = (TableResponse)await host.DescribeTableAsync(Name, asked, CancellationToken.None);

        Assert.Equal(FakeDynamoProvider.DottedTable, table.Name);
        Assert.Equal("id", Assert.Single(table.Columns).Name);
    }

    [Fact]
    public async Task A_dotted_name_that_is_no_table_either_way_is_still_not_found()
    {
        var host = await HostAsync();

        var failure = await Assert.ThrowsAsync<CommandFailure>(
            () => host.DescribeTableAsync(Name, "app.missing", CancellationToken.None));

        Assert.StartsWith("'app.missing' is not a table or view on 'agent-dynamo'.", failure.Message);
    }

    // ---- an export of a read that found nothing (SF3) ------------------------------------------------

    /// <summary>A SELECT that found no item, on a table whose key attributes could not be named, has no column
    /// to write a header from. The streamed CSV and the materialised xlsx refuse it in the same words — the
    /// executor's own "returned no items" included — and the file already at the path is left alone.</summary>
    [Theory]
    [InlineData("empty.csv")]
    [InlineData("empty.xlsx")]
    public async Task An_export_of_a_select_that_found_nothing_says_so_the_same_way_on_both_paths(string file)
    {
        _provider.Executor.FirstPage = FakeDynamoExecutor.NoItemsPage();
        var host = await HostAsync();
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, file);
        await File.WriteAllTextAsync(path, "keep me");

        var failure = await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(
            new RunRequest(Name, """select "total" from "Orders" where "customerId" = 'nobody'""") { OutPath = path },
            CancellationToken.None));

        Assert.Equal(
            "agent-dynamo: that statement returned no rows to write. The SELECT returned no items.",
            failure.Message);
        Assert.Equal("keep me", await File.ReadAllTextAsync(path));
        Assert.Equal(1, _provider.Executor.Executes);
        Assert.Equal(0, _provider.Executor.Streams);
    }

    // ---- a batch the guard cannot read (S3) ----------------------------------------------------------

    /// <summary><c>$$</c> is a form the PostgreSQL lexer reads differently from DynamoDB, so the batch cannot be
    /// read safely. It is refused — never sent — in the sentence that is true of it: nobody found a write in
    /// it, and the refusal must not say one was.</summary>
    [Fact]
    public async Task A_batch_the_guard_cannot_read_is_refused_as_unreadable_not_as_a_write()
    {
        var host = await HostAsync();
        const string sql = """select * from "Orders" where "customerId" = $$c1$$""";

        var failure = await Assert.ThrowsAsync<CommandFailure>(
            () => host.QueryAsync(new RunRequest(Name, sql), CancellationToken.None));

        Assert.Equal(
            "agent-dynamo: Bearing could not read this statement safely in this engine's SQL, so it was not run. "
            + "A connection exposed to external tools runs only statements Bearing can read as reads.",
            failure.Message);
        Assert.DoesNotContain("writes", failure.Message);
        Assert.DoesNotContain("dialect not parsed", failure.Message);
        AssertRefusalLogged(sql, failure.Message);
        Assert.Equal(0, _provider.Executor.Calls);
    }

    // ---- refusals around an export and a slow catalog (TC6) ------------------------------------------

    [Theory]
    [InlineData("orders.csv")]
    [InlineData("orders.xlsx")]
    public async Task An_unkeyed_select_with_an_export_path_is_refused_and_writes_no_file(string file)
    {
        var host = await HostAsync(confirmFullScans: false);
        var path = Path.Combine(_root, file);
        const string sql = """select * from "Orders" where "status" = 'open'""";

        var failure = await Assert.ThrowsAsync<CommandFailure>(
            () => host.QueryAsync(new RunRequest(Name, sql) { OutPath = path }, CancellationToken.None));

        Assert.Contains("would read the whole of \"Orders\"", failure.Message);
        Assert.False(File.Exists(path));
        AssertRefusalLogged(sql, failure.Message);
        Assert.Equal(0, _provider.Executor.Calls);
    }

    /// <summary>A table list that never arrives is waited for only as long as <see cref="ScanGuard.Budget"/>, and
    /// then the statement is refused as one Bearing could not check — not left hanging, and not run.</summary>
    [Fact]
    public async Task A_catalog_that_hangs_past_the_budget_is_refused_as_unchecked_within_the_budget()
    {
        _provider.SnapshotHangs = true;
        try
        {
            var host = await HostAsync(confirmFullScans: false);
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var failure = await Assert.ThrowsAsync<CommandFailure>(() => host.QueryAsync(
                new RunRequest(Name, """select * from "Orders" where "customerId" = 'c1'"""), CancellationToken.None));
            clock.Stop();

            Assert.Equal(
                "'agent-dynamo': Bearing could not check whether this SELECT on \"Orders\" is keyed: the table list for "
                + "this connection was not loaded. The bearing command runs keyed reads only, so it was not run; run it "
                + "in Bearing.",
                failure.Message);
            Assert.InRange(clock.Elapsed, ScanGuard.Budget - TimeSpan.FromMilliseconds(250), ScanGuard.Budget + TimeSpan.FromSeconds(3));
            Assert.Equal(0, _provider.Executor.Calls);
        }
        finally
        {
            _provider.ReleaseSnapshot();
        }
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

    /// <summary>Set to make the catalog read hang until <see cref="ReleaseSnapshot"/> — a table list that never
    /// arrives within <see cref="ScanGuard.Budget"/>.</summary>
    public bool SnapshotHangs { get; set; }

    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Let a hanging catalog read end (as a failure), so nothing is left waiting after the test.</summary>
    public void ReleaseSnapshot() => _released.TrySetResult();

    private async Task<ISchemaSnapshot> HangingSnapshotAsync()
    {
        await _released.Task;

        throw new InvalidOperationException("ListTables never answered.");
    }

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

    /// <summary>A table whose name holds a dot — legal on DynamoDB, which has no schema to qualify with.</summary>
    public const string DottedTable = "app.orders";

    private const long DottedId = 2;

    private static readonly SchemaSnapshot Snapshot = new(
        "eu-west-1",
        [""],
        [
            new TableInfo(OrdersId, "", "Orders", RelationKind.Table),
            new TableInfo(DottedId, "", DottedTable, RelationKind.Table),
        ],
        [
            new ColumnInfo(OrdersId, 1, "customerId", "S", NotNull: true, IsPrimaryKey: true),
            new ColumnInfo(OrdersId, 2, "orderDate", "S", NotNull: true, IsPrimaryKey: true),
            new ColumnInfo(OrdersId, 3, "status", "S", NotNull: false, IsPrimaryKey: false),
            new ColumnInfo(DottedId, 1, "id", "S", NotNull: true, IsPrimaryKey: true),
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
                : owner.SnapshotHangs
                    ? owner.HangingSnapshotAsync()
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

    /// <summary>The row cap each first-page call was given, in order (null = none).</summary>
    public List<int?> ExecuteAsks { get; } = [];

    /// <summary>Set to make every continuation call come back as an error result.</summary>
    public bool FailContinuation { get; set; }

    /// <summary>Set to answer the first-page call with this result instead of a page of the fixed table.</summary>
    public QueryResult? FirstPage { get; set; }

    public Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        Executes++;
        ExecuteAsks.Add(options.MaxRows);

        return Task.FromResult<IReadOnlyList<QueryResult>>([FirstPage ?? Page(0, options.MaxRows ?? PageSize)]);
    }

    /// <summary>The executor's sentence for a page its request budget ended (DynamoDbQueryExecutor.BudgetNotice).</summary>
    public const string BudgetNotice =
        "This page stopped after 10 requests to DynamoDB with 3 rows: the read has not reached the end yet.";

    /// <summary>A first page of 3 rows that the request budget ended with a token still in hand — not the cap.</summary>
    public static QueryResult BudgetStoppedPage() => Page(0, 3) with { Message = BudgetNotice };

    /// <summary>A SELECT that found no item and could name no attribute: no columns, and the executor's own
    /// sentence (DynamoDbQueryExecutor.NoItems).</summary>
    public static QueryResult NoItemsPage() => new(
        [], [], 0, TimeSpan.FromMilliseconds(1), "The SELECT returned no items.", null, Truncated: false)
    {
        StatementIndex = 0,
    };

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
