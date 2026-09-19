using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Data.SqlServer;
using Bearing.Sql;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// What a held transaction actually does on a server (#131) — the half no fake can answer, and the half the
/// feature is: that a write inside one is <b>invisible to everyone else</b> until it commits, that a rollback
/// loses it, and that a failed statement leaves it in a state a commit must not be offered for.
/// <para>
/// Every assertion here is made from a <b>second</b> executor on the same pool, not from the transaction's
/// own. Asking the transaction what it can see would prove only that a connection sees its own writes, which
/// was never in doubt; the claim worth testing is the one about everybody else (§4.7).
/// </para>
/// <para>
/// Both engines, because the abort rule is one rule applied to two different truths: Postgres really does
/// doom a transaction on any error, and SQL Server does not always. What is asserted per engine is what that
/// engine does, never what we wish it did.
/// </para>
/// </summary>
public class ManualCommitLiveTests
{
    private const string Table = "bearing_tx_test";

    // ---- PostgreSQL --------------------------------------------------------------------------

    [SkippableFact]
    public async Task Postgres_holds_a_write_out_of_sight_until_it_commits()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireWritableAsync(factory);

        var outside = provider.CreateQueryExecutor(factory);
        await Reset(outside, $"drop table if exists {Table}; create table {Table} (id int primary key);");
        try
        {
            await using var scope = provider.CreateTransactionScope(factory);
            await scope.BeginAsync(CancellationToken.None);
            Assert.Equal(TransactionState.Active, scope.State);

            await Run(scope.Executor, $"insert into {Table} values (1)");

            // The transaction sees its own write; nobody else does. Both halves, because only the second is
            // a claim about the server rather than about a connection.
            Assert.Equal(1, await Count(scope.Executor, PostgresDialect.Instance));
            Assert.Equal(0, await Count(outside, PostgresDialect.Instance));

            await scope.CommitAsync(CancellationToken.None);
            Assert.Equal(TransactionState.None, scope.State);
            Assert.Equal(1, await Count(outside, PostgresDialect.Instance));
        }
        finally { await Reset(outside, $"drop table if exists {Table};"); }
    }

    [SkippableFact]
    public async Task Postgres_rollback_loses_the_write()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireWritableAsync(factory);

        var outside = provider.CreateQueryExecutor(factory);
        await Reset(outside, $"drop table if exists {Table}; create table {Table} (id int primary key);");
        try
        {
            await using var scope = provider.CreateTransactionScope(factory);
            await scope.BeginAsync(CancellationToken.None);
            await Run(scope.Executor, $"insert into {Table} values (1)");
            await scope.RollbackAsync(CancellationToken.None);

            Assert.Equal(0, await Count(outside, PostgresDialect.Instance));
        }
        finally { await Reset(outside, $"drop table if exists {Table};"); }
    }

    /// <summary>
    /// The reason <see cref="TransactionState.Aborted"/> exists. Postgres answers <c>25P02</c> to every
    /// statement after an error until the transaction ends — so a commit offered here would either fail or,
    /// worse, be silently turned into a rollback. Measured rather than assumed.
    /// </summary>
    [SkippableFact]
    public async Task Postgres_dooms_the_whole_transaction_on_one_failed_statement()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireWritableAsync(factory);

        var outside = provider.CreateQueryExecutor(factory);
        await Reset(outside, $"drop table if exists {Table}; create table {Table} (id int primary key);");
        try
        {
            await using var scope = provider.CreateTransactionScope(factory);
            await scope.BeginAsync(CancellationToken.None);
            await Run(scope.Executor, $"insert into {Table} values (1)");

            var failed = await scope.Executor.ExecuteAsync(
                $"insert into {Table} values ('not a number')", new QueryOptions(), CancellationToken.None);
            Assert.False(failed.All(r => r.Success));
            Assert.Equal(TransactionState.Aborted, scope.State);

            // The server's own verdict, not ours: the next ordinary statement is refused too.
            var after = await scope.Executor.ExecuteAsync("select 1", new QueryOptions(), CancellationToken.None);
            Assert.Equal("25P02", after.Single(r => !r.Success).Error!.SqlState);

            // And the scope refuses to offer a commit for it.
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CommitAsync(CancellationToken.None));

            await scope.RollbackAsync(CancellationToken.None);
            Assert.Equal(0, await Count(outside, PostgresDialect.Instance));
        }
        finally { await Reset(outside, $"drop table if exists {Table};"); }
    }

    /// <summary>
    /// An inline-grid save joins the open transaction instead of committing itself — the one behaviour
    /// difference between the pinned executor and the pooled one, and the whole of #131 as far as
    /// <see cref="IQueryExecutor.ExecuteWriteAsync"/> is concerned.
    /// </summary>
    [SkippableFact]
    public async Task Postgres_write_batch_joins_the_open_transaction_instead_of_committing()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireWritableAsync(factory);

        var outside = provider.CreateQueryExecutor(factory);
        await Reset(outside, $"drop table if exists {Table}; create table {Table} (id int primary key);");
        try
        {
            await using var scope = provider.CreateTransactionScope(factory);
            await scope.BeginAsync(CancellationToken.None);

            var results = await scope.Executor.ExecuteWriteAsync(
                [new SqlWriteCommand($"insert into {Table} values (7)", [])], CancellationToken.None);
            Assert.All(results, r => Assert.True(r.Success));

            Assert.Equal(0, await Count(outside, PostgresDialect.Instance));   // not committed by the batch
            await scope.RollbackAsync(CancellationToken.None);
            Assert.Equal(0, await Count(outside, PostgresDialect.Instance));
        }
        finally { await Reset(outside, $"drop table if exists {Table};"); }
    }

    /// <summary>
    /// The savepoint around <see cref="IQueryExecutor.CountAsync"/>. A shape the count wrapper cannot take
    /// is an error, and on Postgres an error dooms the transaction — so without the savepoint, a speculative
    /// count Bearing runs on the user's behalf (the row-impact preview, §1.5) would destroy their
    /// uncommitted work. The transaction has to still be usable afterwards.
    /// </summary>
    [SkippableFact]
    public async Task Postgres_an_uncountable_shape_does_not_doom_the_transaction()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireWritableAsync(factory);

        var outside = provider.CreateQueryExecutor(factory);
        await Reset(outside, $"drop table if exists {Table}; create table {Table} (id int primary key);");
        try
        {
            await using var scope = provider.CreateTransactionScope(factory);
            await scope.BeginAsync(CancellationToken.None);
            await Run(scope.Executor, $"insert into {Table} values (1)");

            // A data-modifying CTE must stay top-level, so wrapping it is feature_not_supported — the
            // "this shape has no total" case, which comes back as null rather than as a failure.
            var wrapped = PostgresDialect.Instance.CountWrap(
                $"with d as (delete from {Table} returning *) select * from d")!;
            Assert.Null(await scope.Executor.CountAsync(wrapped, CancellationToken.None));

            // Still Active, and still able to run and commit — which is the assertion that matters.
            Assert.Equal(TransactionState.Active, scope.State);
            await Run(scope.Executor, $"insert into {Table} values (2)");
            await scope.CommitAsync(CancellationToken.None);

            Assert.Equal(2, await Count(outside, PostgresDialect.Instance));
        }
        finally { await Reset(outside, $"drop table if exists {Table};"); }
    }

    /// <summary>
    /// The count probe runs under a <b>deadline</b> (§1.5: 2 s per statement, 4 s for the batch), and a
    /// statement cancelled by its token surfaces as <see cref="OperationCanceledException"/> while the server
    /// has already raised 57014 and aborted the transaction block. Catching only the driver's own exception
    /// left the savepoint un-rolled-back: the scope went on reporting Active, Commit stayed on offer, and the
    /// COMMIT Postgres received was silently turned into a ROLLBACK — the one outcome this feature may not
    /// have. Found by review; this is the measurement.
    /// </summary>
    [SkippableFact]
    public async Task Postgres_a_count_cancelled_by_its_deadline_leaves_the_transaction_usable()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireWritableAsync(factory);

        var outside = provider.CreateQueryExecutor(factory);
        await Reset(outside, $"drop table if exists {Table}; create table {Table} (id int primary key);");
        try
        {
            await using var scope = provider.CreateTransactionScope(factory);
            await scope.BeginAsync(CancellationToken.None);
            await Run(scope.Executor, $"insert into {Table} values (1)");

            // A count that cannot finish inside its deadline — the shape the row-impact preview hits on a
            // large table, reproduced here with a sleep so it is deterministic.
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var slow = PostgresDialect.Instance.CountWrap("select pg_sleep(5)")!;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => scope.Executor.CountAsync(slow, deadline.Token));

            // The probe was ours; the transaction is the user's, and it survived.
            Assert.Equal(TransactionState.Active, scope.State);
            await Run(scope.Executor, $"insert into {Table} values (2)");
            await scope.CommitAsync(CancellationToken.None);

            Assert.Equal(2, await Count(outside, PostgresDialect.Instance));
        }
        finally { await Reset(outside, $"drop table if exists {Table};"); }
    }

    // ---- SQL Server --------------------------------------------------------------------------

    [SkippableFact]
    public async Task SqlServer_holds_a_write_out_of_sight_until_it_commits()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(MsSqlTestServer.Info(), MsSqlTestServer.Password);
        await MsSqlTestServer.RequireAsync(factory);

        var outside = provider.CreateQueryExecutor(factory);
        await Reset(outside, $"drop table if exists dbo.{Table}; create table dbo.{Table} (id int primary key);");
        try
        {
            await using var scope = provider.CreateTransactionScope(factory);
            await scope.BeginAsync(CancellationToken.None);
            await Run(scope.Executor, $"insert into dbo.{Table} values (1)");

            // READ COMMITTED is the default here, so an outside reader blocks on the uncommitted row rather
            // than reading zero — which is the same fact stated as a lock instead of as a count, and is why
            // this one reads with NOLOCK instead of counting. An idle transaction holding these is exactly
            // the hazard the idle clocks exist for.
            Assert.Equal(1, await Scalar(outside, $"select count(*) from dbo.{Table} with (nolock)"));
            Assert.Equal(0, await Scalar(outside, $"select count(*) from dbo.{Table} with (readpast)"));

            await scope.CommitAsync(CancellationToken.None);
            Assert.Equal(1, await Scalar(outside, $"select count(*) from dbo.{Table} with (readpast)"));
        }
        finally { await Reset(outside, $"drop table if exists dbo.{Table};"); }
    }

    [SkippableFact]
    public async Task SqlServer_rollback_loses_the_write()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(MsSqlTestServer.Info(), MsSqlTestServer.Password);
        await MsSqlTestServer.RequireAsync(factory);

        var outside = provider.CreateQueryExecutor(factory);
        await Reset(outside, $"drop table if exists dbo.{Table}; create table dbo.{Table} (id int primary key);");
        try
        {
            await using var scope = provider.CreateTransactionScope(factory);
            await scope.BeginAsync(CancellationToken.None);
            await Run(scope.Executor, $"insert into dbo.{Table} values (1)");
            await scope.RollbackAsync(CancellationToken.None);

            Assert.Equal(0, await Scalar(outside, $"select count(*) from dbo.{Table}"));
        }
        finally { await Reset(outside, $"drop table if exists dbo.{Table};"); }
    }

    /// <summary>
    /// The engine difference the abort rule is conservative about, stated rather than assumed: a duplicate
    /// key here does <b>not</b> doom the transaction — the very next statement runs — while on Postgres it
    /// would answer <c>25P02</c>. Bearing marks it aborted anyway and refuses to commit, which costs a
    /// rollback of work this server would have taken. That is the trade, and this is where it is visible.
    /// </summary>
    [SkippableFact]
    public async Task SqlServer_survives_an_error_the_abort_rule_treats_as_fatal_anyway()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(MsSqlTestServer.Info(), MsSqlTestServer.Password);
        await MsSqlTestServer.RequireAsync(factory);

        var outside = provider.CreateQueryExecutor(factory);
        await Reset(outside, $"drop table if exists dbo.{Table}; create table dbo.{Table} (id int primary key);");
        try
        {
            await using var scope = provider.CreateTransactionScope(factory);
            await scope.BeginAsync(CancellationToken.None);
            await Run(scope.Executor, $"insert into dbo.{Table} values (1)");

            var failed = await scope.Executor.ExecuteAsync(
                $"insert into dbo.{Table} values (1)", new QueryOptions(), CancellationToken.None);
            Assert.False(failed.All(r => r.Success));

            // The server is fine with it — this is the half that is not true of Postgres …
            var after = await scope.Executor.ExecuteAsync("select 1", new QueryOptions(), CancellationToken.None);
            Assert.True(after.All(r => r.Success));

            // … and Bearing refuses to commit regardless, which is the decision, not an accident.
            Assert.Equal(TransactionState.Aborted, scope.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CommitAsync(CancellationToken.None));

            await scope.RollbackAsync(CancellationToken.None);
            Assert.Equal(0, await Scalar(outside, $"select count(*) from dbo.{Table}"));
        }
        finally { await Reset(outside, $"drop table if exists dbo.{Table};"); }
    }

    [SkippableFact]
    public async Task SqlServer_write_batch_joins_the_open_transaction_instead_of_committing()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(MsSqlTestServer.Info(), MsSqlTestServer.Password);
        await MsSqlTestServer.RequireAsync(factory);

        var outside = provider.CreateQueryExecutor(factory);
        await Reset(outside, $"drop table if exists dbo.{Table}; create table dbo.{Table} (id int primary key);");
        try
        {
            await using var scope = provider.CreateTransactionScope(factory);
            await scope.BeginAsync(CancellationToken.None);

            var results = await scope.Executor.ExecuteWriteAsync(
                [new SqlWriteCommand($"insert into dbo.{Table} values (7)", [])], CancellationToken.None);
            Assert.All(results, r => Assert.True(r.Success));

            Assert.Equal(0, await Scalar(outside, $"select count(*) from dbo.{Table} with (readpast)"));
            await scope.RollbackAsync(CancellationToken.None);
            Assert.Equal(0, await Scalar(outside, $"select count(*) from dbo.{Table}"));
        }
        finally { await Reset(outside, $"drop table if exists dbo.{Table};"); }
    }

    /// <summary>The sibling of the Postgres case above, in SQL Server's vocabulary. The abort rule marks the
    /// transaction here (a cancel is a failure like any other), so what this pins is the narrower half: the
    /// savepoint was unwound, so the connection is still usable and the rollback that follows works rather
    /// than throwing on a transaction left in an unknown state.</summary>
    [SkippableFact]
    public async Task SqlServer_a_count_cancelled_by_its_deadline_leaves_the_connection_usable()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(MsSqlTestServer.Info(), MsSqlTestServer.Password);
        await MsSqlTestServer.RequireAsync(factory);

        var outside = provider.CreateQueryExecutor(factory);
        await Reset(outside, $"drop table if exists dbo.{Table}; create table dbo.{Table} (id int primary key);");
        try
        {
            await using var scope = provider.CreateTransactionScope(factory);
            await scope.BeginAsync(CancellationToken.None);
            await Run(scope.Executor, $"insert into dbo.{Table} values (1)");

            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var slow = SqlServerDialect.Instance.CountWrap(
                "select 1 as one from (select 1 as x) s cross apply (select 1 as y) t")!;
            // The sleep has to be inside the statement the count runs, so it is appended to the wrapper.
            await Assert.ThrowsAnyAsync<Exception>(
                () => scope.Executor.CountAsync("waitfor delay '00:00:05'; " + slow, deadline.Token));

            // Usable afterwards: the savepoint was unwound, so this reaches the server rather than throwing.
            await scope.RollbackAsync(CancellationToken.None);
            Assert.Equal(TransactionState.None, scope.State);
            Assert.Equal(0, await Scalar(outside, $"select count(*) from dbo.{Table}"));
        }
        finally { await Reset(outside, $"drop table if exists dbo.{Table};"); }
    }

    /// <summary>
    /// Msg 334 — "OUTPUT without INTO on a table with an enabled trigger" — makes the executor re-run the
    /// whole write batch with the returning clauses dropped. That is safe when it owns the transaction,
    /// because the failed attempt rolled back. <b>Joined to the user's transaction there is nothing to roll
    /// back</b>, so a command that already succeeded before a sibling raised 334 would be applied a second
    /// time: its triggers fired twice, its audit rows doubled. A savepoint around the attempt is what makes
    /// the retry start from a clean slate. Found by review; only a trigger-bearing table shows it.
    /// </summary>
    [SkippableFact]
    public async Task SqlServer_a_retried_write_batch_does_not_re_apply_what_already_ran()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(MsSqlTestServer.Info(), MsSqlTestServer.Password);
        await MsSqlTestServer.RequireAsync(factory);

        var outside = provider.CreateQueryExecutor(factory);
        const string audit = "bearing_tx_audit";
        await Reset(outside,
            $"drop table if exists dbo.{Table}; drop table if exists dbo.{audit};"
            + $" create table dbo.{audit} (what nvarchar(10));"
            + $" create table dbo.{Table} (id int identity primary key, note nvarchar(20));");
        // The trigger is what makes Msg 334 happen at all, and also what counts the double-apply.
        await Reset(outside,
            $"create trigger dbo.{Table}_audit on dbo.{Table} after insert, delete "
            + $"as insert into dbo.{audit}(what) values ('touched');");
        try
        {
            await Reset(outside, $"insert into dbo.{Table}(note) values ('doomed')");
            await Reset(outside, $"delete from dbo.{audit}");   // ignore the seed row's own trigger

            await using var scope = provider.CreateTransactionScope(factory);
            await scope.BeginAsync(CancellationToken.None);

            // A delete with no OUTPUT (it succeeds), then an insert that carries one (it raises 334).
            var results = await scope.Executor.ExecuteWriteAsync(
                [
                    new SqlWriteCommand($"delete from dbo.{Table} where id = 1", []),
                    new SqlWriteCommand(
                        $"insert into dbo.{Table}(note) output inserted.* values ('new')", [],
                        SqlWithoutReturning: $"insert into dbo.{Table}(note) values ('new')"),
                ],
                CancellationToken.None);
            Assert.All(results, r => Assert.True(r.Success, r.Error?.Message));

            // One delete and one insert reached the table, so the trigger fired exactly twice. Three means
            // the retry replayed the delete on top of its first, successful, run.
            Assert.Equal(2, await Scalar(scope.Executor, $"select count(*) from dbo.{audit}"));
            await scope.RollbackAsync(CancellationToken.None);
        }
        finally
        {
            await Reset(outside, $"drop table if exists dbo.{Table}; drop table if exists dbo.{audit};");
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static async Task Reset(IQueryExecutor exec, string sql)
    {
        var results = await exec.ExecuteAsync(sql, new QueryOptions(), CancellationToken.None);
        Assert.All(results, r => Assert.True(r.Success, r.Error?.Message));
    }

    private static async Task Run(IQueryExecutor exec, string sql)
    {
        var results = await exec.ExecuteAsync(sql, new QueryOptions(), CancellationToken.None);
        Assert.All(results, r => Assert.True(r.Success, r.Error?.Message));
    }

    private static async Task<long> Count(IQueryExecutor exec, ISqlDialect dialect)
        => await exec.CountAsync(dialect.CountWrap($"select * from {Table}")!, CancellationToken.None) ?? -1;

    private static async Task<long> Scalar(IQueryExecutor exec, string sql)
    {
        var results = await exec.ExecuteAsync(sql, new QueryOptions(), CancellationToken.None);
        var result = results.Single();
        Assert.True(result.Success, result.Error?.Message);
        return Convert.ToInt64(result.Rows.Single()[0]);
    }
}
