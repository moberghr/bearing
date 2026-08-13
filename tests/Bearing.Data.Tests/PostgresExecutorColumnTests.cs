using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

public class PostgresExecutorColumnTests
{
    private static ConnectionInfo Info() => PgTestServer.Info();
    private static string Password => PgTestServer.Password;

    [SkippableFact]
    public async Task Raw_query_columns_carry_base_table_origin()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

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
        Assert.Equal(filmId.BaseTableId, lang.BaseTableId);
        Assert.NotEqual(filmId.BaseColumnOrdinal, lang.BaseColumnOrdinal);
    }
}
