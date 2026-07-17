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
        var result = await executor.ExecuteAsync(
            "select film_id, title from film order by film_id limit 5",
            new QueryOptions(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(5, result.RowCount);
        Assert.Equal(new[] { "film_id", "title" }, result.Columns.Select(c => c.Name));
        Assert.Equal(1, Convert.ToInt32(result.Rows[0][0]));
        Assert.IsType<string>(result.Rows[0][1]);
    }

    [SkippableFact]
    public async Task Surfaces_sql_errors_as_a_query_error()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        Skip.IfNot(await Reachable(factory), "No PostgreSQL reachable for integration test.");

        var executor = provider.CreateQueryExecutor(factory);
        var result = await executor.ExecuteAsync(
            "select * from no_such_table_here", new QueryOptions(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("42P01", result.Error!.SqlState); // undefined_table
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
