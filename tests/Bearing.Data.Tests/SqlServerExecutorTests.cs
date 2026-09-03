using System;
using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.SqlServer;
using Bearing.Sql;
using Bearing.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// Integration tests against a live Microsoft SQL Server. Endpoint and defaults come from
/// <see cref="MsSqlTestServer"/> (override with BEARING_TEST_MSSQL_*). Skipped cleanly when no server is
/// reachable, so the suite stays green off a dev box — which is every box that has built this branch so far.
/// <para>
/// These are the tests that decide whether the SQL Server path is real: the executor's shape was written
/// from documented behaviour, and three of its choices — <c>CommandBehavior.KeyInfo</c> for column origin,
/// the per-statement affected-row delta, and the paging/count SQL the dialect emits — are things only a
/// server can confirm. Each of those has a test below saying what it expects and why.
/// </para>
/// <para>
/// Unlike the Postgres suites there is no sample database to load: every test creates the objects it needs
/// and drops them in a <c>finally</c>, so any database the login may write to will do.
/// </para>
/// </summary>
public class SqlServerExecutorTests
{
    private static ConnectionInfo Info() => MsSqlTestServer.Info();
    private static string Password => MsSqlTestServer.Password;

    /// <summary>The count wrapper the App layer builds before calling the executor: <c>CountAsync</c>
    /// runs an already-shaped count query and never generates one (same contract as
    /// <c>ExecutePageAsync</c>), so the dialect's wrap is applied here exactly as production applies it.
    /// Pairing them in the test is the point — a wrap the server rejects is a dialect bug, not an
    /// executor bug, and this is where the two meet.</summary>
    /// <summary>The count wrapper the App layer builds before calling the executor: <c>CountAsync</c>
    /// runs an already-shaped count query and never generates one (same contract as
    /// <c>ExecutePageAsync</c>). Non-null here by construction — every fixture below is a plain SELECT,
    /// and the dialect only refuses shapes that cannot sit in a derived table (a CTE, a query hint,
    /// FOR JSON/XML). Asserted rather than suppressed so a fixture that drifts into one of those
    /// fails loudly instead of passing a null through.</summary>
    private static string CountSql(string sql)
        => SqlServerDialect.Instance.CountWrap(sql)
           ?? throw new InvalidOperationException($"dialect refused to wrap a count for: {sql}");
    private static IDbProvider Provider() => new ProviderRegistry().Get(SqlServerProvider.ProviderId);

    /// <summary>200 rows of <c>id</c>, 1..200 — enough to page through and to tell a window from a total.
    /// Built from <c>sys.all_objects</c> rather than a recursive CTE so the setup itself stays a plain
    /// statement the guard and the splitter have no opinion about.</summary>
    private const string NumbersTable = "dbo.bearing_page_test";

    /// <summary>The four the metadata reader hides — nobody browses the server's own book-keeping.</summary>
    private static readonly HashSet<string> SystemDatabases =
        new(StringComparer.OrdinalIgnoreCase) { "master", "tempdb", "model", "msdb" };

    private static async Task CreateNumbersAsync(IQueryExecutor exec)
    {
        var results = await exec.ExecuteAsync(
            $"""
            drop table if exists {NumbersTable};
            create table {NumbersTable} (id int not null primary key);
            insert into {NumbersTable} (id)
            select top (200) row_number() over (order by (select null)) from sys.all_objects;
            """, new QueryOptions(), CancellationToken.None);

        // Setup that failed silently would make every assertion below a lie about the executor.
        Assert.All(results, r => Assert.True(r.Success, r.Error?.Message));
    }

    private static Task DropAsync(IQueryExecutor exec, string table) => exec.ExecuteAsync(
        $"drop table if exists {table};", new QueryOptions(), CancellationToken.None);

    [SkippableFact]
    public async Task Executes_a_select_and_returns_typed_rows()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        var results = await executor.ExecuteAsync(
            "select id, name from (values (1, 'alpha'), (2, 'beta')) as v(id, name) order by id",
            new QueryOptions(), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.RowCount);
        Assert.Equal(new[] { "id", "name" }, result.Columns.Select(c => c.Name));
        Assert.Equal(1, Convert.ToInt32(result.Rows[0][0]));
        Assert.Equal("alpha", result.Rows[0][1]);
    }

    [SkippableFact]
    public async Task Multi_statement_run_returns_a_result_set_per_statement()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        var results = await executor.ExecuteAsync(
            "select 1 as first; select 42 as answer", new QueryOptions(), CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("first", results[0].Columns[0].Name);
        Assert.Equal("answer", results[1].Columns[0].Name);
        Assert.Equal(42, Convert.ToInt32(results[1].Rows[0][0]));
    }

    /// <summary>
    /// The behavioural difference most likely to be got wrong in a port from Npgsql:
    /// <see cref="SqlDataReader.RecordsAffected"/> accumulates over the whole batch instead of resetting per
    /// statement, so a naive read makes the second UPDATE claim it touched both statements' rows — and the
    /// number keeps growing with every statement after it. Three rows updated twice must report 3 and 3,
    /// never 3 and 6.
    /// </summary>
    [SkippableFact]
    public async Task Affected_rows_are_per_statement_not_the_running_batch_total()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        const string tbl = "dbo.bearing_affected_test";
        await executor.ExecuteAsync(
            $"drop table if exists {tbl}; create table {tbl} (id int not null primary key, v int null);"
            + $" insert into {tbl} (id) values (1), (2), (3);",
            new QueryOptions(), CancellationToken.None);
        try
        {
            var results = await executor.ExecuteAsync(
                $"update {tbl} set v = 1; update {tbl} set v = 2; update {tbl} set v = 3 where id = 1;",
                new QueryOptions(), CancellationToken.None);

            Assert.Equal(3, results.Count);
            Assert.Equal(new[] { 3L, 3L, 1L }, results.Select(r => (long)r.RowCount));
            Assert.All(results, r => Assert.True(r.Success, r.Error?.Message));
        }
        finally
        {
            await DropAsync(executor, tbl);
        }
    }

    /// <summary>SqlState carries the error <em>number</em> — SQL Server has no SQLSTATE of its own — and
    /// 208 is "Invalid object name". Pins the choice the executor documents, and the value
    /// <see cref="SqlServerProvider.Classify"/> is written against.</summary>
    [SkippableFact]
    public async Task Surfaces_sql_errors_as_a_query_error_carrying_the_error_number()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        var result = Assert.Single(await executor.ExecuteAsync(
            "select * from no_such_table_here", new QueryOptions(), CancellationToken.None));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("208", result.Error!.SqlState);   // invalid object name
        Assert.Null(result.Error.Position);            // a line number would aim the editor's caret wrongly
        Assert.Equal(DbErrorKind.Unknown, provider.Classify(result.Error)); // not auth, not a cancel
    }

    [SkippableFact]
    public async Task Null_cells_materialize_as_null_without_disturbing_their_neighbours()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        var result = Assert.Single(await executor.ExecuteAsync(
            "select 1 as a, cast(null as nvarchar(10)) as b, 'x' as c, cast(null as int) as d, 2 as e",
            new QueryOptions(), CancellationToken.None));

        Assert.True(result.Success, result.Error?.Message);
        var row = result.Rows[0];
        Assert.Equal(1, Convert.ToInt32(row[0]));
        Assert.Null(row[1]);
        Assert.Equal("x", row[2]);
        Assert.Null(row[3]);
        Assert.Equal(2, Convert.ToInt32(row[4]));
    }

    /// <summary>
    /// The count wrap over a query that carries its own ORDER BY — the one shape T-SQL rejects outright
    /// (Msg 1033) and the dialect repairs with <c>offset 0 rows</c>. Nothing but a server can say whether
    /// that repair is accepted, which is why this test exists.
    /// </summary>
    [SkippableFact]
    public async Task Count_wraps_a_query_that_carries_its_own_order_by()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        await CreateNumbersAsync(executor);
        try
        {
            Assert.Equal(200, await executor.CountAsync(CountSql($"select id from {NumbersTable} order by id"), CancellationToken.None));

            // Without an inner ORDER BY nothing needs repairing, and a trailing semicolon is tolerated
            // (statement-at-caret often includes one).
            Assert.Equal(200, await executor.CountAsync(CountSql($"select id from {NumbersTable};"), CancellationToken.None));
        }
        finally
        {
            await DropAsync(executor, NumbersTable);
        }
    }

    /// <summary>A count that can't be <em>shaped</em> reports null; a count that <em>fails</em> throws.
    /// Same split as the Postgres executor's, in SQL Server's error numbers — before it, a dropped table or
    /// a dead connection looked exactly like an uncountable query and the UI just showed no total.</summary>
    [SkippableFact]
    public async Task Count_reports_null_for_an_uncountable_shape_but_throws_on_a_real_failure()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);

        // Uncountable shapes — the wrap itself is invalid, nothing is wrong with the server.
        Assert.Null(await executor.CountAsync(CountSql("select 1 as a; select 2 as b"), CancellationToken.None)); // a batch
        Assert.Null(await executor.CountAsync(CountSql("select 1"), CancellationToken.None));  // unnamed column (8155)
        Assert.Null(await executor.CountAsync(CountSql("update no_such_table_here set x = 1"), CancellationToken.None));         // not row-returning

        // A real failure propagates instead of masquerading as "no total available".
        var ex = await Assert.ThrowsAsync<SqlException>(
            () => executor.CountAsync(CountSql("select * from no_such_table_here"), CancellationToken.None));
        Assert.Equal(208, ex.Number); // invalid object name

        // Cancellation is a real failure too, so it can never be mistaken for an uncountable query.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(
            () => executor.CountAsync(CountSql($"select 1 as one"), cts.Token));
    }

    /// <summary>
    /// Paging a query that has a top-level ORDER BY: the dialect appends <c>offset … fetch next …</c>, which
    /// is only legal <em>because</em> of that ORDER BY. The window has to be the same one Postgres'
    /// <c>limit/offset</c> would return, or "Load more" silently repeats or skips rows.
    /// </summary>
    [SkippableFact]
    public async Task Paging_a_sorted_query_uses_the_offset_fetch_suffix_and_returns_the_right_window()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        await CreateNumbersAsync(executor);
        try
        {
            var sql = $"select id from {NumbersTable} order by id";

            // Non-null: `sql` is a plain ordered SELECT, which takes the top-level suffix.
            var first = PageSql.Page(SqlServerDialect.Instance, sql, offset: 0, limit: 50)!;
            Assert.Contains("offset 0 rows fetch next 50 rows only", first);
            var page1 = await executor.ExecutePageAsync(first, CancellationToken.None);
            Assert.True(page1.Success, page1.Error?.Message);
            Assert.Equal(50, page1.RowCount);
            Assert.Equal(1, Convert.ToInt32(page1.Rows[0][0]));
            Assert.Equal(50, Convert.ToInt32(page1.Rows[^1][0]));

            var second = PageSql.Page(SqlServerDialect.Instance, sql, offset: 50, limit: 50)!;
            var page2 = await executor.ExecutePageAsync(second, CancellationToken.None);
            Assert.Equal(51, Convert.ToInt32(page2.Rows[0][0]));
            Assert.Equal(100, Convert.ToInt32(page2.Rows[^1][0]));

            // The wrap has to agree with the suffix on the same window — it is the fallback the caller
            // silently switches to, so a disagreement would be a paging bug nobody could see.
            var wrapped = await executor.ExecutePageAsync(
                SqlServerDialect.Instance.Wrap(sql, offset: 50, limit: 50)!, CancellationToken.None);
            Assert.True(wrapped.Success, wrapped.Error?.Message);
            Assert.Equal(
                page2.Rows.Select(r => Convert.ToInt32(r[0])).OrderBy(i => i),
                wrapped.Rows.Select(r => Convert.ToInt32(r[0])).OrderBy(i => i));
        }
        finally
        {
            await DropAsync(executor, NumbersTable);
        }
    }

    /// <summary>
    /// Paging a query with <b>no</b> ORDER BY. The suffix is refused rather than invented (an arbitrary
    /// order makes pages non-deterministic), so the caller falls back to the wrap, whose
    /// <c>order by (select null)</c> is what makes OFFSET/FETCH legal at all. What is asserted is therefore
    /// the size of the window and that the server accepted the SQL — not which rows came back, because
    /// without an ORDER BY the engine owes us no particular ones.
    /// </summary>
    [SkippableFact]
    public async Task Paging_an_unsorted_query_falls_back_to_the_wrap_and_the_server_accepts_it()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        await CreateNumbersAsync(executor);
        try
        {
            var sql = $"select id from {NumbersTable}";
            Assert.Null(SqlServerDialect.Instance.TryAppendPage(sql, 0, 10)); // refused, not invented

            var page = await executor.ExecutePageAsync(
                PageSql.Page(SqlServerDialect.Instance, sql, offset: 10, limit: 25)!, CancellationToken.None);

            Assert.True(page.Success, page.Error?.Message);
            Assert.Equal(25, page.RowCount);
            var ids = page.Rows.Select(r => Convert.ToInt32(r[0])).ToList();
            Assert.Equal(25, ids.Distinct().Count());
            Assert.All(ids, id => Assert.InRange(id, 1, 200));
        }
        finally
        {
            await DropAsync(executor, NumbersTable);
        }
    }

    /// <summary>
    /// Paging and counting a <b>CTE</b> query, which is the one shape in this dialect whose SQL was
    /// reasoned rather than observed. A CTE cannot sit inside a derived table (Msg 156), so the wrap hoists
    /// the <c>WITH</c> list over it and puts only the body in the subquery — legal because a
    /// statement-level CTE is in scope throughout the statement, subqueries included. That sentence is a
    /// documented rule, not a measurement, and the dialect tests can only assert the text it produces;
    /// this is where a server says whether the text is accepted and answers correctly.
    /// <para>
    /// Three shapes, because they take three different paths: the body with no order (hoist + wrap), the
    /// body with its own <c>ORDER BY</c> (hoist + wrap + the Msg 1033 <c>offset 0 rows</c> repair, the
    /// combination nothing else in the suite reaches), and the same query paged by the suffix instead —
    /// which is what the caller actually prefers, and the only one of the three whose window is
    /// deterministic enough to name the rows in it.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Paging_a_cte_hoists_the_with_list_and_the_server_accepts_it()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        await CreateNumbersAsync(executor);
        try
        {
            const string body = "select id from c";
            var cte = $"with c as (select id from {NumbersTable} where id <= 100) {body}";

            // ---- The count, hoisted. Non-null is half the assertion: before the hoist the dialect
            // refused this shape outright and the UI showed no total at all.
            var countSql = CountSql(cte);
            Assert.StartsWith("with c as", countSql);
            Assert.Equal(100, await executor.CountAsync(countSql, CancellationToken.None));

            // ---- The wrap. No ORDER BY anywhere, so what is asserted is the size of the window and that
            // the server took the SQL — not which rows, because nothing promised any particular ones.
            var wrapped = SqlServerDialect.Instance.Wrap(cte, offset: 10, limit: 25)!;
            var page = await executor.ExecutePageAsync(wrapped, CancellationToken.None);
            Assert.True(page.Success, page.Error?.Message);
            Assert.Equal(25, page.RowCount);
            var ids = page.Rows.Select(r => Convert.ToInt32(r[0])).ToList();
            Assert.Equal(25, ids.Distinct().Count());
            Assert.All(ids, id => Assert.InRange(id, 1, 100));

            // ---- The hoist *plus* the inner-ORDER BY repair: the body brings an order the derived table
            // may not carry, so `offset 0 rows` is appended inside. Two clauses that each need the other
            // to be legal, and the shape the dialect's own tests can only check as a string.
            var sorted = $"{cte} order by id";
            var sortedCount = CountSql(sorted);
            Assert.Contains("offset 0 rows", sortedCount);
            Assert.Equal(100, await executor.CountAsync(sortedCount, CancellationToken.None));

            var sortedWrap = SqlServerDialect.Instance.Wrap(sorted, offset: 10, limit: 25)!;
            var sortedPage = await executor.ExecutePageAsync(sortedWrap, CancellationToken.None);
            Assert.True(sortedPage.Success, sortedPage.Error?.Message);
            Assert.Equal(25, sortedPage.RowCount);

            // ---- And what the caller actually uses for that query: the suffix, which rides the query's
            // own ORDER BY and leaves the CTE where it is. This one owes us exact rows.
            var suffixed = PageSql.Page(SqlServerDialect.Instance, sorted, offset: 10, limit: 25)!;
            Assert.DoesNotContain("_sq", suffixed);
            var suffixPage = await executor.ExecutePageAsync(suffixed, CancellationToken.None);
            Assert.True(suffixPage.Success, suffixPage.Error?.Message);
            Assert.Equal(
                Enumerable.Range(11, 25),
                suffixPage.Rows.Select(r => Convert.ToInt32(r[0])));
        }
        finally
        {
            await DropAsync(executor, NumbersTable);
        }
    }

    /// <summary>
    /// The read behind "Fetch all rows": one execution, streamed in batches. Same three things the Postgres
    /// suite pins — every row in order from a single pass, a cap that stops the read, and the difference
    /// between "the cap cut this" and "the result ended", which is what the UI reports.
    /// </summary>
    [SkippableFact]
    public async Task Streaming_reads_a_result_in_one_pass_and_says_when_a_cap_cut_it()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        await CreateNumbersAsync(executor);
        try
        {
            var sql = $"select id from {NumbersTable} order by id";

            static async Task<List<RowBatch>> Drain(IAsyncEnumerable<RowBatch> stream)
            {
                var batches = new List<RowBatch>();
                await foreach (var b in stream) batches.Add(b);
                return batches;
            }

            var all = await Drain(executor.StreamRowsAsync(
                sql, new QueryOptions { MaxRows = null, BatchRows = 75 }, CancellationToken.None));
            Assert.Equal(new[] { 75, 75, 50 }, all.Select(b => b.Rows.Count));
            Assert.Equal(Enumerable.Range(1, 200), all.SelectMany(b => b.Rows).Select(r => Convert.ToInt32(r[0])));
            Assert.DoesNotContain(true, all.Select(b => b.Truncated));

            // Capped with rows still behind it: exactly MaxRows are yielded and the last batch says so.
            var capped = await Drain(executor.StreamRowsAsync(
                sql, new QueryOptions { MaxRows = 100, BatchRows = 40 }, CancellationToken.None));
            Assert.Equal(100, capped.Sum(b => b.Rows.Count));
            Assert.True(capped[^1].Truncated);
            Assert.False(capped[0].Truncated);   // only the final batch carries it

            // A cap the result merely reaches is not truncation — reporting it as one would turn a complete
            // fetch into "stopped at the limit" and block the export that follows.
            var exact = await Drain(executor.StreamRowsAsync(
                sql, new QueryOptions { MaxRows = 200, BatchRows = 80 }, CancellationToken.None));
            Assert.Equal(200, exact.Sum(b => b.Rows.Count));
            Assert.False(exact[^1].Truncated);

            // Failures throw rather than ending the stream quietly: a fetch that swallowed one would report
            // a complete result it never read, and the export downstream would write part of the answer.
            var ex = await Assert.ThrowsAsync<SqlException>(() => Drain(executor.StreamRowsAsync(
                "select * from no_such_table_here", new QueryOptions(), CancellationToken.None)));
            Assert.Equal(208, ex.Number);
        }
        finally
        {
            await DropAsync(executor, NumbersTable);
        }
    }

    [SkippableFact]
    public async Task Lists_databases_without_the_servers_own_four()
    {
        var provider = Provider();
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var reader = provider.CreateMetadataReader(factory);
        var dbs = await reader.GetDatabasesAsync(CancellationToken.None);

        // The system four are hidden on purpose, so that a database picker does not open on `master`.
        Assert.DoesNotContain("master", dbs);
        Assert.DoesNotContain("tempdb", dbs);
        Assert.DoesNotContain("model", dbs);
        Assert.DoesNotContain("msdb", dbs);

        // ...and hiding them must not have hidden everything: the database we are connected to is listed.
        // Guarded, because BEARING_TEST_MSSQL_DB may legitimately point *at* a system database, and that is
        // a deliberate override rather than a reader bug.
        Skip.If(SystemDatabases.Contains(MsSqlTestServer.Database),
            $"BEARING_TEST_MSSQL_DB is a system database ({MsSqlTestServer.Database}), which this reader "
            + "hides by design.");
        Assert.Contains(MsSqlTestServer.Database, dbs);
    }
}
