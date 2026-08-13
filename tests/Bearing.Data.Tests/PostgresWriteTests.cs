using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Sql;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

public class PostgresWriteTests
{
    private static ConnectionInfo Info() => PgTestServer.Info();
    private static string Password => PgTestServer.Password;

    [SkippableFact]
    public async Task Insert_update_delete_run_transactionally()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        const string tbl = "bearing_write_test";
        await exec.ExecuteAsync($"drop table if exists {tbl}; create table {tbl} (id serial primary key, name text, note text);",
            new QueryOptions(), CancellationToken.None);
        try
        {
            // INSERT ... RETURNING * gives back the generated id.
            var ins = await exec.ExecuteWriteAsync(
                new[] { DmlGenerator.Insert(null, tbl, new[] { new ColumnValue("name", "alpha"), new ColumnValue("note", "n1") }) },
                CancellationToken.None);
            var insRow = Assert.Single(ins);
            Assert.True(insRow.Success, insRow.Error?.Message);
            Assert.Equal(1, insRow.RowCount);              // one row returned
            var idIdx = ColumnIndex(insRow, "id");
            var newId = Convert.ToInt32(insRow.Rows[0][idIdx]);

            // UPDATE keyed by the returned PK.
            var upd = await exec.ExecuteWriteAsync(
                new[] { DmlGenerator.Update(null, tbl,
                    assignments: new[] { new ColumnValue("name", "beta"), new ColumnValue("note", null) },
                    keys: new[] { new ColumnValue("id", newId) }) },
                CancellationToken.None);
            Assert.Equal(1, Assert.Single(upd).RowCount); // one row affected

            var afterUpd = await exec.ExecuteAsync($"select name, note from {tbl} where id = {newId}", new QueryOptions(), CancellationToken.None);
            var row = Assert.Single(afterUpd[0].Rows);
            Assert.Equal("beta", row[0]);
            Assert.Null(row[1]);                            // note set to NULL

            // DELETE keyed by PK.
            var del = await exec.ExecuteWriteAsync(
                new[] { DmlGenerator.Delete(null, tbl, new[] { new ColumnValue("id", newId) }) },
                CancellationToken.None);
            Assert.Equal(1, Assert.Single(del).RowCount);
            var afterDel = await exec.CountAsync($"select * from {tbl}", CancellationToken.None);
            Assert.Equal(0, afterDel);
        }
        finally
        {
            await exec.ExecuteAsync($"drop table if exists {tbl};", new QueryOptions(), CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task Failing_command_rolls_back_the_whole_batch()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        const string tbl = "bearing_write_rollback";
        await exec.ExecuteAsync($"drop table if exists {tbl}; create table {tbl} (id int primary key);",
            new QueryOptions(), CancellationToken.None);
        try
        {
            // First insert succeeds; second violates the PK — the batch must roll back entirely.
            var res = await exec.ExecuteWriteAsync(new[]
            {
                DmlGenerator.Insert(null, tbl, new[] { new ColumnValue("id", 1) }),
                DmlGenerator.Insert(null, tbl, new[] { new ColumnValue("id", 1) }),
            }, CancellationToken.None);

            Assert.False(res[0].Success);                   // returned as a single error result
            var count = await exec.CountAsync($"select * from {tbl}", CancellationToken.None);
            Assert.Equal(0, count);                          // nothing committed
        }
        finally
        {
            await exec.ExecuteAsync($"drop table if exists {tbl};", new QueryOptions(), CancellationToken.None);
        }
    }

    private static int ColumnIndex(QueryResult r, string name)
    {
        for (var i = 0; i < r.Columns.Count; i++)
            if (r.Columns[i].Name == name) return i;
        return -1;
    }
}
