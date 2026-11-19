using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Data.SqlServer;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// <see cref="IQueryExecutor.ExecuteStreamingAsync"/> against real servers (§5.8). The stream is the loop
/// <c>ExecuteAsync</c> runs, observed — so the load-bearing claim is that the two agree: the same sets, in the
/// same order, with the same rows and the same truncation, for a script that mixes variables, writes and reads.
/// Plus the two places the stream deliberately differs: an error part-way keeps the sets before it, and a
/// cancellation throws.
/// </summary>
public class BatchStreamingLiveTests
{
    private static readonly QueryOptions Options = new() { MaxRows = 200, BatchRows = 64 };

    public static TheoryData<MySqlEngine> MySqlEngines => new() { MySqlEngine.MariaDb, MySqlEngine.MySql };

    private static async Task<List<BatchEvent>> Collect(IQueryExecutor executor, string sql, CancellationToken ct = default)
    {
        var events = new List<BatchEvent>();
        await foreach (var e in executor.ExecuteStreamingAsync(sql, Options, ct)) events.Add(e);
        return events;
    }

    /// <summary>The stream says exactly what <c>ExecuteAsync</c> says, set by set, and says it in order:
    /// started, rows, ended for each row-returning set, then the completion.</summary>
    private static async Task AssertStreamMatchesExecute(IQueryExecutor executor, string sql)
    {
        var expected = await executor.ExecuteAsync(sql, Options, CancellationToken.None);
        Assert.All(expected, r => Assert.True(r.Success, r.Error?.Message));
        var events = await Collect(executor, sql);

        var completed = Assert.IsType<BatchCompleted>(events[^1]);
        Assert.Equal(expected.Select(r => (r.Columns.Count, r.RowCount, r.Message, r.Truncated, r.StatementIndex)),
            completed.Results.Select(r => (r.Columns.Count, r.RowCount, r.Message, r.Truncated, r.StatementIndex)));

        var gridSets = expected.Select((r, i) => (r, i)).Where(x => x.r.Columns.Count > 0).ToList();
        Assert.Equal(gridSets.Select(x => x.i), events.OfType<ResultStarted>().Select(s => s.Index));
        foreach (var (result, index) in gridSets)
        {
            var started = events.OfType<ResultStarted>().Single(s => s.Index == index);
            Assert.Equal(result.Columns.Select(c => c.Name), started.Columns.Select(c => c.Name));

            var streamed = events.OfType<ResultRows>().Where(r => r.Index == index).SelectMany(r => r.Rows).ToList();
            Assert.Equal(result.Rows.Select(r => string.Join("|", r)), streamed.Select(r => string.Join("|", r)));
            Assert.All(events.OfType<ResultRows>().Where(r => r.Index == index), r => Assert.InRange(r.Rows.Count, 1, Options.BatchRows));

            Assert.Equal(result.Truncated, events.OfType<ResultEnded>().Single(e => e.Index == index).Truncated);

            // Order within a set: started, then its rows, then ended.
            var positions = events.Select((e, p) => (e, p)).Where(x => x.e switch
            {
                ResultStarted s => s.Index == index,
                ResultRows r => r.Index == index,
                ResultEnded e => e.Index == index,
                _ => false,
            }).Select(x => x.e).ToList();
            Assert.IsType<ResultStarted>(positions[0]);
            Assert.IsType<ResultEnded>(positions[^1]);
        }
    }

    /// <summary>An error part-way: the sets read before it stay, then the failure.</summary>
    private static async Task AssertErrorKeepsEarlierSets(IQueryExecutor executor, string sql)
    {
        var events = await Collect(executor, sql);
        var completed = Assert.IsType<BatchCompleted>(events[^1]);
        Assert.True(completed.Results.Count >= 2, "the set before the error was dropped");
        Assert.True(completed.Results[0].Success);
        Assert.Single(completed.Results[0].Rows);
        Assert.False(completed.Results[^1].Success);
        Assert.Contains(events, e => e is ResultEnded { Index: 0 });

        // ExecuteAsync is unchanged: the failure alone.
        var plain = await executor.ExecuteAsync(sql, Options, CancellationToken.None);
        Assert.False(Assert.Single(plain).Success);
    }

    /// <summary>A cancellation throws out of the stream, after whatever arrived before it.</summary>
    private static async Task AssertCancelThrows(IQueryExecutor executor, string sql)
    {
        using var cts = new CancellationTokenSource();
        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in executor.ExecuteStreamingAsync(sql, Options with { MaxRows = null }, cts.Token))
            {
                seen++;
                if (e is ResultRows) cts.Cancel();
            }
        });
        Assert.True(seen > 0);
    }

    // ---- SQL Server ------------------------------------------------------------------------------

    private static async Task<IQueryExecutor> SqlServer()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        var factory = provider.CreateConnectionFactory(MsSqlTestServer.Info(), MsSqlTestServer.Password);
        await MsSqlTestServer.RequireAsync(factory);
        return provider.CreateQueryExecutor(factory);
    }

    [SkippableFact]
    public async Task Sql_server_streams_what_execute_returns() => await AssertStreamMatchesExecute(await SqlServer(), """
        DECLARE @n int = 250;
        SELECT TOP (@n) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
        FROM sys.all_columns a CROSS JOIN sys.all_columns b ORDER BY n;
        CREATE TABLE #s (i int); INSERT INTO #s VALUES (1), (2);
        SELECT i FROM #s ORDER BY i;
        """);

    [SkippableFact]
    public async Task Sql_server_keeps_the_sets_before_an_error()
        => await AssertErrorKeepsEarlierSets(await SqlServer(), "SELECT 1 AS a; SELECT CAST('x' AS int) AS b;");

    [SkippableFact]
    public async Task Sql_server_cancel_throws() => await AssertCancelThrows(await SqlServer(),
        "SELECT a.object_id FROM sys.all_columns a CROSS JOIN sys.all_columns b CROSS JOIN sys.all_columns c");

    // ---- Postgres --------------------------------------------------------------------------------

    private static async Task<IQueryExecutor> Postgres()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);
        return provider.CreateQueryExecutor(factory);
    }

    [SkippableFact]
    public async Task Postgres_streams_what_execute_returns() => await AssertStreamMatchesExecute(await Postgres(), """
        select n from generate_series(1, 250) n;
        create temp table bearing_stream_s (i int); insert into bearing_stream_s values (1), (2);
        select i from bearing_stream_s order by i;
        drop table bearing_stream_s;
        """);

    [SkippableFact]
    public async Task Postgres_keeps_the_sets_before_an_error()
        => await AssertErrorKeepsEarlierSets(await Postgres(), "select 1 as a; select 'x'::int as b;");

    [SkippableFact]
    public async Task Postgres_cancel_throws()
        => await AssertCancelThrows(await Postgres(), "select n from generate_series(1, 100000000) n");

    // ---- MySQL / MariaDB -------------------------------------------------------------------------

    private static async Task<IQueryExecutor> MySql(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        return new ProviderRegistry().Get("mysql").CreateQueryExecutor(MySqlTestServer.Factory(engine));
    }

    [SkippableTheory]
    [MemberData(nameof(MySqlEngines))]
    public async Task MySql_streams_what_execute_returns(MySqlEngine engine)
        => await AssertStreamMatchesExecute(await MySql(engine), """
            set @n = 250;
            with recursive r(n) as (select 1 union all select n + 1 from r where n < @n) select n from r;
            create temporary table bearing_stream_s (i int); insert into bearing_stream_s values (1), (2);
            select i from bearing_stream_s order by i;
            drop temporary table bearing_stream_s;
            """);

    [SkippableTheory]
    [MemberData(nameof(MySqlEngines))]
    public async Task MySql_keeps_the_sets_before_an_error(MySqlEngine engine)
        => await AssertErrorKeepsEarlierSets(await MySql(engine), "select 1 as a; select * from bearing_no_such_table;");

    [SkippableTheory]
    [MemberData(nameof(MySqlEngines))]
    public async Task MySql_cancel_throws(MySqlEngine engine) => await AssertCancelThrows(await MySql(engine),
        "select a.column_name from information_schema.columns a, information_schema.columns b");
}
