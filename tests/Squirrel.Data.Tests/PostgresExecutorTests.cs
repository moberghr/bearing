using Squirrel.Core.Data;
using Squirrel.Data;
using Squirrel.Data.Postgres;
using Xunit;
using Xunit.Sdk;

namespace Squirrel.Data.Tests;

/// <summary>
/// Integration tests against a live PostgreSQL loaded with pagila. Point at it via env vars
/// (SQUIRREL_TEST_PG_*), defaulting to the local docker container on port 5433. Skipped cleanly
/// when no database is reachable so the suite stays green off a dev box.
/// </summary>
public class PostgresExecutorTests
{
    private static ConnectionInfo Info() => new()
    {
        Id = Guid.NewGuid(),
        Name = "pagila-test",
        ProviderId = PostgresProvider.ProviderId,
        Host = Env("HOST", "localhost"),
        Port = int.Parse(Env("PORT", "5433")),
        Database = Env("DB", "pagila"),
        User = Env("USER", "postgres"),
    };

    private static string Password => Env("PASSWORD", "squirrel");

    private static string Env(string key, string dflt)
        => Environment.GetEnvironmentVariable($"SQUIRREL_TEST_PG_{key}") ?? dflt;

    private static async Task<bool> Reachable(IDbConnectionFactory f)
    {
        try { return await f.TestConnectionAsync(CancellationToken.None); }
        catch { return false; }
    }

    [SkippableFact]
    public async Task Executes_a_select_and_returns_typed_rows()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        Skip.IfNot(await Reachable(factory), "No PostgreSQL reachable for integration test.");

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
        Skip.IfNot(await Reachable(factory), "No PostgreSQL reachable for integration test.");

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
        Skip.IfNot(await Reachable(factory), "No PostgreSQL reachable for integration test.");

        var executor = provider.CreateQueryExecutor(factory);
        var result = Assert.Single(await executor.ExecuteAsync(
            "select * from no_such_table_here", new QueryOptions(), CancellationToken.None));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("42P01", result.Error!.SqlState); // undefined_table
    }

    [SkippableFact]
    public async Task Paging_and_count_over_a_single_select()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        Skip.IfNot(await Reachable(factory), "No PostgreSQL reachable for integration test.");

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
        Skip.IfNot(await Reachable(factory), "No PostgreSQL reachable for integration test.");

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

    [SkippableFact]
    public async Task Lists_databases()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        Skip.IfNot(await Reachable(factory), "No PostgreSQL reachable for integration test.");

        var reader = provider.CreateMetadataReader(factory);
        var dbs = await reader.GetDatabasesAsync(CancellationToken.None);
        Assert.Contains("pagila", dbs);
    }
}
