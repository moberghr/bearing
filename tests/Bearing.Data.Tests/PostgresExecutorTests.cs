using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Testing;
using Npgsql;
using Xunit;
using Xunit.Sdk;

namespace Bearing.Data.Tests;

/// <summary>
/// Integration tests against a live PostgreSQL loaded with pagila. Endpoint and defaults come from
/// <see cref="PgTestServer"/> (override with BEARING_TEST_PG_*). Skipped cleanly when no database is
/// reachable so the suite stays green off a dev box.
/// </summary>
public class PostgresExecutorTests
{
    private static ConnectionInfo Info() => PgTestServer.Info();
    private static string Password => PgTestServer.Password;

    [SkippableFact]
    public async Task Executes_a_select_and_returns_typed_rows()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        var results = await executor.ExecuteAsync(
            "select film_id, title from film order by film_id limit 5",
            new QueryOptions(), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(5, result.RowCount);
        Assert.Equal(new[] { "film_id", "title" }, result.Columns.Select(c => c.Name));
        Assert.Equal(1, Convert.ToInt32(result.Rows[0][0]));
        Assert.IsType<string>(result.Rows[0][1]);
    }

    [SkippableFact]
    public async Task Multi_statement_run_returns_a_result_set_per_statement()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        var results = await executor.ExecuteAsync(
            "select film_id from film order by film_id limit 3; select 42 as answer",
            new QueryOptions(), CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(3, results[0].RowCount);
        Assert.Equal("film_id", results[0].Columns[0].Name);
        Assert.Equal("answer", results[1].Columns[0].Name);
        Assert.Equal(42, Convert.ToInt32(results[1].Rows[0][0]));
    }

    [SkippableFact]
    public async Task Surfaces_sql_errors_as_a_query_error()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        var result = Assert.Single(await executor.ExecuteAsync(
            "select * from no_such_table_here", new QueryOptions(), CancellationToken.None));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("42P01", result.Error!.SqlState); // undefined_table
    }

    /// <summary>Null cells come back as CLR null, and a null never shifts the values around it. Pins the row
    /// loop's sync <c>IsDBNull</c> — valid only because a non-sequential reader has the whole row buffered by
    /// the time <c>ReadAsync</c> returns, which is exactly the assumption worth a test.</summary>
    [SkippableFact]
    public async Task Null_cells_materialize_as_null_without_disturbing_their_neighbours()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        var result = Assert.Single(await executor.ExecuteAsync(
            "select 1 as a, null::text as b, 'x' as c, null::int as d, 2 as e",
            new QueryOptions(), CancellationToken.None));

        Assert.True(result.Success, result.Error?.Message);
        var row = result.Rows[0];
        Assert.Equal(1, Convert.ToInt32(row[0]));
        Assert.Null(row[1]);
        Assert.Equal("x", row[2]);
        Assert.Null(row[3]);
        Assert.Equal(2, Convert.ToInt32(row[4]));
    }

    /// <summary>A count that can't be *shaped* reports null; a count that *fails* throws. Before this split
    /// every failure returned null, so a dropped table or a dead connection looked exactly like an
    /// uncountable query and the UI just showed no total.</summary>
    [SkippableFact]
    public async Task Count_reports_null_for_an_uncountable_shape_but_throws_on_a_real_failure()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);

        // Uncountable shapes — the wrap itself is invalid SQL, nothing is wrong with the server.
        Assert.Null(await executor.CountAsync("select 1; select 2;", CancellationToken.None));
        Assert.Null(await executor.CountAsync("update film set title = title", CancellationToken.None));

        // A real failure propagates instead of masquerading as "no total available".
        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => executor.CountAsync("select * from no_such_table_here", CancellationToken.None));
        Assert.Equal("42P01", ex.SqlState); // undefined_table

        // Cancellation is a real failure too (Npgsql raises query_canceled, not OperationCanceledException),
        // so it can never be mistaken for an uncountable query.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(
            () => executor.CountAsync("select film_id from film", cts.Token));
    }

    [SkippableFact]
    public async Task Paging_and_count_over_a_single_select()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        const string sql = "select film_id from film order by film_id";

        // Total count wraps the query.
        var total = await executor.CountAsync(sql, CancellationToken.None);
        Assert.Equal(1000, total); // pagila has 1000 films

        // First page (PageSql would produce this top-level suffix; the executor just runs it).
        var page1 = await executor.ExecutePageAsync($"{sql}\nlimit 100", CancellationToken.None);
        Assert.True(page1.Success, page1.Error?.Message);
        Assert.Equal(100, page1.RowCount);
        Assert.Equal(1, Convert.ToInt32(page1.Rows[0][0]));

        // Next page continues from the offset.
        var page2 = await executor.ExecutePageAsync($"{sql}\nlimit 100 offset 100", CancellationToken.None);
        Assert.Equal(100, page2.RowCount);
        Assert.Equal(101, Convert.ToInt32(page2.Rows[0][0]));

        // A trailing semicolon is tolerated (statement-at-caret often includes it).
        var counted = await executor.CountAsync("select film_id from film;", CancellationToken.None);
        Assert.Equal(1000, counted);
    }

    [SkippableFact]
    public async Task Executor_runs_both_page_shapes_identically_and_in_order()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        const string sql = "select film_id from film order by film_id";

        // The executor runs whatever PageSql hands it. Both shapes — the top-level suffix (preferred)
        // and the derived-table wrap (fallback) — must return the same window, films 101..200 in order.
        var appended = await executor.ExecutePageAsync($"{sql}\nlimit 100 offset 100", CancellationToken.None);
        var wrapped = await executor.ExecutePageAsync(
            $"select * from (\n{sql}\n) as _sq offset 100 limit 100", CancellationToken.None);

        Assert.True(appended.Success, appended.Error?.Message);
        Assert.Equal(100, appended.RowCount);
        Assert.Equal(101, Convert.ToInt32(appended.Rows[0][0]));
        Assert.Equal(200, Convert.ToInt32(appended.Rows[^1][0]));

        Assert.Equal(appended.RowCount, wrapped.RowCount);
        for (var i = 0; i < appended.Rows.Count; i++)
            Assert.Equal(Convert.ToInt32(appended.Rows[i][0]), Convert.ToInt32(wrapped.Rows[i][0]));
    }

    /// <summary>
    /// The read behind "Fetch all rows": one execution, streamed in batches, instead of a walk over pages.
    /// Three things it has to get right — every row in order from a single pass, a cap that stops the read,
    /// and the difference between "the cap cut this" and "the result ended", which is what the UI reports.
    /// </summary>
    [SkippableFact]
    public async Task Streaming_reads_a_result_in_one_pass_and_says_when_a_cap_cut_it()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        const string sql = "select film_id from film order by film_id";

        static async Task<List<RowBatch>> Drain(IAsyncEnumerable<RowBatch> stream)
        {
            var batches = new List<RowBatch>();
            await foreach (var b in stream) batches.Add(b);
            return batches;
        }

        // Uncapped: all 1000 pagila films, in order, in BatchRows-sized batches plus a tail.
        var all = await Drain(executor.StreamRowsAsync(
            sql, new QueryOptions { MaxRows = null, BatchRows = 300 }, CancellationToken.None));
        Assert.Equal(new[] { 300, 300, 300, 100 }, all.Select(b => b.Rows.Count));
        Assert.Equal(Enumerable.Range(1, 1000), all.SelectMany(b => b.Rows).Select(r => Convert.ToInt32(r[0])));
        Assert.DoesNotContain(true, all.Select(b => b.Truncated));

        // Capped with rows still behind it: exactly MaxRows are yielded and the last batch says so. The
        // caller asks for one row past the cap purely so this is knowable without a second query.
        var capped = await Drain(executor.StreamRowsAsync(
            $"{sql}\nlimit 501", new QueryOptions { MaxRows = 500, BatchRows = 200 }, CancellationToken.None));
        Assert.Equal(500, capped.Sum(b => b.Rows.Count));
        Assert.True(capped[^1].Truncated);
        Assert.False(capped[0].Truncated);      // only the final batch carries it

        // A cap the result merely reaches is *not* truncation — reporting it as one would turn a complete
        // fetch into "stopped at the limit" and block the export that follows it.
        var exact = await Drain(executor.StreamRowsAsync(
            $"{sql}\nlimit 500", new QueryOptions { MaxRows = 500, BatchRows = 200 }, CancellationToken.None));
        Assert.Equal(500, exact.Sum(b => b.Rows.Count));
        Assert.False(exact[^1].Truncated);

        // Failures throw rather than ending the stream quietly: a fetch that swallowed one would report a
        // complete result it never read, and the export downstream would write part of the answer.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Drain(executor.StreamRowsAsync(
            "select * from no_such_table_here", new QueryOptions(), CancellationToken.None)));
        Assert.Equal("42P01", ex.SqlState); // undefined_table
    }

    [SkippableFact]
    public async Task Lists_databases()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var reader = provider.CreateMetadataReader(factory);
        var dbs = await reader.GetDatabasesAsync(CancellationToken.None);
        Assert.Contains("pagila", dbs);
    }
}
