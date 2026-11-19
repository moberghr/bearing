using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Results;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Bearing.Results;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Load more and fetch all for a result the engine continues by token (DynamoDB's <c>NextToken</c>), driven
/// through <see cref="ExecutionViewModel"/> with <see cref="TokenExecutor"/> — no live database, no grid
/// (SC12). The offset path's own tests (<see cref="FetchAllAndExportTests"/>) pin the other engines.
/// <para>
/// The load-bearing claims: every trip after the first page goes through
/// <see cref="IQueryExecutor.ExecuteContinuationAsync"/> and nothing else — the SQL is never re-run and no
/// offset page is ever built — and whether more rows exist is the token's answer, not the row count's.
/// </para>
/// </summary>
public class ContinuationPagingTests : IDisposable
{
    private const int Page = 10;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-continuation", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private sealed record Harness(
        ExecutionViewModel Exec,
        ResultSetViewModel Result,
        EditorTabViewModel Tab,
        TokenExecutor Executor,
        FakeDialogs Dialogs,
        SettingsService Settings,
        Func<string> Status);

    /// <summary>A tab that has run one statement whose read continues by token.</summary>
    private async Task<Harness> RunOnce(TokenExecutor executor)
    {
        Directory.CreateDirectory(_root);
        var settings = SettingsService.InMemory(new AppSettings
        {
            AutosaveMode = AutosaveMode.Off,
            ResultPageSize = Page,
            ResultFetchAllMaxRows = AppSettings.Defaults.ResultFetchAllMaxRows,
        });
        var ctx = new WorkspaceContext(
            new FakeProvider { Executor = executor },
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            settings: settings);
        var conn = new ConnectionInfo { Id = Guid.NewGuid(), Name = "c", ProviderId = "postgres", Database = "app" };
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };

        var status = "";
        var tab = new EditorTabViewModel("items") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;
        ctx.Status = s => status = s;

        var dialogs = new FakeDialogs();
        var exec = new ExecutionViewModel(ctx, dialogs);
        await exec.ExecuteAsync("select * from \"Items\"");

        return new Harness(exec, tab.LastResult!, tab, executor, dialogs, settings, () => status);
    }

    private Task<Harness> RunOnce(int totalRows) => RunOnce(new TokenExecutor { TotalRows = totalRows });

    // ---- HasMore follows the token ---------------------------------------------------------------

    /// <summary>A page the request budget cut short holds fewer rows than a page and still has more behind
    /// it. Reading "more" off the row count would retire load more on exactly the result that needs it.</summary>
    [Fact]
    public async Task A_short_first_page_with_a_token_still_has_more()
    {
        var h = await RunOnce(new TokenExecutor { TotalRows = 50, FirstPageRows = 3 });

        Assert.Equal(3, h.Result.Loaded);
        Assert.Equal("t1", h.Result.Continuation);
        Assert.True(h.Result.HasMore);
        Assert.True(h.Result.IsPageable);
    }

    [Fact]
    public async Task A_full_first_page_without_a_token_has_no_more()
    {
        var h = await RunOnce(totalRows: Page); // exactly one page, and the engine says the read ended

        Assert.Equal(Page, h.Result.Loaded);
        Assert.Null(h.Result.Continuation);
        Assert.False(h.Result.HasMore);

        await h.Exec.LoadMoreAsync(h.Result);
        Assert.True(await h.Exec.FetchAllAsync(h.Result));
        Assert.Empty(h.Executor.Continued);
        Assert.Equal(0, h.Executor.OffsetCalls);
    }

    // ---- load more -------------------------------------------------------------------------------

    [Fact]
    public async Task Load_more_sends_only_the_token_and_takes_the_next_one()
    {
        var h = await RunOnce(totalRows: 25);

        await h.Exec.LoadMoreAsync(h.Result);

        Assert.Equal(["t1"], h.Executor.Continued);
        Assert.Equal([Page], h.Executor.MaxRowsAsked);
        Assert.Equal(1, h.Executor.ExecuteCalls);      // the statement was not run again
        Assert.Equal(0, h.Executor.OffsetCalls);       // and no LIMIT/OFFSET page was built or streamed
        Assert.Equal(2 * Page, h.Result.Loaded);
        Assert.Equal("t2", h.Result.Continuation);
        Assert.True(h.Result.HasMore);
        Assert.Equal(Enumerable.Range(1, 20), h.Result.Rows.Select(r => (int)r[0]!)); // appended, not re-read
    }

    [Fact]
    public async Task The_last_page_clears_the_token_and_load_more_then_asks_nothing()
    {
        var h = await RunOnce(totalRows: 15);

        await h.Exec.LoadMoreAsync(h.Result);
        Assert.Equal(15, h.Result.Loaded);
        Assert.Null(h.Result.Continuation);
        Assert.False(h.Result.HasMore);

        await h.Exec.LoadMoreAsync(h.Result);
        Assert.Single(h.Executor.Continued);
        Assert.Equal(0, h.Executor.OffsetCalls);
    }

    /// <summary>A rejected or failed page is reported and changes nothing — the rows on screen, the token and
    /// HasMore all stay, so the next scroll retries the same token (spec: an expired token reads as
    /// "Load more failed: …").</summary>
    [Fact]
    public async Task A_failed_page_leaves_the_rows_and_the_token_untouched()
    {
        var h = await RunOnce(totalRows: 25);
        h.Executor.ContinuationError = new QueryError("The provided token has expired.", null, null);

        await h.Exec.LoadMoreAsync(h.Result);

        Assert.Contains("Load more failed", h.Status());
        Assert.Contains("token has expired", h.Status());
        Assert.Equal(Page, h.Result.Loaded);
        Assert.Equal("t1", h.Result.Continuation);
        Assert.True(h.Result.HasMore);
        Assert.False(h.Tab.IsRunning);

        h.Executor.ContinuationError = null;
        await h.Exec.LoadMoreAsync(h.Result);
        Assert.Equal(["t1", "t1"], h.Executor.Continued);   // the retry sends the same token
        Assert.Equal(2 * Page, h.Result.Loaded);
    }

    [Fact]
    public async Task A_page_cut_short_by_the_request_budget_says_so_in_the_status_line()
    {
        var h = await RunOnce(totalRows: 25);
        h.Executor.ShortPage = 4;

        await h.Exec.LoadMoreAsync(h.Result);

        Assert.Equal(Page + 4, h.Result.Loaded);
        Assert.True(h.Result.HasMore);
        // The engine's sentence is host-neutral (W1a); the App adds how to read on.
        Assert.Equal($"{TokenExecutor.Notice} {ContinuationPaging.LoadMoreContinues}", h.Status());
    }

    [Fact]
    public async Task A_first_page_cut_short_by_the_request_budget_says_so_in_the_run_summary()
    {
        var h = await RunOnce(new TokenExecutor { TotalRows = 50, FirstPageRows = 2, FirstPageNotice = true });

        Assert.Contains("Done", h.Status());
        Assert.Contains($"{TokenExecutor.Notice} {ContinuationPaging.LoadMoreContinues}", h.Status());
    }

    [Fact]
    public async Task A_page_cancelled_by_the_user_reports_a_cancel_and_keeps_the_token()
    {
        var h = await RunOnce(totalRows: 25);
        h.Executor.BeforeContinuation = _ => h.Tab.CancelRun();

        await h.Exec.LoadMoreAsync(h.Result);

        Assert.Equal("Load cancelled.", h.Status());
        Assert.Equal(Page, h.Result.Loaded);
        Assert.Equal("t1", h.Result.Continuation);
    }

    // ---- fetch all -------------------------------------------------------------------------------

    [Fact]
    public async Task Fetch_all_follows_the_token_to_the_end_without_an_offset_read()
    {
        var h = await RunOnce(totalRows: 35);

        Assert.True(await h.Exec.FetchAllAsync(h.Result));

        Assert.Equal(["t1", "t2", "t3"], h.Executor.Continued);
        Assert.Equal(1, h.Executor.ExecuteCalls);
        Assert.Equal(0, h.Executor.OffsetCalls);
        Assert.Equal(35, h.Result.Loaded);
        Assert.Null(h.Result.Continuation);
        Assert.False(h.Result.HasMore);
        Assert.Equal(35, h.Result.TotalCount);
        Assert.Contains("Fetched all 35 rows", h.Status());
        Assert.Equal(Enumerable.Range(1, 35), h.Result.Rows.Select(r => (int)r[0]!));
    }

    [Fact]
    public async Task Fetch_all_stops_at_the_ceiling_and_says_so()
    {
        var h = await RunOnce(totalRows: 3000);
        h.Settings.Set(SettingsCatalog.Find("results.fetchAllMaxRows")!, 1_000);

        Assert.False(await h.Exec.FetchAllAsync(h.Result));

        Assert.Contains("Stopped at", h.Status());
        Assert.Contains("Fetch all limit", h.Status());
        Assert.Equal(1_000, h.Result.Loaded);          // exactly the ceiling: the last ask was cut to the room left
        Assert.NotNull(h.Result.Continuation);         // the token is kept, so the result is honest about more
        Assert.True(h.Result.HasMore);
        Assert.Null(h.Result.TotalCount);
        Assert.Equal(0, h.Executor.OffsetCalls);
        Assert.All(h.Executor.MaxRowsAsked, asked => Assert.InRange(asked, 1, Page));
    }

    [Fact]
    public async Task A_fetch_that_fails_partway_keeps_what_landed_and_is_not_complete()
    {
        var h = await RunOnce(totalRows: 100);
        h.Executor.BeforeContinuation = call =>
        {
            if (call == 3) h.Executor.ContinuationError = new QueryError("Throughput exceeded.", null, null);
        };

        Assert.False(await h.Exec.FetchAllAsync(h.Result));

        Assert.Contains("Fetch all failed", h.Status());
        Assert.Contains("Throughput exceeded", h.Status());
        Assert.Equal(3 * Page, h.Result.Loaded);      // first page + the two continuations that landed
        Assert.True(h.Result.HasMore);
        Assert.Null(h.Result.TotalCount);
    }

    [Fact]
    public async Task Export_follows_the_token_to_the_end_before_writing()
    {
        var h = await RunOnce(totalRows: 25);
        var path = Path.Combine(_root, "items.csv");
        h.Dialogs.ExportPath = path;

        await h.Exec.ExportAsync(h.Result, ExportFormat.Csv);

        Assert.Equal(25, h.Result.Loaded);
        Assert.Equal(0, h.Executor.OffsetCalls);
        var lines = File.ReadAllText(path).TrimEnd('\r', '\n').Split("\r\n");
        Assert.Equal(26, lines.Length);               // header + every row, not the page on screen
    }

    // ---- building --------------------------------------------------------------------------------

    /// <summary>The token carries its own statement, so each set of a batch pages on its own — the offset
    /// rule's "one set only" exists because an OFFSET over a whole batch is meaningless.</summary>
    [Fact]
    public void Every_set_of_a_batch_that_carries_a_token_is_pageable()
    {
        var columns = new[] { new ColumnDescriptor("pk", "S", typeof(string)) };
        var rows = new[] { new object?[] { "a" } };
        var results = new[]
        {
            new QueryResult(columns, rows, 1, TimeSpan.Zero, null, null, Truncated: true) { Continuation = "x", StatementIndex = 0 },
            new QueryResult(columns, rows, 1, TimeSpan.Zero, null, null, Truncated: false) { StatementIndex = 1 },
        };

        var sets = ResultSetBuilder.BuildResultSets(results, "select * from a; select * from b", snapshot: null);

        Assert.True(sets[0].IsPageable);
        Assert.True(sets[0].HasMore);
        Assert.Equal("x", sets[0].Continuation);
        Assert.False(sets[1].IsPageable);              // no token, and not the only set: unchanged
        Assert.False(sets[1].HasMore);
        Assert.False(sets[0].IsFullScan);
    }

    /// <summary>
    /// An executor whose reads continue by token, as DynamoDB's does: the first page carries <c>t1</c>, each
    /// continuation the next, and the last none. Rows are numbered from 1 so order and gaps are assertable.
    /// Records every call; the offset entry points (<see cref="ExecutePageAsync"/>,
    /// <see cref="StreamRowsAsync"/>, <see cref="CountAsync"/>) only count, so a test can pin them at zero.
    /// </summary>
    internal sealed class TokenExecutor : IQueryExecutor
    {
        public const string Notice = "This page stopped after 10 requests: the read has not reached the end yet.";

        private static readonly ColumnDescriptor[] Columns = [new("n", "N", typeof(int))];

        private int _served;
        private int _tokens;

        public int TotalRows { get; init; }

        /// <summary>Rows the first page returns (default: a full page).</summary>
        public int? FirstPageRows { get; init; }

        /// <summary>The first page reports itself cut short by the request budget.</summary>
        public bool FirstPageNotice { get; init; }

        /// <summary>When set, the next continuation returns this many rows and the budget notice.</summary>
        public int? ShortPage { get; set; }

        /// <summary>When set, every continuation comes back as a failed result carrying it — how the
        /// DynamoDB executor reports a failed page (it does not throw one).</summary>
        public QueryError? ContinuationError { get; set; }

        /// <summary>Invoked with the 1-based continuation number before it is served.</summary>
        public Action<int>? BeforeContinuation { get; set; }

        public int ExecuteCalls { get; private set; }
        public int OffsetCalls { get; private set; }
        public List<string> Continued { get; } = new();
        public List<int> MaxRowsAsked { get; } = new();

        private QueryResult Next(int want, bool notice)
        {
            var take = Math.Max(0, Math.Min(want, TotalRows - _served));
            var rows = new object?[take][];
            for (var i = 0; i < take; i++) rows[i] = new object?[] { _served + i + 1 };
            _served += take;
            var token = _served < TotalRows ? $"t{++_tokens}" : null;

            return new QueryResult(Columns, rows, take, TimeSpan.Zero, notice && token is not null ? Notice : null, null,
                Truncated: token is not null)
            {
                Continuation = token,
                StatementIndex = 0,
            };
        }

        public Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
        {
            ExecuteCalls++;
            _served = 0;
            _tokens = 0;

            return Task.FromResult<IReadOnlyList<QueryResult>>([Next(FirstPageRows ?? options.MaxRows ?? Page, FirstPageNotice)]);
        }

        public Task<QueryResult> ExecuteContinuationAsync(string continuation, int maxRows, CancellationToken ct)
        {
            Continued.Add(continuation);
            MaxRowsAsked.Add(maxRows);
            BeforeContinuation?.Invoke(Continued.Count);
            ct.ThrowIfCancellationRequested();
            if (ContinuationError is { } error)
            {
                return Task.FromResult(new QueryResult(
                    Array.Empty<ColumnDescriptor>(), Array.Empty<object?[]>(), 0, TimeSpan.Zero, null, error, Truncated: false));
            }

            var shortPage = ShortPage;
            ShortPage = null;

            return Task.FromResult(Next(shortPage ?? maxRows, shortPage is not null));
        }

        public Task<QueryResult> ExecutePageAsync(string pageSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
        {
            OffsetCalls++;

            return Task.FromResult(Next(Page, false));
        }

        public async IAsyncEnumerable<RowBatch> StreamRowsAsync(
            string sql, QueryOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            OffsetCalls++;
            await Task.CompletedTask;
            yield break;
        }

        public Task<long?> CountAsync(string countSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
            => Task.FromResult<long?>(null);

        public Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
