using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.SqlServer;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// Column origin, which is the whole reason <see cref="ColumnDescriptor"/> grew a name-based form:
/// <c>SqlDataReader</c> has no equivalent of Postgres' table OID + attribute number, it reports base
/// schema/table/column <em>names</em>, and only when the command ran under
/// <see cref="System.Data.CommandBehavior.KeyInfo"/>. Everything downstream of this — FK navigation and
/// inline editing on a SQL Server result — is dead unless these names actually arrive, and no unit test can
/// say whether they do.
/// </summary>
public class SqlServerExecutorColumnTests
{
    private static ConnectionInfo Info() => MsSqlTestServer.Info();
    private static string Password => MsSqlTestServer.Password;

    [SkippableFact]
    public async Task Raw_query_columns_carry_base_table_origin_by_name()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        const string tbl = "dbo.bearing_origin_test";
        await executor.ExecuteAsync(
            $"drop table if exists {tbl};"
            + $" create table {tbl} (id int not null primary key, label nvarchar(20) null);"
            + $" insert into {tbl} (id, label) values (1, 'one');",
            new QueryOptions(), CancellationToken.None);
        try
        {
            var results = await executor.ExecuteAsync(
                $"select id, label as caption, 1 as expr from {tbl}",
                new QueryOptions(), CancellationToken.None);
            var result = Assert.Single(results);
            Assert.True(result.Success, result.Error?.Message);

            var id = result.Columns.Single(c => c.Name == "id");
            var caption = result.Columns.Single(c => c.Name == "caption");
            var expr = result.Columns.Single(c => c.Name == "expr");

            // Direct table columns carry a catalog origin; an expression column does not.
            Assert.True(id.HasBaseColumn);
            Assert.True(caption.HasBaseColumn);
            Assert.False(expr.HasBaseColumn);

            // The names, which is the only form on offer here. The ids stay at 0 deliberately: filling both
            // would make the names dead weight, since the resolver prefers an id when it has one.
            Assert.Equal("dbo", id.BaseSchemaName);
            Assert.Equal("bearing_origin_test", id.BaseTableName);
            Assert.Equal("id", id.BaseColumnName);
            Assert.Equal(0, id.BaseTableId);
            Assert.Equal(0, id.BaseColumnOrdinal);

            // An alias renames the result column, not its origin — the generated DML has to name the
            // catalog column, so this is the pairing inline edit depends on.
            Assert.Equal("label", caption.BaseColumnName);
            Assert.Equal("bearing_origin_test", caption.BaseTableName);

            // Both real columns agree on one base table, which is what makes the result editable at all.
            Assert.Equal(id.BaseTableName, caption.BaseTableName);
            Assert.Equal(id.BaseSchemaName, caption.BaseSchemaName);
        }
        finally
        {
            await executor.ExecuteAsync($"drop table if exists {tbl};", new QueryOptions(), CancellationToken.None);
        }
    }

    /// <summary>
    /// KeyInfo is applied to every statement <c>ExecuteAsync</c> runs, and SqlClient implements it by asking
    /// the server for browse metadata — which historically rejects or rewrites some statements. This is the
    /// canary for that: a batch mixing DDL, a write and two reads must still come back statement for
    /// statement. If it ever fails, the fix is to ask for KeyInfo only on a lone row-returning statement and
    /// accept no origin otherwise; the plumbing is isolated to one call site for exactly that reason.
    /// </summary>
    [SkippableFact]
    public async Task Key_info_does_not_disturb_an_ordinary_batch()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        const string tbl = "dbo.bearing_keyinfo_test";
        try
        {
            var results = await executor.ExecuteAsync(
                $"""
                drop table if exists {tbl};
                create table {tbl} (id int not null primary key);
                insert into {tbl} (id) values (1), (2);
                select id from {tbl} order by id;
                select count(*) as total from {tbl};
                """, new QueryOptions(), CancellationToken.None);

            Assert.All(results, r => Assert.True(r.Success, r.Error?.Message));
            var reads = results.Where(r => r.Columns.Count > 0).ToList();
            Assert.Equal(2, reads.Count);
            Assert.Equal(new[] { 1, 2 }, reads[0].Rows.Select(r => Convert.ToInt32(r[0])));
            Assert.Equal(2, Convert.ToInt32(reads[1].Rows[0][0]));

            // An aggregate has no origin, and asking for one must not invent it.
            Assert.False(reads[1].Columns[0].HasBaseColumn);
        }
        finally
        {
            await executor.ExecuteAsync($"drop table if exists {tbl};", new QueryOptions(), CancellationToken.None);
        }
    }
}
