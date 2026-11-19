using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Services;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Bearing.App.Results;
using Bearing.Results;
using Bearing.Sessions;
using Bearing.Sql;
using Xunit;
using TokenExecutor = Bearing.App.Tests.ContinuationPagingTests.TokenExecutor;

namespace Bearing.App.Tests;

/// <summary>
/// DynamoDB's full-scan question in the App (SC14, <i>Behaviour 8</i>), driven through
/// <see cref="ExecutionViewModel"/> with the real <see cref="PartiQlDialect"/> (a <c>dynamodb</c> connection resolves
/// <see cref="ProviderTraits.DynamoDb"/>), a fake table list and a token executor. Every case counts both what the
/// dialog was asked and what the executor ran — "Cancel runs nothing" is the executor's zero.
/// </summary>
public class ScanConfirmationTests : IDisposable
{
    private const long OrdersId = 1;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-scan", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    /// <summary>Orders: partition key customerId, sort key orderDate, and a GSI byStatus on status — shaped the
    /// way <c>DynamoDbMetadataReader</c> builds it.</summary>
    private static readonly SchemaSnapshot Tables = new(
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

    private sealed record Harness(
        ExecutionViewModel Exec,
        EditorTabViewModel Tab,
        TokenExecutor Executor,
        FakeDialogs Dialogs,
        FakeProvider Provider,
        WorkspaceContext Ctx,
        ConnectionInfo Connection,
        Func<string> Status);

    private Harness Build(
        string providerId = "dynamodb", bool confirmFullScans = true, bool withDialogs = true, int totalRows = 25)
    {
        Directory.CreateDirectory(_root);
        var settings = SettingsService.InMemory(new AppSettings
        {
            AutosaveMode = AutosaveMode.Off,
            ResultPageSize = 10,
            ResultFetchAllMaxRows = 5_000,
        });
        var executor = new TokenExecutor { TotalRows = totalRows };
        var provider = new FakeProvider
        {
            Executor = executor,
            Snapshot = Tables,
            Details = OrdersDetails,
            Sizes = [new RelationSize(OrdersId, 0, 0, 0, 0, EstimatedRows: 12_345)],
        };
        var ctx = new WorkspaceContext(
            provider,
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            settings: settings);
        var conn = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "orders-prod",
            ProviderId = providerId,
            Host = "eu-west-1",
            Database = "eu-west-1",
            CredentialKind = providerId == "dynamodb" ? CredentialKind.AwsProfile : CredentialKind.StoredPassword,
            ConfirmFullScans = confirmFullScans,
        };
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };

        var status = "";
        var tab = new EditorTabViewModel("orders") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;
        ctx.Status = s => status = s;

        var dialogs = new FakeDialogs();
        var exec = new ExecutionViewModel(ctx, withDialogs ? dialogs : null);

        return new Harness(exec, tab, executor, dialogs, provider, ctx, conn, () => status);
    }

    // ---- before a run ----------------------------------------------------------------------------

    [Fact]
    public async Task A_full_scan_asks_once_and_cancel_runs_nothing()
    {
        var h = Build();

        await h.Exec.ExecuteAsync("select * from \"Orders\" where status = 'open'");

        var question = Assert.Single(h.Dialogs.ScanQuestions);
        Assert.Equal(ScanQuestionKind.Run, question.Kind);
        Assert.Equal(ScanVerdictKind.FullScan, Assert.Single(question.Verdicts).Kind);
        Assert.Equal(0, h.Executor.ExecuteCalls);
        Assert.Null(h.Tab.LastResult);
        Assert.Equal("Cancelled — nothing ran.", h.Status());
    }

    /// <summary>
    /// The review's case: the check runs before the statement, for up to the lookup budget plus the dialog. The tab
    /// is busy for all of it, so a second Run while the question is open neither starts a second lookup nor asks a
    /// second time, and the tab is idle again once it is answered.
    /// </summary>
    [Fact]
    public async Task The_tab_is_busy_while_the_scan_check_asks_and_a_second_run_does_not_ask_again()
    {
        var h = Build();
        const string sql = "select * from \"Orders\" where status = 'open'";
        bool? runningWhenAsked = null;
        Task? second = null;
        h.Dialogs.OnConfirmScan = _ =>
        {
            runningWhenAsked = h.Tab.IsRunning;
            second ??= h.Exec.ExecuteAsync(sql);
        };

        await h.Exec.ExecuteAsync(sql);
        await second!;

        Assert.True(runningWhenAsked);
        Assert.Single(h.Dialogs.ScanQuestions);
        Assert.Equal(0, h.Executor.ExecuteCalls);
        Assert.False(h.Tab.IsRunning);
    }

    /// <summary>Esc during the lookup stops it: nothing is asked and nothing runs.</summary>
    [Fact]
    public async Task Esc_during_the_scan_lookup_cancels_the_run()
    {
        var h = Build();
        h.Provider.ConnectGate = new TaskCompletionSource<bool>();
        try
        {
            var run = h.Exec.ExecuteAsync("select * from \"Orders\" where status = 'open'");
            Assert.True(h.Tab.IsRunning);

            h.Exec.CancelExecution();
            await run;

            Assert.Empty(h.Dialogs.ScanQuestions);
            Assert.Equal(0, h.Executor.ExecuteCalls);
            Assert.False(h.Tab.IsRunning);
            Assert.Equal("Cancelled — nothing ran.", h.Status());
        }
        finally
        {
            h.Provider.ConnectGate.TrySetResult(true);
        }
    }

    /// <summary>Any WHERE that does not pin the partition key asks: one page of it may evaluate up to ten requests'
    /// worth of items.</summary>
    [Theory]
    [InlineData("select * from \"Orders\" where x = 1")]
    [InlineData("select * from \"Orders\" where status = 'open' order by customerId")]
    [InlineData("select * from \"Orders\".\"byStatus\" where customerId = 'a'")]
    public async Task A_filtered_scan_still_asks_and_cancel_runs_nothing(string sql)
    {
        var h = Build();

        await h.Exec.ExecuteAsync(sql);

        Assert.Equal(ScanVerdictKind.FullScan, Assert.Single(Assert.Single(h.Dialogs.ScanQuestions).Verdicts).Kind);
        Assert.Equal(0, h.Executor.ExecuteCalls);
    }

    [Fact]
    public async Task A_confirmed_scan_runs_once_and_its_results_are_marked()
    {
        var h = Build();
        h.Dialogs.ConfirmScanAnswer = true;

        await h.Exec.ExecuteAsync("select * from \"Orders\" where status = 'open'");

        Assert.Single(h.Dialogs.ScanQuestions);
        Assert.Equal(1, h.Executor.ExecuteCalls);
        Assert.True(h.Tab.LastResult!.IsFullScan);
    }

    /// <summary>The text names the table, the partition key it lacks, DynamoDB's item estimate, that rows load a
    /// page at a time, and the IAM boundary — the sentences are ScanGuard's.</summary>
    [Fact]
    public async Task The_question_names_the_table_the_key_the_item_count_and_the_boundary()
    {
        var h = Build();

        await h.Exec.ExecuteAsync("select * from \"Orders\" where status = 'open'");

        var question = Assert.Single(h.Dialogs.ScanQuestions);
        Assert.Contains("\"Orders\"", question.Text);
        Assert.Contains("\"customerId\"", question.Text);
        Assert.Contains("about 12,345 items", question.Text);
        Assert.Contains(ScanGuard.Paging, question.Text);
        Assert.Contains(ScanGuard.Boundary, question.Text);
        Assert.Contains("dynamodb:FullTableScan", question.Text);
        Assert.Equal("Read a whole table on orders-prod?", question.Heading);
        Assert.Equal("Run anyway", question.ConfirmLabel);
    }

    [Fact]
    public async Task A_scan_through_an_index_names_the_index()
    {
        var h = Build();

        await h.Exec.ExecuteAsync("select * from \"Orders\".\"byStatus\" where customerId = 'a'");

        var verdict = Assert.Single(Assert.Single(h.Dialogs.ScanQuestions).Verdicts);
        Assert.Equal(ScanVerdictKind.FullScan, verdict.Kind);
        Assert.Equal("status", verdict.PartitionKey);
        Assert.Equal(0, h.Executor.ExecuteCalls);
    }

    /// <summary>An unknown table is "could not check": asked, and never described as a scan.</summary>
    [Fact]
    public async Task An_undetermined_read_asks_once_says_it_could_not_check_and_cancel_runs_nothing()
    {
        var h = Build();

        await h.Exec.ExecuteAsync("select * from \"Invoices\" where id = 'a'");

        var question = Assert.Single(h.Dialogs.ScanQuestions);
        var verdict = Assert.Single(question.Verdicts);
        Assert.Equal(ScanVerdictKind.Undetermined, verdict.Kind);
        Assert.Equal(ScanUncertainty.UnknownTable, verdict.Uncertainty);
        Assert.Contains("could not check", question.Text);
        Assert.DoesNotContain("would read the whole", question.Text);
        Assert.Contains("could not check", question.Heading);
        // Moved from the headless dialog test: never called a scan, in the heading or the title.
        Assert.DoesNotContain("scan", question.Heading, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scan", question.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, h.Executor.ExecuteCalls);
    }

    /// <summary>A batch is asked about once, listing only the statements that are not keyed.</summary>
    [Fact]
    public async Task A_batch_with_a_keyed_read_and_two_scans_asks_once_for_the_whole_run()
    {
        var h = Build();
        h.Dialogs.ConfirmScanAnswer = true;

        await h.Exec.ExecuteAsync(
            "select * from \"Orders\" where customerId = 'a'; select * from \"Orders\" where status = 'open'; "
            + "select * from \"Invoices\"");

        var question = Assert.Single(h.Dialogs.ScanQuestions);
        Assert.Equal(
            [ScanVerdictKind.FullScan, ScanVerdictKind.Undetermined],
            question.Verdicts.Select(v => v.Kind));
        Assert.Equal(1, h.Executor.ExecuteCalls);
    }

    [Theory]
    [InlineData("select * from \"Orders\" where customerId = 'a'")]
    [InlineData("select * from \"Orders\" where customerId IN ['a', 'b'] and status = 'open'")]
    [InlineData("select * from \"Orders\".\"byStatus\" where status = 'open'")]
    public async Task A_keyed_read_never_asks(string sql)
    {
        var h = Build();

        await h.Exec.ExecuteAsync(sql);

        Assert.Empty(h.Dialogs.ScanQuestions);
        Assert.Equal(1, h.Executor.ExecuteCalls);
        Assert.False(h.Tab.LastResult!.IsFullScan);
    }

    [Theory]
    [InlineData("select * from \"Orders\"")]
    [InlineData("select * from \"Orders\" where status = 'open'")]
    public async Task With_the_setting_off_a_scan_never_asks(string sql)
    {
        var h = Build(confirmFullScans: false);

        await h.Exec.ExecuteAsync(sql);

        Assert.Empty(h.Dialogs.ScanQuestions);
        Assert.Equal(1, h.Executor.ExecuteCalls);
        Assert.False(h.Tab.LastResult!.IsFullScan);

        await h.Exec.FetchAllAsync(h.Tab.LastResult);
        Assert.Empty(h.Dialogs.ScanQuestions);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public async Task Other_engines_never_ask(string providerId)
    {
        var h = Build(providerId);

        await h.Exec.ExecuteAsync("select * from Orders");

        Assert.Empty(h.Dialogs.ScanQuestions);
        Assert.Equal(1, h.Executor.ExecuteCalls);
        Assert.Null(ScanConfirmation.TargetsToCheck(h.Connection, ProviderTraits.For(h.Connection).Dialect, "select * from Orders"));
    }

    /// <summary>No window, no answer: the run that would have been asked about does not happen (the real
    /// <see cref="Views.DialogService"/> answers false headless for the same reason).</summary>
    [Fact]
    public async Task With_no_window_to_ask_in_a_scan_does_not_run()
    {
        var h = Build(withDialogs: false);

        await h.Exec.ExecuteAsync("select * from \"Orders\" where status = 'open'");

        Assert.Equal(0, h.Executor.ExecuteCalls);
        Assert.Contains("no window to confirm it in", h.Status());
    }

    // ---- an unfiltered SELECT (no WHERE at all): the first page runs unasked -----------------------

    /// <summary>The tree's "Select top 100 rows" on DynamoDB: with no WHERE every evaluated item is returned, so
    /// the first page reads about one page of items and runs without asking — but the result is still marked, so
    /// Fetch all asks.</summary>
    [Fact]
    public async Task An_unfiltered_select_runs_its_first_page_unasked_and_fetch_all_still_asks()
    {
        var h = Build();

        await h.Exec.ExecuteAsync("select * from \"Orders\"");

        Assert.Empty(h.Dialogs.ScanQuestions);
        Assert.Equal(1, h.Executor.ExecuteCalls);
        Assert.Equal(10, h.Tab.LastResult!.Loaded);
        Assert.True(h.Tab.LastResult.IsFullScan);

        Assert.False(await h.Exec.FetchAllAsync(h.Tab.LastResult));

        var question = Assert.Single(h.Dialogs.ScanQuestions);
        Assert.Equal(ScanQuestionKind.FetchAll, question.Kind);
        Assert.DoesNotContain("confirmed", question.Text);
        Assert.Empty(h.Executor.Continued);
        Assert.Equal(10, h.Tab.LastResult.Loaded);
    }

    [Theory]
    [InlineData("select customerId, status from \"Orders\" order by customerId")]
    [InlineData("select * from \"Orders\".\"byStatus\"")]
    public async Task Order_by_projection_and_an_index_do_not_make_an_unfiltered_select_ask(string sql)
    {
        var h = Build();

        await h.Exec.ExecuteAsync(sql);

        Assert.Empty(h.Dialogs.ScanQuestions);
        Assert.Equal(1, h.Executor.ExecuteCalls);
        Assert.True(h.Tab.LastResult!.IsFullScan);
    }

    [Fact]
    public async Task Export_of_an_unfiltered_select_asks_once_and_cancel_writes_nothing()
    {
        var h = Build();
        h.Dialogs.ExportPath = Path.Combine(_root, "orders.csv");
        await h.Exec.ExecuteAsync("select * from \"Orders\"");

        await h.Exec.ExportAsync(h.Tab.LastResult!, ExportFormat.Csv);

        Assert.Equal(ScanQuestionKind.FetchAll, Assert.Single(h.Dialogs.ScanQuestions).Kind);
        Assert.Empty(h.Executor.Continued);
        Assert.False(File.Exists(h.Dialogs.ExportPath));
    }

    [Fact]
    public async Task Load_more_on_an_unfiltered_select_does_not_ask()
    {
        var h = Build();
        await h.Exec.ExecuteAsync("select * from \"Orders\"");

        await h.Exec.LoadMoreAsync(h.Tab.LastResult!);

        Assert.Empty(h.Dialogs.ScanQuestions);
        Assert.Equal(["t1"], h.Executor.Continued);
    }

    /// <summary>Nothing to ask, so no window is needed: the first page runs.</summary>
    [Fact]
    public async Task With_no_window_an_unfiltered_select_still_runs_its_first_page()
    {
        var h = Build(withDialogs: false);

        await h.Exec.ExecuteAsync("select * from \"Orders\"");

        Assert.Equal(1, h.Executor.ExecuteCalls);
        Assert.True(h.Tab.LastResult!.IsFullScan);
    }

    /// <summary>A batch mixing an unfiltered SELECT with one that needs asking asks once, naming only the one
    /// that needs asking; confirmed, the whole run is marked.</summary>
    [Theory]
    [InlineData("select * from \"Orders\"; select * from \"Orders\" where status = 'open'",
        ScanVerdictKind.FullScan, "select * from \"Orders\" where status = 'open'")]
    [InlineData("select * from \"Orders\"; select * from \"Invoices\"",
        ScanVerdictKind.Undetermined, "select * from \"Invoices\"")]
    public async Task A_batch_with_an_unfiltered_select_asks_only_about_the_others(
        string sql, ScanVerdictKind asked, string statement)
    {
        var h = Build();
        h.Dialogs.ConfirmScanAnswer = true;

        await h.Exec.ExecuteAsync(sql);

        var question = Assert.Single(h.Dialogs.ScanQuestions);
        var verdict = Assert.Single(question.Verdicts);
        Assert.Equal(asked, verdict.Kind);
        Assert.Equal(statement, verdict.Target.Statement);
        Assert.False(verdict.Unfiltered);
        Assert.Equal(1, h.Executor.ExecuteCalls);
        Assert.True(h.Tab.LastResult!.IsFullScan);
    }

    [Fact]
    public async Task A_batch_of_an_unfiltered_select_and_a_keyed_one_runs_unasked_and_is_marked()
    {
        var h = Build();

        await h.Exec.ExecuteAsync("select * from \"Orders\" where customerId = 'a'; select * from \"Orders\"");

        Assert.Empty(h.Dialogs.ScanQuestions);
        Assert.Equal(1, h.Executor.ExecuteCalls);
        Assert.True(h.Tab.LastResult!.IsFullScan);
    }

    [Fact]
    public void The_run_question_leaves_out_unfiltered_scans_and_is_null_when_only_they_remain()
    {
        var conn = new ConnectionInfo { Id = Guid.NewGuid(), ProviderId = "dynamodb", Name = "orders-prod" };
        var unfiltered = new ScanVerdict(
            new ScanTarget("select * from \"Orders\"", "Orders", null, [], Readable: true) { HasWhere = false },
            ScanVerdictKind.FullScan, "customerId", null, null)
        { Unfiltered = true };
        var filtered = new ScanVerdict(
            new ScanTarget("select * from \"Orders\" where x = 1", "Orders", null, [], Readable: true),
            ScanVerdictKind.FullScan, "customerId", null, null);

        Assert.Null(ScanConfirmation.QuestionFor(conn, [unfiltered]));

        var question = ScanConfirmation.QuestionFor(conn, [unfiltered, filtered])!;
        Assert.Equal([filtered], question.Verdicts);
        Assert.Contains("x = 1", question.Verdicts[0].Target.Statement);
    }

    // ---- after a confirmed scan --------------------------------------------------------------------

    private async Task<Harness> ConfirmedScan()
    {
        var h = Build();
        h.Dialogs.ConfirmScanAnswer = true;
        await h.Exec.ExecuteAsync("select * from \"Orders\" where status = 'open'");
        Assert.Single(h.Dialogs.ScanQuestions);
        h.Dialogs.ScanQuestions.Clear();

        return h;
    }

    [Fact]
    public async Task Fetch_all_on_a_scan_asks_again_naming_the_limit_and_cancel_reads_nothing()
    {
        var h = await ConfirmedScan();
        h.Dialogs.ConfirmScanAnswer = false;

        Assert.False(await h.Exec.FetchAllAsync(h.Tab.LastResult!));

        var question = Assert.Single(h.Dialogs.ScanQuestions);
        Assert.Equal(ScanQuestionKind.FetchAll, question.Kind);
        Assert.Contains("Fetch all limit of 5,000 rows", question.Text);
        Assert.Contains(ScanGuard.Boundary, question.Text);
        Assert.Empty(h.Executor.Continued);
        Assert.Equal(10, h.Tab.LastResult!.Loaded);
    }

    [Fact]
    public async Task Fetch_all_on_a_scan_confirmed_again_reads_to_the_end()
    {
        var h = await ConfirmedScan();

        Assert.True(await h.Exec.FetchAllAsync(h.Tab.LastResult!));

        Assert.Single(h.Dialogs.ScanQuestions);
        Assert.Equal(25, h.Tab.LastResult!.Loaded);
        Assert.Equal(1, h.Executor.ExecuteCalls);
    }

    [Fact]
    public async Task Export_of_a_scan_asks_again_once_and_cancel_writes_nothing()
    {
        var h = await ConfirmedScan();
        h.Dialogs.ConfirmScanAnswer = false;
        h.Dialogs.ExportPath = Path.Combine(_root, "orders.csv");

        await h.Exec.ExportAsync(h.Tab.LastResult!, ExportFormat.Csv);

        var question = Assert.Single(h.Dialogs.ScanQuestions);
        Assert.Equal(ScanQuestionKind.FetchAll, question.Kind);
        Assert.Empty(h.Executor.Continued);
        Assert.Empty(h.Dialogs.ExportPickers);
        Assert.False(File.Exists(h.Dialogs.ExportPath));
    }

    [Fact]
    public async Task Export_of_a_scan_confirmed_again_writes_every_row()
    {
        var h = await ConfirmedScan();
        h.Dialogs.ExportPath = Path.Combine(_root, "orders.csv");

        await h.Exec.ExportAsync(h.Tab.LastResult!, ExportFormat.Csv);

        Assert.Single(h.Dialogs.ScanQuestions);
        var lines = File.ReadAllText(h.Dialogs.ExportPath).TrimEnd('\r', '\n').Split("\r\n");
        Assert.Equal(26, lines.Length);
    }

    /// <summary>A second scan result of the same run, continuing from its own token, beside the first.</summary>
    private static ResultSetViewModel SecondScanResult(ResultSetViewModel first)
    {
        var second = ResultSetBuilder.BuildResultSets(
            [new QueryResult([new ColumnDescriptor("n", "N", typeof(int))], [[1]], 1, TimeSpan.Zero, null, null, Truncated: true)
            {
                Continuation = "t9",
            }],
            "select * from \"Orders\" where status = 'shipped'", null, ProviderTraits.DynamoDb,
            sourceSession: first.SourceSession)[0];
        ScanConfirmation.Mark([second], isFullScan: true);

        return second;
    }

    /// <summary>The review's case: a workbook export of a run with several scan results asks once, not once per
    /// result.</summary>
    [Fact]
    public async Task A_workbook_export_of_several_scan_results_asks_once()
    {
        var h = await ConfirmedScan();
        var first = h.Tab.LastResult!;
        h.Tab.SetRunResults([first, SecondScanResult(first)]);
        h.Dialogs.ConfirmScanAnswer = false;
        h.Dialogs.ExportPath = Path.Combine(_root, "orders.xlsx");

        await h.Exec.ExportRunAsync();

        var question = Assert.Single(h.Dialogs.ScanQuestions);
        Assert.Equal(ScanQuestionKind.FetchAll, question.Kind);
        Assert.Empty(h.Dialogs.ExportPickers); // the default answer is Cancel: nothing written
        Assert.Empty(h.Executor.Continued);
    }

    [Fact]
    public async Task A_workbook_export_confirmed_once_reads_every_scan_result_on()
    {
        var h = await ConfirmedScan();
        var first = h.Tab.LastResult!;
        var second = SecondScanResult(first);
        h.Tab.SetRunResults([first, second]);
        h.Dialogs.ConfirmScanAnswer = true;
        h.Dialogs.ExportPath = Path.Combine(_root, "orders.xlsx");

        await h.Exec.ExportRunAsync();

        Assert.Single(h.Dialogs.ScanQuestions);
        Assert.Contains("t9", h.Executor.Continued);
        Assert.False(first.HasMore);
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task Load_more_on_a_scan_does_not_ask_again()
    {
        var h = await ConfirmedScan();

        await h.Exec.LoadMoreAsync(h.Tab.LastResult!);
        await h.Exec.LoadMoreAsync(h.Tab.LastResult!);

        Assert.Empty(h.Dialogs.ScanQuestions);
        Assert.Equal(["t1", "t2"], h.Executor.Continued);
    }

    /// <summary>TC2: a scan-confirmed result whose connection can no longer be resolved still asks — without a
    /// name — and Cancel reads nothing. It used to go ahead unasked (fail open).</summary>
    [Fact]
    public async Task Fetch_all_on_a_scan_whose_connection_is_gone_still_asks_and_cancel_reads_nothing()
    {
        var h = await ConfirmedScan();
        h.Dialogs.ConfirmScanAnswer = false;
        h.Ctx.Project!.Manifest.Connections.Clear();   // deleted since the run: nothing to name

        Assert.False(await h.Exec.FetchAllAsync(h.Tab.LastResult!));

        var question = Assert.Single(h.Dialogs.ScanQuestions);
        Assert.Equal(ScanQuestionKind.FetchAll, question.Kind);
        Assert.Null(question.Connection);
        Assert.Equal("Keep reading the whole table?", question.Heading);
        Assert.Empty(h.Executor.Continued);
    }

    [Fact]
    public async Task With_no_connection_and_no_window_fetch_all_on_a_scan_reads_nothing()
    {
        var h = await ConfirmedScan();
        var check = new ScanConfirmation(h.Ctx.Sessions, dialogs: null, _ => { });

        Assert.False(await check.ConfirmFetchAllAsync(h.Tab.LastResult!, info: null, fetchAllLimit: 5_000));
    }

    /// <summary>TC3: a run Bearing could not check, once confirmed, is treated like a confirmed scan afterwards —
    /// its result is marked, and Fetch all asks again.</summary>
    [Fact]
    public async Task A_confirmed_undetermined_run_is_marked_and_fetch_all_asks_again()
    {
        var h = Build();
        h.Dialogs.ConfirmScanAnswer = true;
        await h.Exec.ExecuteAsync("select * from \"Invoices\" where id = 'a'");
        Assert.Equal(ScanVerdictKind.Undetermined, Assert.Single(Assert.Single(h.Dialogs.ScanQuestions).Verdicts).Kind);
        Assert.True(h.Tab.LastResult!.IsFullScan);
        h.Dialogs.ScanQuestions.Clear();
        h.Dialogs.ConfirmScanAnswer = false;

        Assert.False(await h.Exec.FetchAllAsync(h.Tab.LastResult!));

        Assert.Equal(ScanQuestionKind.FetchAll, Assert.Single(h.Dialogs.ScanQuestions).Kind);
        Assert.Empty(h.Executor.Continued);
    }

    /// <summary>The window title follows the heading's rule: a run Bearing could not check is not titled a scan.</summary>
    [Fact]
    public void The_title_calls_only_a_known_scan_a_scan()
    {
        var conn = new ConnectionInfo { Id = Guid.NewGuid(), ProviderId = "dynamodb", Name = "orders-prod" };
        var target = new ScanTarget("select * from \"Orders\"", "Orders", null, [], Readable: true);
        var scan = new ScanQuestion(
            conn, ScanQuestionKind.Run, [new ScanVerdict(target, ScanVerdictKind.FullScan, "customerId", null, null)], "");
        var unchecked_ = new ScanQuestion(
            conn, ScanQuestionKind.Run,
            [new ScanVerdict(target, ScanVerdictKind.Undetermined, null, null, ScanUncertainty.UnknownTable)], "");

        Assert.Equal("Confirm full table scan", scan.Title);
        Assert.Equal("Confirm unchecked read", unchecked_.Title);
        Assert.DoesNotContain("scan", unchecked_.Heading, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A scan that fit in one page has nothing more to read, so Fetch all has nothing to ask about.</summary>
    [Fact]
    public async Task Fetch_all_on_a_scan_with_nothing_more_does_not_ask()
    {
        var h = Build(totalRows: 5);
        h.Dialogs.ConfirmScanAnswer = true;
        await h.Exec.ExecuteAsync("select * from \"Orders\" where status = 'open'");
        h.Dialogs.ScanQuestions.Clear();

        Assert.True(await h.Exec.FetchAllAsync(h.Tab.LastResult!));
        Assert.Empty(h.Dialogs.ScanQuestions);
    }

    // ---- the lookup's budget -----------------------------------------------------------------------

    /// <summary>A table list that does not arrive within the budget is "could not check", not a scan and not a
    /// pass — and the lookup returns at the budget rather than waiting for it.</summary>
    [Fact]
    public async Task A_table_list_that_misses_the_budget_is_undetermined()
    {
        var h = Build();
        h.Provider.MetadataGate = new TaskCompletionSource();
        var check = new ScanConfirmation(h.Ctx.Sessions, h.Dialogs, _ => { }) { Budget = TimeSpan.FromMilliseconds(100) };
        var targets = PartiQlDialect.Instance.ScanTargets("select * from \"Orders\" where customerId = 'a'")!;

        try
        {
            var verdict = Assert.Single(await check.ClassifyAsync(h.Connection, targets));

            Assert.Equal(ScanVerdictKind.Undetermined, verdict.Kind);
            Assert.Equal(ScanUncertainty.TablesNotLoaded, verdict.Uncertainty);
        }
        finally
        {
            h.Provider.MetadataGate.TrySetResult();
        }
    }

    /// <summary>ARCH1: a SELECT the reduction did not follow is asked about as one Bearing could not check. Before
    /// the shared padding the App looked only at the targets it was handed, so a batch whose only read was skipped
    /// had nothing to ask about and ran unasked.</summary>
    [Fact]
    public async Task A_read_the_reduction_skipped_is_asked_about_and_cancel_runs_nothing()
    {
        var h = Build();
        var statuses = new System.Collections.Generic.List<string>();
        var check = new ScanConfirmation(h.Ctx.Sessions, h.Dialogs, statuses.Add);
        const string sql = "select * from \"Orders\" where customerId = 'a'";

        Assert.Empty(SkippingPartiQlDialect.Instance.ScanTargets(sql)!);
        var decision = await check.ConfirmRunAsync(h.Connection, SkippingPartiQlDialect.Instance, sql);

        Assert.False(decision.Proceed);
        var verdict = Assert.Single(Assert.Single(h.Dialogs.ScanQuestions).Verdicts);
        Assert.Equal(ScanVerdictKind.Undetermined, verdict.Kind);
        Assert.Equal(ScanUncertainty.Unreadable, verdict.Uncertainty);
        Assert.Contains("Cancelled — nothing ran.", statuses);
    }

    [Fact]
    public void Mark_sets_every_result_of_a_confirmed_scan_and_nothing_otherwise()
    {
        var columns = new[] { new ColumnDescriptor("pk", "S", typeof(string)) };
        var rows = new[] { new object?[] { "a" } };
        var results = new[] { new QueryResult(columns, rows, 1, TimeSpan.Zero, null, null, Truncated: false) };

        var plain = Results.ResultSetBuilder.BuildResultSets(results, "select * from a", snapshot: null);
        ScanConfirmation.Mark(plain, isFullScan: false);
        Assert.False(plain[0].IsFullScan);

        var scanned = Results.ResultSetBuilder.BuildResultSets(results, "select * from a", snapshot: null);
        ScanConfirmation.Mark(scanned, isFullScan: true);
        Assert.True(scanned[0].IsFullScan);
    }
}
