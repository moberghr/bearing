using System;
using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.SqlServer;
using Bearing.Sql;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// Inline edits, end to end: the DML <see cref="SqlServerDialect"/> generates, run through
/// <see cref="IQueryExecutor.ExecuteWriteAsync"/> as one transaction. Two things only a server can settle —
/// that <c>OUTPUT INSERTED.*</c> in front of <c>VALUES</c> is accepted (a trailing OUTPUT is a syntax error,
/// which is why the dialect owns the whole statement rather than a returning-clause suffix), and that a
/// failure part-way through a batch leaves nothing behind (§5.4).
/// </summary>
public class SqlServerWriteTests
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
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    [SkippableFact]
    public async Task Insert_update_delete_run_transactionally()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        const string tbl = "bearing_write_test";
        await exec.ExecuteAsync(
            $"drop table if exists dbo.{tbl};"
            + $" create table dbo.{tbl} (id int identity primary key, name nvarchar(20) null, note nvarchar(20) null);",
            new QueryOptions(), CancellationToken.None);
        try
        {
            // INSERT ... OUTPUT INSERTED.* gives back the generated identity, which is what the grid needs
            // to key the row it just created.
            var ins = await exec.ExecuteWriteAsync(
                new[]
                {
                    DmlGenerator.Insert(Dialect, "dbo", tbl,
                        new[] { new ColumnValue("name", "alpha"), new ColumnValue("note", "n1") }),
                }, CancellationToken.None);
            var insRow = Assert.Single(ins);
            Assert.True(insRow.Success, insRow.Error?.Message);
            Assert.Equal(1, insRow.RowCount);
            var newId = Convert.ToInt32(insRow.Rows[0][ColumnIndex(insRow, "id")]);

            // UPDATE keyed by the returned PK, including a value set back to NULL.
            var upd = await exec.ExecuteWriteAsync(
                new[]
                {
                    DmlGenerator.Update(Dialect, "dbo", tbl,
                        assignments: new[] { new ColumnValue("name", "beta"), new ColumnValue("note", null) },
                        keys: new[] { new ColumnValue("id", newId) }),
                }, CancellationToken.None);
            Assert.Equal(1, Assert.Single(upd).RowCount); // one row affected

            var afterUpd = await exec.ExecuteAsync(
                $"select name, note from dbo.{tbl} where id = {newId}", new QueryOptions(), CancellationToken.None);
            var row = Assert.Single(afterUpd[0].Rows);
            Assert.Equal("beta", row[0]);
            Assert.Null(row[1]);                          // note set to NULL, not to the string "null"

            var del = await exec.ExecuteWriteAsync(
                new[] { DmlGenerator.Delete(Dialect, "dbo", tbl, new[] { new ColumnValue("id", newId) }) },
                CancellationToken.None);
            Assert.Equal(1, Assert.Single(del).RowCount);
            Assert.Equal(0, await exec.CountAsync(CountSql($"select id from dbo.{tbl}"), CancellationToken.None));
        }
        finally
        {
            await exec.ExecuteAsync($"drop table if exists dbo.{tbl};", new QueryOptions(), CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task Failing_command_rolls_back_the_whole_batch()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        const string tbl = "bearing_write_rollback";
        await exec.ExecuteAsync(
            $"drop table if exists dbo.{tbl}; create table dbo.{tbl} (id int not null primary key);",
            new QueryOptions(), CancellationToken.None);
        try
        {
            // First insert succeeds; the second violates the PK — the batch must roll back entirely, or a
            // multi-row grid edit could commit half of itself.
            var res = await exec.ExecuteWriteAsync(new[]
            {
                DmlGenerator.Insert(Dialect, "dbo", tbl, new[] { new ColumnValue("id", 1) }),
                DmlGenerator.Insert(Dialect, "dbo", tbl, new[] { new ColumnValue("id", 1) }),
            }, CancellationToken.None);

            var failure = Assert.Single(res);              // returned as one error result, not a partial list
            Assert.False(failure.Success);
            Assert.Equal("2627", failure.Error!.SqlState); // violation of PRIMARY KEY constraint
            Assert.Equal(0, await exec.CountAsync(CountSql($"select id from dbo.{tbl}"), CancellationToken.None));
        }
        finally
        {
            await exec.ExecuteAsync($"drop table if exists dbo.{tbl};", new QueryOptions(), CancellationToken.None);
        }
    }

    private static int ColumnIndex(QueryResult r, string name)
    {
        for (var i = 0; i < r.Columns.Count; i++)
            if (r.Columns[i].Name == name) return i;
        return -1;
    }
}
