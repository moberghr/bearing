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
using Bearing.Core.Logging;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Bearing.Results;
using Bearing.Sessions;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// A batch's sets appear as they are read (§5.8): each grid when its first rows arrive, filling in behind them,
/// with Esc keeping what arrived. The assembler's bookkeeping is tested directly; the run is driven end to end
/// through <see cref="ExecutionViewModel"/> with an executor that pauses between sets on a gate.
/// </summary>
public class BatchStreamingTests : IDisposable
{
    private static readonly ColumnDescriptor[] Cols = [new("n", "int4", typeof(int))];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-stream", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private static object?[][] Rows(int from, int count)
        => Enumerable.Range(from, count).Select(n => new object?[] { n }).ToArray();

    private static QueryResult Set(object?[][] rows, bool truncated = false)
        => new(Cols, rows, rows.Length, TimeSpan.FromMilliseconds(7), null, null, truncated);

    private static BatchRunAssembler Assembler(List<(IReadOnlyList<ResultSetViewModel> Sets, bool First)> published)
        => new("select 1; select 2;", ProviderTraits.Postgres, connectionReadOnly: false, run: null,
            (sets, first) => published.Add((sets, first)), () => TimeSpan.FromMilliseconds(3));

    // ---- the assembler ---------------------------------------------------------------------------

    [Fact]
    public void Each_set_is_published_when_it_begins_and_fills_in_as_rows_arrive()
    {
        var published = new List<(IReadOnlyList<ResultSetViewModel> Sets, bool First)>();
        var a = Assembler(published);

        a.Start(new ResultStarted(0, Cols), snapshot: null);
        Assert.Single(published);
        Assert.True(published[0].First);                 // a run's first set replaces the pane …
        var first = published[0].Sets[0];
        Assert.True(first.IsReading);
        Assert.Equal("0 rows so far…", first.RowCountText);

        a.Rows(new ResultRows(0, Rows(1, 3)));
        Assert.Equal(3, first.Loaded);
        Assert.False(first.IsCutShort);                  // still reading: neither complete nor cut short yet

        a.End(new ResultEnded(0, Truncated: false));
        Assert.False(first.IsReading);
        Assert.False(first.HasMore);

        a.Start(new ResultStarted(1, Cols), snapshot: null);
        Assert.False(published[1].First);                // … later ones extend the same run
        Assert.Equal(2, published[1].Sets.Count);
        Assert.Same(first, published[1].Sets[0]);        // the set already on screen is not rebuilt
    }

    /// <summary>A fast set's batches are held and handed to the grid a few times a second, not one by one: each
    /// hand-over rebuilds the grid's view of the whole set, and doing that per batch froze the window for seconds
    /// on a 150,000-row set. The first rows still show at once, and nothing held is lost at the set's end.</summary>
    [Fact]
    public void Rows_reach_the_grid_at_most_every_flush_interval_and_all_of_them_by_the_end()
    {
        var now = TimeSpan.Zero;
        var a = new BatchRunAssembler("select 1; select 2;", ProviderTraits.Postgres, false, null, (_, _) => { }, () => now);
        a.Start(new ResultStarted(0, Cols), null);
        var set = a.Reading!;

        Assert.True(a.Rows(new ResultRows(0, Rows(1, 10))));       // empty grid: shown at once
        Assert.Equal(10, set.Loaded);

        now += TimeSpan.FromMilliseconds(50);
        Assert.False(a.Rows(new ResultRows(0, Rows(11, 10))));     // inside the interval: held
        Assert.False(a.Rows(new ResultRows(0, Rows(21, 10))));
        Assert.Equal(10, set.Loaded);
        Assert.Equal(20, a.Pending);                              // still counted in progress

        now += BatchRunAssembler.FlushInterval;
        Assert.True(a.Rows(new ResultRows(0, Rows(31, 10))));      // interval passed: everything held lands
        Assert.Equal(40, set.Loaded);

        Assert.False(a.Rows(new ResultRows(0, Rows(41, 5))));
        a.End(new ResultEnded(0, false));                          // the set's end flushes the rest
        Assert.Equal(Enumerable.Range(1, 45), set.Rows.Select(r => (int)r[0]!));
    }

    [Fact]
    public void A_set_the_ceiling_cut_ends_cut_short()
    {
        var a = Assembler([]);
        a.Start(new ResultStarted(0, Cols), null);
        a.Rows(new ResultRows(0, Rows(1, 5)));
        a.End(new ResultEnded(0, Truncated: true));

        var set = a.Complete([Set(Rows(1, 5), truncated: true)], null).Single();
        Assert.True(set.IsCutShort);
        Assert.False(set.IsPageable);
    }

    /// <summary>Before streaming, an error in a later statement threw away every set read before it — the
    /// executors reported the failure alone. Now the sets stay, the one it interrupted is marked cut short,
    /// and the error follows it.</summary>
    [Fact]
    public void An_error_part_way_keeps_the_earlier_sets_and_follows_the_one_it_cut_off()
    {
        var published = new List<(IReadOnlyList<ResultSetViewModel> Sets, bool First)>();
        var a = Assembler(published);
        a.Start(new ResultStarted(0, Cols), null);
        a.Rows(new ResultRows(0, Rows(1, 3)));
        a.End(new ResultEnded(0, false));
        a.Start(new ResultStarted(1, Cols), null);
        a.Rows(new ResultRows(1, Rows(1, 2)));

        var failure = new QueryResult([], [], 0, TimeSpan.Zero, null, new QueryError("division by zero", "22012", null), false);
        var final = a.Complete([Set(Rows(1, 3)), failure], null);

        Assert.Equal(3, final.Count);
        Assert.False(final[0].IsCutShort);
        Assert.Equal(3, final[0].Loaded);
        Assert.True(final[1].IsCutShort);                // the interrupted set keeps its 2 rows, honestly marked
        Assert.Equal(2, final[1].Loaded);
        Assert.Equal("division by zero", final[2].Error?.Message);
        Assert.Same(final, published[^1].Sets);          // and the pane gets the list with the error in it
    }

    [Fact]
    public void Results_without_rows_are_added_in_run_order_once_the_run_is_over()
    {
        var a = Assembler([]);
        a.Start(new ResultStarted(1, Cols), null);
        a.Rows(new ResultRows(1, Rows(1, 2)));
        a.End(new ResultEnded(1, false));

        var count = new QueryResult([], [], 4, TimeSpan.Zero, "4 row(s) affected", null, false);
        var final = a.Complete([count, Set(Rows(1, 2))], null);

        Assert.Equal(2, final.Count);
        Assert.Equal("4 row(s) affected", final[0].Message);
        Assert.Equal(2, final[1].Loaded);
    }

    [Fact]
    public void Abandoning_marks_only_the_set_being_read()
    {
        var a = Assembler([]);
        a.Start(new ResultStarted(0, Cols), null);
        a.Rows(new ResultRows(0, Rows(1, 3)));
        a.End(new ResultEnded(0, false));
        a.Start(new ResultStarted(1, Cols), null);
        var reading = a.Reading!;
        a.Rows(new ResultRows(1, Rows(1, 1)));

        a.Abandon();

        Assert.False(reading.IsReading);
        Assert.True(reading.IsCutShort);
        Assert.Equal("first 1 rows, more not loaded", reading.RowCountText);
    }

    // ---- end to end through the view-model -------------------------------------------------------

    /// <summary>Plays a two-set batch, waiting on <see cref="Gate"/> after the first set has ended — or, with
    /// <see cref="HangInFirstSet"/>, mid-way through it until cancelled.</summary>
    private sealed class GatedExecutor : IQueryExecutor
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HangInFirstSet { get; init; }

        public async IAsyncEnumerable<BatchEvent> ExecuteStreamingAsync(
            string sql, QueryOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new ResultStarted(0, Cols);
            yield return new ResultRows(0, Rows(1, 3));
            if (HangInFirstSet) await Task.Delay(Timeout.Infinite, ct);
            yield return new ResultEnded(0, false);
            await Gate.Task.WaitAsync(ct);
            yield return new ResultStarted(1, Cols);
            yield return new ResultRows(1, Rows(1, 4));
            yield return new ResultEnded(1, false);
            yield return new BatchCompleted([Set(Rows(1, 3)), Set(Rows(1, 4))]);
        }

        public Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
            => throw new InvalidOperationException("a batch must stream");
        public Task<QueryResult> ExecutePageAsync(string pageSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
            => throw new NotSupportedException();
        public IAsyncEnumerable<RowBatch> StreamRowsAsync(string sql, QueryOptions options, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<long?> CountAsync(string countSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private (ExecutionViewModel Exec, EditorTabViewModel Tab, FakeDialogs Dialogs, SqliteQueryLog Log, Func<string> Status)
        Shell(IQueryExecutor executor)
    {
        Directory.CreateDirectory(_root);
        var log = new SqliteQueryLog(Path.Combine(_root, "log.sqlite"));
        var ctx = new WorkspaceContext(
            new FakeProvider { Executor = executor },
            new JsonProjectStore(),
            new JsonSessionStore(),
            log,
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off, ResultPageSize = 10 }));
        var conn = new ConnectionInfo { Id = Guid.NewGuid(), Name = "c", ProviderId = "postgres", Database = "app" };
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };
        var status = "";
        var tab = new EditorTabViewModel("script") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;
        ctx.Status = s => status = s;
        var dialogs = new FakeDialogs();
        return (new ExecutionViewModel(ctx, dialogs), tab, dialogs, log, () => status);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task The_first_set_is_on_screen_while_the_second_has_not_begun()
    {
        var executor = new GatedExecutor();
        var (exec, tab, _, _, status) = Shell(executor);

        var streamed = 0;
        tab.ResultsStreamed += () => streamed++;   // what tells the pane to draw before the run ends
        var run = exec.ExecuteAsync("select n from a;\nselect n from b;");
        await Until(() => tab.Results.Count == 1 && !tab.Results[0].IsReading);

        Assert.Equal(1, streamed);
        Assert.Equal(3, tab.Results[0].Loaded);
        Assert.True(tab.IsRunning);
        Assert.Contains("Reading result 1", status());

        executor.Gate.SetResult();
        await run;

        Assert.Equal(2, streamed);
        Assert.False(tab.IsRunning);
        Assert.Equal([3, 4], tab.Results.Select(r => r.Loaded));
        Assert.All(tab.Results, r => Assert.False(r.HasMore));
        Assert.Contains("c ·", status());
    }

    [Fact]
    public async Task Esc_mid_set_keeps_its_rows_marks_it_cut_short_logs_the_run_and_refuses_export()
    {
        var executor = new GatedExecutor { HangInFirstSet = true };
        var (exec, tab, dialogs, log, status) = Shell(executor);

        var run = exec.ExecuteAsync("select n from a;\nselect n from b;");
        await Until(() => tab.Results.Count == 1 && tab.Results[0].Loaded == 3);
        exec.CancelExecution();
        await run;

        var set = Assert.Single(tab.Results);
        Assert.Equal(3, set.Loaded);
        Assert.True(set.IsCutShort);
        Assert.Contains("cancelled", status(), StringComparison.OrdinalIgnoreCase);

        // Whatever the batch did before Esc reached the server, so the audit trail has it.
        var logged = Assert.Single(await log.SearchAsync(new QueryLogQuery(), CancellationToken.None));
        Assert.False(logged.Success);
        Assert.Contains("select n from b", logged.SqlText);

        dialogs.ExportPath = Path.Combine(_root, "out.csv");
        await exec.ExportAsync(set, ExportFormat.Csv);
        Assert.False(File.Exists(dialogs.ExportPath));
        Assert.Contains("Export stopped", status());
    }
}
