using System;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Data.MySql;
using Bearing.Sql;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// Integration tests for the MySQL / MariaDB provider, every one run against <b>both</b> engines
/// (<c>./build/test-db-mysql.sh</c> starts them). Each test creates its own tables, named per engine so the two
/// runs cannot collide, and drops them in a <c>finally</c> (§4.7).
/// </summary>
public class MySqlLiveTests
{
    private static readonly IDbProvider Provider = new MySqlProvider();
    private static readonly ISqlDialect Dialect = MySqlDialect.Instance;

    public static TheoryData<MySqlEngine> Engines => new() { MySqlEngine.MariaDb, MySqlEngine.MySql };

    /// <summary>Tens of billions of rows to count — long enough to be stopped, and a query that fails when it is.</summary>
    private const string HeavyQuery =
        "select count(*) from information_schema.columns a, information_schema.columns b, information_schema.columns c";

    private static string Table(MySqlEngine engine, string name) => $"bt_{name}_{engine.ToString().ToLowerInvariant()}";

    private static async Task<IReadOnlyList<QueryResult>> RunAsync(IQueryExecutor exec, string sql)
    {
        var results = await exec.ExecuteAsync(sql, new QueryOptions(), CancellationToken.None);
        var failed = results.FirstOrDefault(r => r.Error is not null);
        Assert.True(failed is null, failed?.Error?.Message);
        return results;
    }

    private static async Task DropAsync(IQueryExecutor exec, params string[] tables)
    {
        foreach (var t in tables)
        {
            await exec.ExecuteAsync($"drop table if exists {t}", new QueryOptions(), CancellationToken.None);
        }
    }

    // ---- Executor ----

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task A_run_of_writes_reports_its_total_and_a_select_its_rows(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var factory = MySqlTestServer.Factory(engine);
        var exec = Provider.CreateQueryExecutor(factory);
        var t = Table(engine, "batch");
        try
        {
            await RunAsync(exec, $"drop table if exists {t}; create table {t} (id int primary key, v varchar(10))");

            // The protocol folds a run of non-row statements into one result, so a batch of writes reports
            // their total — 3 inserted + 2 updated (measured; SqlClient does the same, see its executor).
            var writes = await RunAsync(exec, $"insert into {t} values (1,'a'),(2,'b'),(3,'c'); update {t} set v = 'z' where id < 3");
            var total = Assert.Single(writes);
            Assert.Empty(total.Columns);
            Assert.Equal(5, total.RowCount);
            Assert.Equal("5 row(s) affected", total.Message);

            var read = await RunAsync(exec, $"select * from {t} order by id; select count(*) from {t}");
            Assert.Equal(2, read.Count);
            Assert.Equal(3, read[0].Rows.Count);
            Assert.Equal("z", read[0].Rows[0][1]);
        }
        finally
        {
            await DropAsync(exec, t);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task Result_columns_carry_their_origin_by_name_qualified_by_database(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var factory = MySqlTestServer.Factory(engine);
        var exec = Provider.CreateQueryExecutor(factory);
        var t = Table(engine, "origin");
        try
        {
            await RunAsync(exec, $"drop table if exists {t}; create table {t} (id int primary key, label varchar(10))");
            var result = (await RunAsync(exec, $"select id, label as caption, 1 + 1 as two from {t}"))[0];

            Assert.Equal(t, result.Columns[0].BaseTableName);
            Assert.Equal(MySqlTestServer.Database, result.Columns[0].BaseSchemaName);
            Assert.Equal(MySqlTestServer.Database, result.Columns[0].BaseCatalogName);
            Assert.Equal("label", result.Columns[1].BaseColumnName);   // the alias resolves to its column
            Assert.Null(result.Columns[2].BaseTableName);              // an expression has no origin
        }
        finally
        {
            await DropAsync(exec, t);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task The_dialects_page_wrap_and_count_sql_is_accepted(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var factory = MySqlTestServer.Factory(engine);
        var exec = Provider.CreateQueryExecutor(factory);
        var t = Table(engine, "page");
        try
        {
            await RunAsync(exec, $"drop table if exists {t}; create table {t} (id int primary key)");
            await RunAsync(exec,
                $"insert into {t} with recursive n(i) as (select 1 union all select i + 1 from n where i < 50) select i from n");

            var query = $"select id from {t} order by id";
            var page = await exec.ExecutePageAsync(Dialect.TryAppendPage(query, 20, 10)!, CancellationToken.None);
            Assert.Null(page.Error);
            Assert.Equal(new object[] { 21, 30 }, new[] { page.Rows[0][0]!, page.Rows[^1][0]! });

            var wrapped = await exec.ExecutePageAsync(Dialect.Wrap(query + " limit 30", 25, 10)!, CancellationToken.None);
            Assert.Null(wrapped.Error);
            Assert.Equal(5, wrapped.Rows.Count);

            Assert.Equal(50, await exec.CountAsync(Dialect.CountWrap(query)!, CancellationToken.None));
            // Two columns of one name: legal on its own, error 1060 as a derived table — no total, no throw.
            Assert.Null(await exec.CountAsync(Dialect.CountWrap($"select id, id from {t}")!, CancellationToken.None));
        }
        finally
        {
            await DropAsync(exec, t);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task A_delimiter_script_splits_into_statements_the_server_runs(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var factory = MySqlTestServer.Factory(engine);
        var exec = Provider.CreateQueryExecutor(factory);
        var proc = $"bt_proc_{engine.ToString().ToLowerInvariant()}";
        const string script = """
            DELIMITER //
            create procedure {0}(in n int)
            begin
              declare x int default 0;
              if n > 0 then set x = n * 2; end if;
              select x as doubled;
            end //
            DELIMITER ;
            call {0}(21);
            """;
        try
        {
            await exec.ExecuteAsync($"drop procedure if exists {proc}", new QueryOptions(), CancellationToken.None);
            var spans = Dialect.SplitStatements(string.Format(script, proc));
            Assert.Equal(2, spans.Count);

            await RunAsync(exec, spans[0].Text);
            var called = await RunAsync(exec, spans[1].Text);
            Assert.Equal(42L, Convert.ToInt64(called[0].Rows[0][0]));
        }
        finally
        {
            await exec.ExecuteAsync($"drop procedure if exists {proc}", new QueryOptions(), CancellationToken.None);
        }
    }

    // ---- Writes and transactions ----

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task A_generated_insert_saves_and_returns_the_row_where_the_engine_can(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var factory = MySqlTestServer.Factory(engine);
        var exec = Provider.CreateQueryExecutor(factory);
        var t = Table(engine, "insert");
        try
        {
            await RunAsync(exec, $"drop table if exists {t}; create table {t} (id int auto_increment primary key, v varchar(10))");
            var command = new SqlWriteCommand(
                Dialect.InsertStatement(Dialect.Quote(t), "`v`", "@p0", withReturning: true),
                new[] { new SqlParameter("@p0", "hello") },
                Dialect.InsertStatement(Dialect.Quote(t), "`v`", "@p0", withReturning: false));

            var results = await exec.ExecuteWriteAsync(new[] { command }, CancellationToken.None);

            Assert.Null(results[0].Error);
            if (engine == MySqlEngine.MariaDb)
            {
                // MariaDB 10.5+ returns the written row — the generated key included.
                Assert.Equal(1, Convert.ToInt32(results[0].Rows[0][0]));
            }
            else
            {
                // MySQL has no RETURNING: the plain form ran, and nothing came back.
                Assert.Empty(results[0].Rows);
            }
            var stored = await RunAsync(exec, $"select v from {t}");
            Assert.Equal("hello", stored[0].Rows[0][0]);
        }
        finally
        {
            await DropAsync(exec, t);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task A_held_transaction_rolls_back_and_a_count_inside_it_leaves_it_intact(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var factory = MySqlTestServer.Factory(engine);
        var exec = Provider.CreateQueryExecutor(factory);
        var t = Table(engine, "tx");
        try
        {
            await RunAsync(exec, $"drop table if exists {t}; create table {t} (id int primary key) engine=InnoDB");

            await using (var scope = Provider.CreateTransactionScope(factory))
            {
                await scope.BeginAsync(CancellationToken.None);
                await RunAsync(scope.Executor, $"insert into {t} values (1), (2)");
                // An uncountable shape inside the transaction: refused, and the transaction survives it.
                Assert.Null(await scope.Executor.CountAsync(Dialect.CountWrap($"select id, id from {t}")!, CancellationToken.None));
                Assert.Equal(2, await scope.Executor.CountAsync(Dialect.CountWrap($"select * from {t}")!, CancellationToken.None));
                Assert.Equal(TransactionState.Active, scope.State);
                await scope.RollbackAsync(CancellationToken.None);
            }
            Assert.Equal(0L, Convert.ToInt64((await RunAsync(exec, $"select count(*) from {t}"))[0].Rows[0][0]));

            await using (var scope = Provider.CreateTransactionScope(factory))
            {
                await scope.BeginAsync(CancellationToken.None);
                await RunAsync(scope.Executor, $"insert into {t} values (3)");
                await scope.CommitAsync(CancellationToken.None);
            }
            Assert.Equal(1L, Convert.ToInt64((await RunAsync(exec, $"select count(*) from {t}"))[0].Rows[0][0]));
        }
        finally
        {
            await DropAsync(exec, t);
        }
    }

    // ---- Read-only and timeout ----

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task A_read_only_connection_is_refused_by_the_server_on_every_pooled_session(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var writable = MySqlTestServer.Factory(engine);
        var rw = Provider.CreateQueryExecutor(writable);
        var t = Table(engine, "ro");
        var proc = $"bt_ro_proc_{engine.ToString().ToLowerInvariant()}";
        try
        {
            await RunAsync(rw, $"drop table if exists {t}; create table {t} (id int primary key)");
            await RunAsync(rw, $"drop procedure if exists {proc}");
            // A write the lexer cannot see: the server is the one refusing it.
            await RunAsync(rw, $"create procedure {proc}() insert into {t} values (1)");

            await using var readOnly = MySqlTestServer.Factory(engine, MySqlTestServer.Info(engine) with { ReadOnly = true });
            var ro = Provider.CreateQueryExecutor(readOnly);

            var direct = await ro.ExecuteAsync($"insert into {t} values (2)", new QueryOptions(), CancellationToken.None);
            Assert.Equal("1792", direct[0].Error?.SqlState);

            var viaProc = await ro.ExecuteAsync($"call {proc}()", new QueryOptions(), CancellationToken.None);
            Assert.NotNull(viaProc[^1].Error);

            // The user lifts it within one session; the next use of the pool is read-only again.
            await ro.ExecuteAsync("set session transaction read write", new QueryOptions(), CancellationToken.None);
            var after = await ro.ExecuteAsync($"insert into {t} values (3)", new QueryOptions(), CancellationToken.None);
            Assert.Equal("1792", after[0].Error?.SqlState);

            Assert.Equal(0L, Convert.ToInt64((await RunAsync(rw, $"select count(*) from {t}"))[0].Rows[0][0]));
        }
        finally
        {
            await rw.ExecuteAsync($"drop procedure if exists {proc}", new QueryOptions(), CancellationToken.None);
            await DropAsync(rw, t);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task A_statement_timeout_stops_the_statement_and_is_not_reported_as_a_cancel(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var factory = MySqlTestServer.Factory(engine, MySqlTestServer.Info(engine) with { StatementTimeoutSeconds = 1 });
        var exec = Provider.CreateQueryExecutor(factory);

        var started = DateTime.UtcNow;
        // Not SLEEP() or BENCHMARK(): a killed one *returns* (1 and 0) instead of failing, which is the
        // server's semantics for those two functions and would make the timeout look like a result.
        var result = (await exec.ExecuteAsync(HeavyQuery, new QueryOptions(), CancellationToken.None))[0];

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8));
        Assert.NotNull(result.Error);
        Assert.NotEqual(DbErrorKind.Canceled, Provider.Classify(result.Error!));
    }

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task Esc_cancels_a_running_statement_as_a_cancellation(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var factory = MySqlTestServer.Factory(engine);
        var exec = Provider.CreateQueryExecutor(factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => exec.ExecuteAsync(HeavyQuery, new QueryOptions(), cts.Token));
    }

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task A_wrong_password_is_an_authentication_failure(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var factory = Provider.CreateConnectionFactory(MySqlTestServer.Info(engine), "not-the-password");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => factory.TestConnectionAsync(CancellationToken.None));
        Assert.Equal(DbErrorKind.Authentication, Provider.ClassifyException(ex));
    }

    // ---- Metadata ----

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task The_snapshot_has_tables_columns_keys_and_resolvable_foreign_keys(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var factory = MySqlTestServer.Factory(engine);
        var exec = Provider.CreateQueryExecutor(factory);
        var parent = Table(engine, "parent");
        var child = Table(engine, "child");
        var view = Table(engine, "view");
        try
        {
            await RunAsync(exec, $"""
                drop view if exists {view}; drop table if exists {child}; drop table if exists {parent};
                create table {parent} (a int, b int, name varchar(20) not null, primary key (a, b));
                create table {child} (id int primary key, pb int, pa int,
                    constraint fk_{child} foreign key (pa, pb) references {parent} (a, b));
                create view {view} as select a, name from {parent}
                """);

            var reader = Provider.CreateMetadataReader(factory);
            Assert.Contains(MySqlTestServer.Database, await reader.GetDatabasesAsync(CancellationToken.None));
            Assert.DoesNotContain("mysql", await reader.GetDatabasesAsync(CancellationToken.None));

            var snapshot = await reader.LoadSnapshotAsync(MySqlTestServer.Database, CancellationToken.None);
            var p = snapshot.ResolveTable(null, parent)!;
            var c = snapshot.ResolveTable(MySqlTestServer.Database, child)!;
            Assert.Equal(RelationKind.View, snapshot.ResolveTable(null, view)!.Kind);

            var cols = snapshot.ColumnsOf(p.Id);
            Assert.Equal(new[] { "a", "b", "name" }, cols.Select(x => x.Name));
            Assert.True(cols[0].IsPrimaryKey && cols[1].IsPrimaryKey && !cols[2].IsPrimaryKey);
            Assert.Equal("varchar(20)", cols[2].DataType);
            Assert.True(cols[2].NotNull);

            var fk = Assert.Single(snapshot.ForeignKeysTouching(c.Id).Where(f => f.ParentTableId == c.Id));
            Assert.Equal(p.Id, fk.ReferencedTableId);
            // Composite, with the child's columns declared in the other order: pa (3) → a (1), pb (2) → b (2),
            // paired by position in the key rather than by column order.
            Assert.Equal(new[] { 3, 2 }, fk.ParentOrdinals);
            Assert.Equal(new[] { 1, 2 }, fk.ReferencedOrdinals);

            Assert.Contains("select", (await reader.GetViewDefinitionAsync(snapshot.ResolveTable(null, view)!.Id, CancellationToken.None))
                .ToLowerInvariant());
        }
        finally
        {
            await exec.ExecuteAsync($"drop view if exists {view}", new QueryOptions(), CancellationToken.None);
            await DropAsync(exec, child, parent);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task Table_details_read_indexes_constraints_triggers_and_column_extras(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var factory = MySqlTestServer.Factory(engine);
        var exec = Provider.CreateQueryExecutor(factory);
        var t = Table(engine, "detail");
        var trigger = $"trg_{t}";
        try
        {
            await RunAsync(exec, $"""
                drop table if exists {t};
                create table {t} (
                    id int auto_increment primary key,
                    email varchar(50) not null default 'none',
                    qty int, doubled int as (qty * 2),
                    constraint uq_{t} unique (email),
                    constraint ck_{t} check (qty >= 0)
                ) comment = 'the details';
                create index ix_{t} on {t} (qty);
                create trigger {trigger} before insert on {t} for each row set new.qty = coalesce(new.qty, 0)
                """);

            var reader = Provider.CreateMetadataReader(factory);
            var snapshot = await reader.LoadSnapshotAsync(MySqlTestServer.Database, CancellationToken.None);
            var details = await reader.GetTableDetailsAsync(snapshot.ResolveTable(null, t)!.Id, CancellationToken.None);

            Assert.Contains(details.Indexes, i => i.IsPrimary && i.Ordinals.SequenceEqual(new[] { 1 }));
            Assert.Contains(details.Indexes, i => i.Name == $"ix_{t}" && !i.IsUnique);
            Assert.Contains(details.Constraints, k => k.Kind == ConstraintKind.Unique && k.Name == $"uq_{t}");
            Assert.Contains(details.Constraints, k => k.Kind == ConstraintKind.Check && k.Definition.Contains("qty"));
            Assert.Contains(details.Triggers, x => x.Name == trigger);
            Assert.Equal(ColumnIdentity.ByDefault, details.ColumnAt(1)!.Identity);
            Assert.Contains("none", details.ColumnAt(2)!.Default);
            Assert.NotNull(details.ColumnAt(4)!.GeneratedExpression);
            Assert.Null(details.ColumnAt(4)!.Default);
            Assert.Equal("the details", details.Comment);

            var sizes = await reader.GetRelationSizesAsync(CancellationToken.None);
            Assert.Contains(sizes, s => s.TableId == snapshot.ResolveTable(null, t)!.Id);
        }
        finally
        {
            await DropAsync(exec, t);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task Routines_list_with_arguments_and_their_source(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var factory = MySqlTestServer.Factory(engine);
        var exec = Provider.CreateQueryExecutor(factory);
        var fn = $"bt_fn_{engine.ToString().ToLowerInvariant()}";
        try
        {
            await RunAsync(exec, $"drop function if exists {fn}");
            await RunAsync(exec, $"create function {fn}(x int) returns int deterministic return x + 1");

            var reader = Provider.CreateMetadataReader(factory);
            var routine = Assert.Single((await reader.GetRoutinesAsync(CancellationToken.None)).Where(r => r.Name == fn));
            Assert.Equal(RoutineKind.Function, routine.Kind);
            // MariaDB still spells the display width (int(11)); MySQL 8 dropped it.
            Assert.StartsWith("x int", routine.Arguments);
            Assert.StartsWith("int", routine.ReturnType);
            Assert.Contains("x + 1", await reader.GetRoutineDefinitionAsync(routine.Id, CancellationToken.None));
        }
        finally
        {
            await exec.ExecuteAsync($"drop function if exists {fn}", new QueryOptions(), CancellationToken.None);
        }
    }

    // ---- Server activity ----

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task Activity_lists_a_running_statement_and_cancel_stops_it(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);
        await using var factory = MySqlTestServer.Factory(engine);
        var exec = Provider.CreateQueryExecutor(factory);
        var activity = Provider.CreateServerActivity(factory);

        var marker = $"bearing_activity_{Guid.NewGuid():N}";
        var running = exec.ExecuteAsync($"select sleep(20) as {marker}", new QueryOptions(), CancellationToken.None);
        try
        {
            BackendActivity? row = null;
            for (var i = 0; i < 40 && row is null; i++)
            {
                await Task.Delay(100);
                var seen = await activity.GetActivityAsync(new ActivityFilter(MySqlTestServer.Database), CancellationToken.None);
                Assert.True(seen.SeesAllSessions);   // root holds PROCESS
                row = seen.Backends.FirstOrDefault(b => b.Query?.Contains(marker) == true);
            }
            Assert.NotNull(row);
            Assert.Equal("active", row!.State);

            Assert.True(await activity.CancelBackendAsync(row.Pid, CancellationToken.None));
            var result = (await running)[0];
            // KILL QUERY on a SLEEP() ends it early; MySQL reports the interrupted sleep as 1, MariaDB as an
            // interruption — either way the statement is over long before its 20 seconds.
            Assert.True(result.Error is not null || Convert.ToInt64(result.Rows[0][0]) == 1);
        }
        finally
        {
            await running.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
