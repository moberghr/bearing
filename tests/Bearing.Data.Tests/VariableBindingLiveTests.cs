using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Data.MySql;
using Bearing.Data.Postgres;
using Bearing.Data.SqlServer;
using Bearing.Sql;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// <c>:name</c> values bound as driver parameters (#103), against each engine. What only the server can
/// answer: that every executor path binds them (a run, a page, a count, a stream), that a value is one value
/// however it reads, and that each engine types it where it stands — <c>limit</c>/<c>top</c> need a number,
/// an integer key compared with text must still match.
/// <para>
/// The SQL goes through <see cref="VariableRun"/> exactly as a run in the app does, so a placeholder spelling
/// a driver doesn't bind would fail here.
/// </para>
/// </summary>
public class VariableBindingLiveTests
{
    private static PreparedRun Prepare(ISqlDialect dialect, string sql, params (string Name, string Value)[] answers)
    {
        var plan = VariableRun.Plan(dialect, sql);
        var typed = answers.ToDictionary(a => a.Name, a => a.Value, StringComparer.OrdinalIgnoreCase);
        // As the prompt answers: a typed value where given, the @set value otherwise.
        return VariableRun.Bind(dialect, plan, plan.Slots
            .Select(s => typed.TryGetValue(s.Name, out var v) ? v : ScriptDirectives.FormatValue(s.Value, s.Kind) ?? "")
            .ToList());
    }

    private static async Task<IReadOnlyList<QueryResult>> RunAsync(IQueryExecutor exec, PreparedRun run)
    {
        var results = await exec.ExecuteAsync(run.Sql, new QueryOptions { Parameters = run.Parameters }, CancellationToken.None);
        var failed = results.FirstOrDefault(r => r.Error is not null);
        Assert.True(failed is null, failed?.Error?.Message);
        return results;
    }

    // ---- Postgres (pagila) ------------------------------------------------------------------------

    private static async Task<(IDbConnectionFactory Factory, IQueryExecutor Exec)> PgAsync()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);
        return (factory, provider.CreateQueryExecutor(factory));
    }

    [SkippableFact]
    public async Task Postgres_types_each_value_from_where_it_stands()
    {
        var (factory, exec) = await PgAsync();
        await using var _ = factory;

        // An integer key, a text column, a limit, and a null — each needs a different type, and none is named.
        var run = Prepare(PostgresDialect.Instance,
            "@set id = 7\nselect film_id, title from film where film_id >= :id and title <> :skip order by film_id limit :n",
            ("skip", "x"), ("n", "3"));
        var rows = Assert.Single(await RunAsync(exec, run)).Rows;

        Assert.Equal([7, 8, 9], rows.Select(r => (int)r[0]!));

        var nulls = Assert.Single(await RunAsync(exec, Prepare(PostgresDialect.Instance, "select :v is null", ("v", "null"))));
        Assert.Equal(true, nulls.Rows[0][0]);
    }

    [SkippableFact]
    public async Task Postgres_a_value_that_reads_like_sql_is_compared_as_text()
    {
        var (factory, exec) = await PgAsync();
        await using var _ = factory;

        var run = Prepare(PostgresDialect.Instance, "select count(*) from film where title = :t", ("t", "x' or '1'='1"));

        Assert.Equal(0L, Assert.Single(await RunAsync(exec, run)).Rows[0][0]);
    }

    [SkippableFact]
    public async Task Postgres_binds_a_multi_statement_run_and_every_later_trip()
    {
        var (factory, exec) = await PgAsync();
        await using var _ = factory;
        var dialect = PostgresDialect.Instance;

        var batch = await RunAsync(exec, Prepare(dialect, "select :a::int; select :a::int + 1", ("a", "41")));
        Assert.Equal([41, 42], batch.Select(r => (int)r.Rows[0][0]!));

        var run = Prepare(dialect, "select film_id from film where rental_duration = :d order by film_id", ("d", "3"));
        var page = await exec.ExecutePageAsync(dialect.Wrap(run.Sql, 0, 5)!, run.Parameters, CancellationToken.None);
        Assert.True(page.Success, page.Error?.Message);
        Assert.Equal(5, page.Rows.Count);

        var total = await exec.CountAsync(dialect.CountWrap(run.Sql)!, run.Parameters, CancellationToken.None);
        var expected = await RunAsync(exec, Prepare(dialect, "select count(*) from film where rental_duration = 3"));
        Assert.Equal((long)expected[0].Rows[0][0]!, total);

        var streamed = 0;
        await foreach (var b in exec.StreamRowsAsync(run.Sql, new QueryOptions { Parameters = run.Parameters }, CancellationToken.None))
            streamed += b.Rows.Count;
        Assert.Equal(total, streamed);
    }

    [SkippableFact]
    public async Task Postgres_explains_a_statement_with_values()
    {
        var (factory, exec) = await PgAsync();
        await using var _ = factory;

        var run = Prepare(PostgresDialect.Instance, "select * from film where film_id = :id", ("id", "1"));
        var request = ExplainSql.Plan(run.Sql);
        var results = await exec.ExecuteAsync(request.Sql, new QueryOptions { Parameters = run.Parameters }, CancellationToken.None);

        Assert.True(results.All(r => r.Success), results.FirstOrDefault(r => !r.Success)?.Error?.Message);
    }

    // ---- SQL Server -------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Sql_server_sends_numbers_as_numbers_and_text_as_text()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(MsSqlTestServer.Info(), MsSqlTestServer.Password);
        await MsSqlTestServer.RequireAsync(factory);
        var exec = provider.CreateQueryExecutor(factory);
        var dialect = SqlServerDialect.Instance;

        // `top (@n)` refuses text; `+ 1` on a number is arithmetic, on text it would be concatenation.
        var run = Prepare(dialect,
            "select top (:n) name, :id + 1 as next, :who as who, :gone as gone from sys.all_objects order by name",
            ("n", "2"), ("id", "41"), ("who", "O'Brien"), ("gone", "null"));
        var result = Assert.Single(await RunAsync(exec, run));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(42L, result.Rows[0][1]);
        Assert.Equal("O'Brien", result.Rows[0][2]);
        Assert.Null(result.Rows[0][3]);

        var quoted = Assert.Single(await RunAsync(exec, Prepare(dialect, "select :'zip' as zip", ("zip", "01234"))));
        Assert.Equal("01234", quoted.Rows[0][0]);

        var count = await exec.CountAsync(dialect.CountWrap(Prepare(dialect, "select name from sys.all_objects where object_id > :id", ("id", "0")).Sql)!,
            [new QueryParameter("bearing_id", "0", QueryParameterKind.Number)], CancellationToken.None);
        Assert.True(count > 0);
    }

    // ---- MySQL / MariaDB --------------------------------------------------------------------------

    public static TheoryData<MySqlEngine> Engines => new() { MySqlEngine.MariaDb, MySqlEngine.MySql };

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task MySql_binds_numbers_text_and_backslashes_exactly(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var factory = MySqlTestServer.Factory(engine);
        var exec = new MySqlProvider().CreateQueryExecutor(factory);
        var dialect = MySqlDialect.Instance;

        // `limit ?` refuses a string; a backslash must arrive as one, whatever the server's sql_mode (§5.1a).
        var run = Prepare(dialect,
            "select table_name, :path as path, :n + 1 as next from information_schema.tables limit :n",
            ("path", @"C:\temp\"), ("n", "2"));
        var result = Assert.Single(await RunAsync(exec, run));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(@"C:\temp\", result.Rows[0][1]);
        Assert.Equal(3L, Convert.ToInt64(result.Rows[0][2]));
    }

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task MySql_binds_a_multi_statement_run(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var factory = MySqlTestServer.Factory(engine);
        var exec = new MySqlProvider().CreateQueryExecutor(factory);

        var results = await RunAsync(exec, Prepare(MySqlDialect.Instance, "select :a; select :a + 1", ("a", "41")));

        Assert.Equal([41L, 42L], results.Select(r => Convert.ToInt64(r.Rows[0][0])));
    }
}
