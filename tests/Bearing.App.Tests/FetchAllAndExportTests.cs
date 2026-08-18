using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Results;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Fetch all rows, and the export that depends on it. Driven through <see cref="ExecutionViewModel"/> with
/// <see cref="PageableExecutor"/> — no live database, no grid.
/// <para>
/// The load-bearing behaviour here is that <b>export never writes half a result</b>: it fetches to the end
/// first and abandons the export if that doesn't finish, because a file containing the 10 rows that happened
/// to be on screen is indistinguishable from a complete one once it leaves the app.
/// </para>
/// </summary>
public class FetchAllAndExportTests : IDisposable
{
    private const int Page = 10;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-fetchall", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private sealed record Harness(
        ExecutionViewModel Exec,
        ResultSetViewModel Result,
        EditorTabViewModel Tab,
        PageableExecutor Executor,
        FakeDialogs Dialogs,
        SettingsService Settings,
        Func<string> Status);

    /// <summary>A tab that has run one pageable query, with <paramref name="totalRows"/> rows behind it.</summary>
    private async Task<Harness> RunOnce(int totalRows)
    {
        Directory.CreateDirectory(_root);
        var executor = new PageableExecutor { TotalRows = totalRows, PageSize = Page };
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
        var tab = new EditorTabViewModel("orders") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;
        ctx.Status = s => status = s;

        var dialogs = new FakeDialogs();
        var exec = new ExecutionViewModel(ctx, dialogs);
        await exec.ExecuteAsync("select n from t");
        return new Harness(exec, tab.LastResult!, tab, executor, dialogs, settings, () => status);
    }

    // ---- fetch all ---------------------------------------------------------------------------

    [Fact]
    public async Task Fetch_all_streams_to_the_end_and_the_total_is_then_known_without_counting()
    {
        var h = await RunOnce(totalRows: 25);
        Assert.Equal(Page, h.Result.Loaded);       // one page on screen …
        Assert.True(h.Result.HasMore);

        Assert.True(await h.Exec.FetchAllAsync(h.Result));

        Assert.Equal(25, h.Result.Loaded);          // … all of it after
        Assert.False(h.Result.HasMore);
        // The point of the change: one execution, not a page walk. Walking re-ran the query per page with a
        // growing OFFSET, and each page being its own statement is what let a concurrent write duplicate or
        // skip rows between them while the fetch still claimed to be complete.
        Assert.Equal(1, h.Executor.StreamCalls);
        Assert.Equal(0, h.Executor.PageCalls);
        // The count is now a fact rather than a query, so [Count] retires without asking the server.
        Assert.Equal(25, h.Result.TotalCount);
        Assert.False(h.Result.CanCount);
        Assert.Contains("Fetched all 25 rows", h.Status());
        Assert.Equal(Enumerable.Range(1, 25), h.Result.Rows.Select(r => (int)r[0]!)); // in order, no gaps
    }

    /// <summary>The single read starts past what is already on screen and is bounded by the ceiling, so the
    /// rows the first page fetched aren't transferred twice — and the server isn't asked for more than the
    /// fetch is allowed to keep. The <c>+ 1</c> is the probe that makes "there was more" detectable.</summary>
    [Fact]
    public async Task Fetch_all_asks_for_one_window_that_skips_the_loaded_rows_and_stops_at_the_ceiling()
    {
        var h = await RunOnce(totalRows: 25);

        Assert.True(await h.Exec.FetchAllAsync(h.Result));

        var cap = AppSettings.Defaults.ResultFetchAllMaxRows;
        Assert.Equal($"select n from t\nlimit {cap - Page + 1} offset {Page}", h.Executor.LastStreamSql);
    }

    [Fact]
    public async Task Fetch_all_on_an_already_complete_result_is_a_no_op_that_reports_success()
    {
        var h = await RunOnce(totalRows: 4); // fits in one page
        Assert.False(h.Result.HasMore);

        Assert.True(await h.Exec.FetchAllAsync(h.Result));
        Assert.Equal(0, h.Executor.StreamCalls);  // nothing was asked of the server
        Assert.Equal(0, h.Executor.PageCalls);
    }

    /// <summary>A read that dies partway is a failure, not a complete result. The page-walking version read
    /// the failure as an empty page — "no more rows" — and reported "Fetched all N rows", which then let an
    /// export write a file containing part of the answer.</summary>
    [Fact]
    public async Task A_read_that_fails_partway_is_reported_as_a_failure_not_as_a_complete_fetch()
    {
        var h = await RunOnce(totalRows: 1000);
        h.Executor.StreamError = new InvalidOperationException("connection reset");
        h.Executor.StreamErrorAfterBatches = 2;

        Assert.False(await h.Exec.FetchAllAsync(h.Result));

        Assert.Contains("Fetch all failed", h.Status());
        Assert.Contains("connection reset", h.Status());
        Assert.Equal(Page * 3, h.Result.Loaded);    // the batches that did land are kept
        Assert.True(h.Result.HasMore);              // and it doesn't pretend the result is exhausted
        Assert.Null(h.Result.TotalCount);
    }

    [Fact]
    public async Task Fetch_all_stops_at_the_configured_ceiling_and_says_so()
    {
        var h = await RunOnce(totalRows: 3000);
        // Lowered *after* the view-model was built: the ceiling is read per fetch, so a change in the settings
        // window applies to the next ⤓ all rather than the next launch (the catalog row carries no
        // AppliesNote, and an unmarked row promises immediacy). 1,000 is the descriptor's minimum — the
        // service clamps to the declared range, so a smaller number here would silently become this one.
        h.Settings.Set(SettingsCatalog.Find("results.fetchAllMaxRows")!, 1_000);

        // Reported as a stop, not a success: a truncated fetch that claimed to be complete would make the
        // row count — and anything exported from it — quietly wrong.
        Assert.False(await h.Exec.FetchAllAsync(h.Result));

        Assert.Contains("Stopped at", h.Status());
        Assert.Contains("Fetch all limit", h.Status());
        // Exactly the ceiling: the read asks for one row past it purely to notice there was more, and that
        // probe row is never materialized (the page-walking version overshot by up to a page).
        Assert.Equal(1_000, h.Result.Loaded);
        Assert.True(h.Result.HasMore);              // and it is honest about there being more
        Assert.Null(h.Result.TotalCount);
    }

    [Fact]
    public async Task Cancelling_a_fetch_keeps_the_rows_already_loaded()
    {
        var h = await RunOnce(totalRows: 1000);
        // Cancel deterministically at the third batch rather than racing a timer; a real driver surfaces the
        // cancel exactly here too.
        h.Executor.BeforeBatch = batch => { if (batch == 3) h.Tab.CancelRun(); };

        Assert.False(await h.Exec.FetchAllAsync(h.Result));

        Assert.Equal("Fetch all cancelled.", h.Status());
        Assert.Equal(Page * 3, h.Result.Loaded);    // the first page plus the two batches that completed
        Assert.True(h.Result.HasMore);
        Assert.False(h.Tab.IsRunning);              // the run is torn down, so the tab is usable again
    }

    // ---- export -----------------------------------------------------------------------------

    [Fact]
    public async Task Export_fetches_the_whole_result_first_so_the_file_is_not_half_the_answer()
    {
        var h = await RunOnce(totalRows: 25);
        var path = Path.Combine(_root, "out.csv");
        h.Dialogs.ExportPath = path;
        ExportCompletion? completed = null;
        h.Exec.ExportCompleted += c => completed = c;

        await h.Exec.ExportAsync(h.Result, ExportFormat.Csv);

        Assert.Equal(25, h.Result.Loaded);                       // paged to the end before writing
        var lines = File.ReadAllText(path).TrimEnd('\r', '\n').Split("\r\n");
        Assert.Equal("n", lines[0]);                             // header
        Assert.Equal(26, lines.Length);                          // header + 25 rows
        Assert.Equal("25", lines[^1]);
        Assert.Equal(new ExportCompletion(path, 25, ExportFormat.Csv), completed);
        Assert.Contains("Exported 25 rows", h.Status());
    }

    [Fact]
    public async Task An_export_whose_fetch_is_cancelled_writes_nothing_and_never_asks_for_a_path()
    {
        var h = await RunOnce(totalRows: 1000);
        h.Dialogs.ExportPath = Path.Combine(_root, "never.csv");
        h.Executor.BeforeBatch = batch => { if (batch == 2) h.Tab.CancelRun(); };

        await h.Exec.ExportAsync(h.Result, ExportFormat.Csv);

        Assert.Empty(h.Dialogs.ExportPickers);   // the picker is downstream of a complete fetch
        Assert.False(File.Exists(Path.Combine(_root, "never.csv")));
        Assert.Contains("isn't fully loaded", h.Status());
    }

    [Fact]
    public async Task Cancelling_the_save_picker_writes_nothing_and_raises_nothing()
    {
        var h = await RunOnce(totalRows: 4);
        h.Dialogs.ExportPath = null;             // the user dismissed the picker
        var raised = 0;
        h.Exec.ExportCompleted += _ => raised++;

        await h.Exec.ExportAsync(h.Result, ExportFormat.Xlsx);

        Assert.Single(h.Dialogs.ExportPickers);
        Assert.Equal(0, raised);
        Assert.Empty(Directory.GetFiles(_root, "*.xlsx"));
    }

    [Fact]
    public async Task The_picker_is_offered_a_name_and_type_that_match_the_format()
    {
        var h = await RunOnce(totalRows: 4);
        h.Dialogs.ExportPath = Path.Combine(_root, "book.xlsx");

        await h.Exec.ExportAsync(h.Result, ExportFormat.Xlsx);

        var (suggested, format) = h.Dialogs.ExportPickers.Single();
        Assert.Equal(ExportFormat.Xlsx, format);
        Assert.StartsWith("orders-", suggested);      // the tab's name (this result has no single table)
        Assert.EndsWith(".xlsx", suggested);
        Assert.True(File.Exists(Path.Combine(_root, "book.xlsx")));
    }

    [Fact]
    public async Task A_failed_write_is_reported_rather_than_thrown()
    {
        var h = await RunOnce(totalRows: 4);
        // A directory where the file should go: the write fails the way a full disk or a read-only folder
        // would, and (§5.2) that must reach the status bar, not the crash handler.
        var path = Path.Combine(_root, "taken");
        Directory.CreateDirectory(path);
        h.Dialogs.ExportPath = path;
        var raised = 0;
        h.Exec.ExportCompleted += _ => raised++;

        await h.Exec.ExportAsync(h.Result, ExportFormat.Csv);

        Assert.Contains("Export failed", h.Status());
        Assert.Equal(0, raised);
    }
}
