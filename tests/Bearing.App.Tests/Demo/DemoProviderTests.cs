using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Results;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Demo;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// What the demo provider gives the UI (#63): result shapes the app's own machinery recognises, and the
/// awkward paths — streaming, the row ceiling, a failing count, a write batch — behaving like the real one.
/// <para>
/// These are assertions about the <i>fixtures being usable</i>, not about resolution being correct. Whether
/// Postgres really reports an FK the way <see cref="DemoCatalog"/> claims belongs in <c>Bearing.Data.Tests</c>
/// against live pagila (§4.6) — asserting the fixture's assumptions back would be a green suite over a
/// broken app.
/// </para>
/// </summary>
public class DemoProviderTests
{
    private static readonly QueryOptions Options = new();

    // ---- the fixtures reach the app's own machinery -----------------------------------------------

    [Fact]
    public void A_demo_result_arrives_at_the_grid_editable_with_its_keys_marked()
    {
        // The whole reason for declaring column origins rather than setting the view model's flags: the real
        // ResultSetBuilder, resolvers included, is what runs.
        var sets = ResultSetBuilder.BuildResultSets(
            [DemoCatalog.Payments()], "select * from shop.payment", DemoCatalog.Snapshot());

        var payments = Assert.Single(sets);
        Assert.True(payments.IsEditable);
        Assert.Equal([0], payments.PrimaryKeyColumns);
        Assert.Equal([1], payments.ForeignKeyColumns);   // store_id
        Assert.Equal("payment", payments.EditTarget!.Table);
    }

    [Fact]
    public void The_foreign_key_column_has_nulls_in_it()
    {
        // #61's repro has to be in the data, not just in the schema: a dimmed-italic NULL in an FK column is
        // only reachable if some row actually has one.
        var payments = DemoCatalog.Payments();

        Assert.Contains(payments.Rows, row => row[1] is null);
        Assert.Contains(payments.Rows, row => row[1] is not null);
    }

    [Fact]
    public void A_view_result_resolves_read_only_with_a_reason()
    {
        // Real origins, no primary key among them — the lock chip's path, which an origin-less fake cannot
        // reach at all.
        var sets = ResultSetBuilder.BuildResultSets(
            [DemoCatalog.ReceiptView()], "select * from shop.receipt", DemoCatalog.Snapshot());

        var receipts = Assert.Single(sets);
        Assert.False(receipts.IsEditable);
        Assert.NotNull(receipts.LockReason);
    }

    [Fact]
    public void An_aggregate_is_a_grid_and_nothing_more()
    {
        var sets = ResultSetBuilder.BuildResultSets(
            [DemoCatalog.Aggregate()], "select store_id, count(*) from shop.payment group by 1",
            DemoCatalog.Snapshot());

        var aggregate = Assert.Single(sets);
        Assert.False(aggregate.IsEditable);
        Assert.Empty(aggregate.PrimaryKeyColumns);
        Assert.Empty(aggregate.ForeignKeyColumns);
        Assert.True(aggregate.HasGrid);
    }

    [Fact]
    public void Foreign_key_navigation_has_somewhere_to_land()
    {
        var snapshot = DemoCatalog.Snapshot();

        var target = ForeignKeyResolver.Resolve(snapshot, DemoCatalog.Payments().Columns, clickedColumn: 1);

        Assert.NotNull(target);
        Assert.Equal("store", snapshot.Tables.Single(t => t.Id == DemoCatalog.StoreId).Name);
        // And the landing table is one the executor can actually serve, or the navigation dead-ends.
        Assert.Contains("Vukovar", DemoCatalog.Stores().Rows.Select(r => r[1]));
    }

    [Fact]
    public void A_run_covers_the_shapes_that_render_differently()
    {
        var run = DemoCatalog.Run();

        Assert.Contains(run, r => r.Columns.Count > 0 && r.Success);   // a grid
        Assert.Contains(run, r => r.Message is not null);              // a rows-affected message
        Assert.Contains(run, r => !r.Success);                         // an error
    }

    [Fact]
    public void The_fixtures_are_the_same_every_time()
    {
        // Captures get diffed and assertions count rows, so a clock or a GUID in here would be a flake.
        Assert.Equal(
            Rendered(DemoCatalog.Run()),
            Rendered(DemoCatalog.Run()));

        static string Rendered(IReadOnlyList<QueryResult> run) => string.Join("|", run.Select(r =>
            $"{r.Duration.Ticks}:{r.Message}:{r.Error?.Message}:"
            + string.Join(",", r.Rows.Select(row => string.Join("/", row)))));
    }

    // ---- the executor behaves like a real one ----------------------------------------------------

    [Fact]
    public async Task It_answers_the_table_the_query_mentions()
    {
        var executor = DemoExecutor.Default();

        var payments = await executor.ExecuteAsync("select * from shop.payment", Options, default);
        var stores = await executor.ExecuteAsync("select * from shop.store", Options, default);

        Assert.Equal("store_id", payments[0].Columns[1].Name);
        Assert.Equal("name", stores[0].Columns[1].Name);
        Assert.Equal(2, executor.Executed.Count);
    }

    [Fact]
    public async Task A_multi_statement_run_returns_one_result_per_statement()
    {
        // The welcome script tells the user to run two statements together, and the real executor returns two
        // result sets. Matching the whole batch as one string returned a single payments grid, so the demo's
        // own instruction produced the wrong answer.
        var executor = DemoExecutor.Default(Split);

        var results = await executor.ExecuteAsync(
            "select * from shop.payment; select * from shop.store;", Options, default);

        Assert.Equal(2, results.Count);
        Assert.Equal("store_id", results[0].Columns[1].Name);
        Assert.Equal("name", results[1].Columns[1].Name);
    }

    [Fact]
    public async Task Without_a_splitter_a_batch_is_one_statement()
    {
        // The default, for a caller that has no lexer to hand — Bearing.Demo may not reference Bearing.Sql.
        var results = await DemoExecutor.Default().ExecuteAsync(
            "select * from shop.payment; select * from shop.store;", Options, default);

        Assert.Single(results);
    }

    [Fact]
    public async Task A_query_naming_one_relation_is_not_served_another_whose_name_it_contains()
    {
        // `select payment_id … from shop.receipt` contains "payment", so bare patterns handed the view's
        // query a payments grid — first registration wins, and both are plausible results, so the collision
        // was invisible.
        var executor = DemoExecutor.Default();

        var results = await executor.ExecuteAsync(
            "select payment_id, store_name from shop.receipt", Options, default);

        Assert.Equal(["payment_id", "store_name"], results[0].Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task A_scripted_entry_with_no_results_does_not_throw_on_the_single_result_paths()
    {
        // Serve("x") with no results compiles, and Fallback can be set empty. ExecuteAsync tolerated it;
        // the page, count and stream paths indexed straight into nothing.
        var executor = new DemoExecutor();
        executor.Serve("shop.payment");
        executor.Fallback = [];

        Assert.Empty((await executor.ExecutePageAsync("select * from shop.payment limit 1", default)).Rows);
        Assert.Equal(0, await executor.CountAsync("select * from shop.payment", default));
        await foreach (var _ in executor.StreamRowsAsync("select * from shop.payment", Options, default)) { }
    }

    [Fact]
    public async Task Recording_survives_concurrent_runs()
    {
        // One executor is shared by every tab in a demo session, so its record is appended from whatever
        // thread ran the query — an unguarded List can corrupt or throw.
        var executor = DemoExecutor.Default();

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
            executor.ExecuteAsync($"select * from shop.store -- {i}", Options, default))));

        Assert.Equal(40, executor.Executed.Count);
    }

    [Fact]
    public async Task The_record_does_not_grow_without_bound()
    {
        // A long demo session would otherwise retain every string it ever ran.
        var executor = DemoExecutor.Default();

        for (var i = 0; i < 260; i++)
            await executor.ExecuteAsync($"select {i} from shop.store", Options, default);

        Assert.Equal(200, executor.Executed.Count);
        // The recent past is what it keeps, not the distant one.
        Assert.Contains("select 259 from shop.store", executor.Executed);
        Assert.DoesNotContain("select 0 from shop.store", executor.Executed);
    }

    /// <summary>A stand-in for the real statement splitter, which lives in Bearing.Sql.</summary>
    private static IReadOnlyList<string> Split(string sql)
        => sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public async Task An_unscripted_query_gets_an_empty_grid_not_an_error()
    {
        // A red banner for every query a test did not think to script would make the harness fight the test.
        var executor = new DemoExecutor();

        var results = await executor.ExecuteAsync("select 1", Options, default);

        Assert.True(results[0].Success);
        Assert.Empty(results[0].Rows);
        Assert.NotEmpty(results[0].Columns);
    }

    [Fact]
    public async Task The_row_ceiling_truncates_and_says_so()
    {
        var executor = new DemoExecutor().Serve("payment", DemoCatalog.Payments(40));

        var results = await executor.ExecuteAsync(
            "select * from shop.payment", new QueryOptions { MaxRows = 10 }, default);

        Assert.Equal(10, results[0].Rows.Count);
        Assert.True(results[0].Truncated);
    }

    [Fact]
    public async Task Streaming_arrives_in_batches_in_row_order()
    {
        var executor = new DemoExecutor().Serve("payment", DemoCatalog.Payments(25));

        var batches = await Batches(executor, new QueryOptions { BatchRows = 10 });

        Assert.Equal([10, 10, 5], batches.Select(b => b.Rows.Count));
        // In order and complete: the ids run 1..25 across the batches with nothing dropped or repeated.
        Assert.Equal(
            Enumerable.Range(1, 25),
            batches.SelectMany(b => b.Rows).Select(r => Convert.ToInt32(r[0])));
        Assert.All(batches, b => Assert.False(b.Truncated));
    }

    [Fact]
    public async Task Streaming_marks_the_final_batch_when_the_ceiling_cut_it()
    {
        // The distinction the incremental read depends on: "this is the whole result" versus "this is where
        // the ceiling stopped".
        var executor = new DemoExecutor().Serve("payment", DemoCatalog.Payments(25));

        var batches = await Batches(executor, new QueryOptions { BatchRows = 10, MaxRows = 15 });

        Assert.Equal(15, batches.Sum(b => b.Rows.Count));
        Assert.True(batches[^1].Truncated);
        Assert.All(batches.SkipLast(1), b => Assert.False(b.Truncated));
    }

    [Fact]
    public async Task Streaming_a_result_that_fits_exactly_is_not_truncated()
    {
        var executor = new DemoExecutor().Serve("payment", DemoCatalog.Payments(20));

        var batches = await Batches(executor, new QueryOptions { BatchRows = 10, MaxRows = 20 });

        Assert.Equal(20, batches.Sum(b => b.Rows.Count));
        Assert.All(batches, b => Assert.False(b.Truncated));
    }

    [Fact]
    public async Task Streaming_stops_when_the_read_is_cancelled()
    {
        var executor = new DemoExecutor().Serve("payment", DemoCatalog.Payments(40));
        using var cts = new CancellationTokenSource();

        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var batch in executor.StreamRowsAsync(
                "select * from shop.payment", new QueryOptions { BatchRows = 5 }, cts.Token))
            {
                seen += batch.Rows.Count;
                if (seen >= 10) cts.Cancel();
            }
        });

        Assert.Equal(10, seen);
    }

    [Fact]
    public async Task A_page_query_comes_back_without_column_origins()
    {
        // As from the real provider: the base table of a wrapped paging query is not read. Keeping the
        // origins would make the UI look more capable on page two than it is.
        var executor = DemoExecutor.Default();

        var page = await executor.ExecutePageAsync(
            "select * from (select * from shop.payment) t limit 10 offset 10", default);

        Assert.All(page.Columns, c => Assert.False(c.HasBaseColumn));
        Assert.True(DemoCatalog.Payments().Columns.All(c => c.HasBaseColumn));
    }

    [Fact]
    public async Task A_count_can_be_a_number_a_blank_or_a_failure()
    {
        // Three distinct outcomes the UI renders differently, and all three have to be reachable.
        var executor = DemoExecutor.Default();
        Assert.Equal(40, await executor.CountAsync("select * from shop.payment", default));

        executor.Uncountable = true;
        Assert.Null(await executor.CountAsync("select * from shop.payment", default));

        executor.Uncountable = false;
        executor.CountError = new InvalidOperationException("connection lost");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.CountAsync("select * from shop.payment", default));
    }

    [Fact]
    public async Task A_write_batch_is_recorded_and_reported_per_command()
    {
        // What the inline-edit commit path hands down, so a test can assert the generated DML and its
        // parameters without a server (§5.4).
        var executor = DemoExecutor.Default();
        var commands = new SqlWriteCommand[]
        {
            new("update shop.payment set note = $1 where id = $2",
                [new SqlParameter("$1", "seen"), new SqlParameter("$2", 3)]),
            new("delete from shop.payment where id = $1", [new SqlParameter("$1", 4)]),
        };

        var results = await executor.ExecuteWriteAsync(commands, default);

        Assert.Equal(["UPDATE 1", "DELETE 1"], results.Select(r => r.Message));
        var recorded = Assert.Single(executor.Writes);
        Assert.Equal(2, recorded.Count);
        Assert.Equal("seen", recorded[0].Parameters[0].Value);
    }

    // ---- the provider ---------------------------------------------------------------------------

    [Fact]
    public async Task The_provider_serves_the_demo_catalog()
    {
        var provider = new DemoProvider();
        var factory = provider.CreateConnectionFactory(
            new ConnectionInfo { Id = Guid.NewGuid(), Name = "demo", ProviderId = "postgres" }, password: null);
        var metadata = provider.CreateMetadataReader(factory);

        Assert.True(await factory.TestConnectionAsync(default));
        Assert.Contains(DemoCatalog.Database, await metadata.GetDatabasesAsync(default));

        var snapshot = await metadata.LoadSnapshotAsync(DemoCatalog.Database, default);
        Assert.Equal(DemoCatalog.Database, snapshot.Database);
        Assert.NotNull(snapshot.ResolveTable(DemoCatalog.Schema, "payment"));
        Assert.NotEmpty(await metadata.GetRoutinesAsync(default));
    }

    [Fact]
    public async Task Another_database_gets_the_same_catalog_under_its_own_name()
    {
        // Sessions are keyed by (connection, database) (§9.4a), so a demo run has to be able to have two
        // databases without them being the same object.
        var metadata = new DemoProvider().CreateMetadataReader(new DemoConnectionFactory());

        var other = await metadata.LoadSnapshotAsync("postgres", default);

        Assert.Equal("postgres", other.Database);
        Assert.NotNull(other.ResolveTable(DemoCatalog.Schema, "payment"));
    }

    [Fact]
    public void Every_session_shares_one_executor()
    {
        // So a test scripts it once and can then assert what the UI asked for, however many tabs ran.
        var provider = new DemoProvider();

        var first = provider.CreateQueryExecutor(new DemoConnectionFactory());
        var second = provider.CreateQueryExecutor(new DemoConnectionFactory());

        Assert.Same(first, second);
        Assert.Same(provider.Executor, first);
    }

    private static async Task<List<RowBatch>> Batches(DemoExecutor executor, QueryOptions options)
    {
        var batches = new List<RowBatch>();
        await foreach (var batch in executor.StreamRowsAsync("select * from shop.payment", options, default))
            batches.Add(batch);
        return batches;
    }
}
