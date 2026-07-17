using Squirrel.Core.Data;
using Squirrel.Data;
using Squirrel.Data.Postgres;
using Xunit;

namespace Squirrel.Data.Tests;

public class PostgresExecutorColumnTests
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

    [SkippableFact]
    public async Task Raw_query_columns_carry_base_table_origin()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        Skip.IfNot(await Safe(factory), "No PostgreSQL reachable for integration test.");

        var executor = provider.CreateQueryExecutor(factory);
        var results = await executor.ExecuteAsync(
            "select film_id, language_id, 1 as expr from film limit 1", new QueryOptions(), CancellationToken.None);
        var cols = Assert.Single(results).Columns;

        // Direct table columns carry a catalog origin; an expression column does not.
        var filmId = cols.Single(c => c.Name == "film_id");
        var lang = cols.Single(c => c.Name == "language_id");
        var expr = cols.Single(c => c.Name == "expr");

        Assert.True(filmId.HasBaseColumn);
        Assert.True(lang.HasBaseColumn);
        Assert.False(expr.HasBaseColumn);

        // film_id and language_id share the same base table but differ in attribute number.
        Assert.Equal(filmId.BaseTableOid, lang.BaseTableOid);
        Assert.NotEqual(filmId.BaseColumnAttNum, lang.BaseColumnAttNum);
    }

    private static async Task<bool> Safe(IDbConnectionFactory f)
    {
        try { return await f.TestConnectionAsync(CancellationToken.None); } catch { return false; }
    }
}
