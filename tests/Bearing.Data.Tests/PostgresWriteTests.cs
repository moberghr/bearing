using System;
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

    /// <summary>The count wrapper the App layer builds before calling the executor: <c>CountAsync</c>
    /// runs an already-shaped count query and never generates one (same contract as
    /// <c>ExecutePageAsync</c>). Non-null here by construction — every fixture below is a plain SELECT,
    /// and the dialect only refuses shapes that cannot sit in a derived table (a CTE, a query hint,
    /// FOR JSON/XML). Asserted rather than suppressed so a fixture that drifts into one of those
    /// fails loudly instead of passing a null through.</summary>
    private static string CountSql(string sql)
        => PostgresDialect.Instance.CountWrap(sql)
           ?? throw new InvalidOperationException($"dialect refused to wrap a count for: {sql}");

    [SkippableFact]
    public async Task Insert_update_delete_run_transactionally()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

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
            var afterDel = await exec.CountAsync(CountSql($"select * from {tbl}"), CancellationToken.None);
            Assert.Equal(0, afterDel);
        }
        finally
        {
            await exec.ExecuteAsync($"drop table if exists {tbl};", new QueryOptions(), CancellationToken.None);
        }
    }

    /// <summary>
    /// The expression path against a live server (#149). A fixture can only assert the SQL we generate; what
    /// it cannot say is whether Postgres takes it, whether the written value is the server's clock rather
    /// than the client's, and whether RETURNING hands back the evaluated value. All three are the feature.
    /// </summary>
    [SkippableFact]
    public async Task An_expression_assignment_is_evaluated_by_the_server_and_read_back()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        const string tbl = "bearing_expression_test";
        await exec.ExecuteAsync(
            $"drop table if exists {tbl}; create table {tbl} (id serial primary key, created_at timestamptz, tag uuid);",
            new QueryOptions(), CancellationToken.None);
        try
        {
            var ins = await exec.ExecuteWriteAsync(
                new[] { DmlGenerator.Insert(null, tbl, new[] { new ColumnValue("created_at", null) }) },
                CancellationToken.None);
            var newId = Convert.ToInt32(ins[0].Rows[0][ColumnIndex(ins[0], "id")]);

            var before = DateTime.UtcNow.AddSeconds(-5);
            var upd = await exec.ExecuteWriteAsync(
                new[] { DmlGenerator.Update(null, tbl,
                    assignments: new[]
                    {
                        new ColumnValue("created_at", new SqlExpression("now()")),
                        new ColumnValue("tag", new SqlExpression("gen_random_uuid()")),
                    },
                    keys: new[] { new ColumnValue("id", newId) },
                    returning: new[] { "id", "created_at", "tag" }) },
                CancellationToken.None);

            var result = Assert.Single(upd);
            Assert.True(result.Success, result.Error?.Message);

            // The value comes back evaluated, which is the whole point: it exists nowhere else.
            var returned = Assert.Single(result.Rows);
            var written = Assert.IsType<DateTime>(returned[ColumnIndex(result, "created_at")]);
            Assert.Equal(DateTimeKind.Utc, written.Kind);                 // timestamptz, §5.5
            Assert.InRange(written, before, DateTime.UtcNow.AddSeconds(5));
            var tag = Assert.IsType<Guid>(returned[ColumnIndex(result, "tag")]);
            Assert.NotEqual(Guid.Empty, tag);

            // …and it is what actually landed in the table, not just what RETURNING said.
            var reread = await exec.ExecuteAsync(
                $"select created_at from {tbl} where id = {newId}", new QueryOptions(), CancellationToken.None);
            Assert.Equal(written, Assert.Single(reread[0].Rows)[0]);
        }
        finally
        {
            await exec.ExecuteAsync($"drop table if exists {tbl};", new QueryOptions(), CancellationToken.None);
        }
    }

    /// <summary>
    /// The grid hands back a string for every column Npgsql reads as one, not just <c>text</c>. Bound as
    /// <c>text</c>, a jsonb edit failed with "column is of type jsonb but expression is of type text" (no
    /// implicit cast); bound as <c>unknown</c>, the server types each value from its column like a literal.
    /// Covers the assignment, insert and key-predicate positions, and that a text column still gets text.
    /// </summary>
    [SkippableFact]
    public async Task String_values_take_their_column_type_jsonb_enum_and_text()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        const string schema = "bearing_string_params";
        await exec.ExecuteAsync(
            $"drop schema if exists {schema} cascade; create schema {schema}; " +
            $"create type {schema}.mood as enum ('calm', 'busy'); " +
            $"create table {schema}.t (id int primary key, doc jsonb, mood {schema}.mood, code text);",
            new QueryOptions(), CancellationToken.None);
        try
        {
            // A pool loads the server's types when it opens, so one opened before the enum existed can't
            // read it back. The writes use a pool opened after setup, as the app's would be.
            await using var fresh = provider.CreateConnectionFactory(Info(), Password);
            exec = provider.CreateQueryExecutor(fresh);

            var ins = await exec.ExecuteWriteAsync(
                new[] { DmlGenerator.Insert(schema, "t", new[]
                {
                    new ColumnValue("id", 1), new ColumnValue("doc", """{"a": 1}"""),
                    new ColumnValue("mood", "calm"), new ColumnValue("code", "007"),
                }) },
                CancellationToken.None);
            Assert.True(Assert.Single(ins).Success, ins[0].Error?.Message);

            var upd = await exec.ExecuteWriteAsync(
                new[] { DmlGenerator.Update(schema, "t",
                    assignments: new[] { new ColumnValue("doc", """{"b": [true]}"""), new ColumnValue("mood", "busy") },
                    keys: new[] { new ColumnValue("id", 1), new ColumnValue("code", "007") }) },
                CancellationToken.None);
            Assert.True(Assert.Single(upd).Success, upd[0].Error?.Message);
            Assert.Equal(1, upd[0].RowCount);

            // A jsonb key: the predicate is jsonb = unknown, resolved to jsonb equality.
            var byDoc = await exec.ExecuteWriteAsync(
                new[] { DmlGenerator.Update(schema, "t",
                    assignments: new[] { new ColumnValue("code", "008") },
                    keys: new[] { new ColumnValue("doc", """{"b":[true]}""") }) },
                CancellationToken.None);
            Assert.True(Assert.Single(byDoc).Success, byDoc[0].Error?.Message);
            Assert.Equal(1, byDoc[0].RowCount);

            var after = await exec.ExecuteAsync(
                $"select doc::text, mood::text, code, pg_typeof(code)::text from {schema}.t",
                new QueryOptions(), CancellationToken.None);
            var row = Assert.Single(after[0].Rows);
            Assert.Equal("""{"b": [true]}""", row[0]);
            Assert.Equal("busy", row[1]);
            Assert.Equal("008", row[2]);                     // text stays text: no leading zero lost
            Assert.Equal("text", row[3]);
        }
        finally
        {
            await exec.ExecuteAsync($"drop schema if exists {schema} cascade;", new QueryOptions(), CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task Failing_command_rolls_back_the_whole_batch()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

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
            var count = await exec.CountAsync(CountSql($"select * from {tbl}"), CancellationToken.None);
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
