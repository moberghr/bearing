using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Sql;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The plan parser against plans a real Postgres produced.
/// <para>
/// <c>ExplainTests</c> in the Sql suite parses hand-written JSON, which proves the parser reads the shape
/// <i>I believe</i> Postgres emits — the same assumption that let a CRLF bug through a green folding suite.
/// These run the actual EXPLAIN, so a field named differently, moved, or dropped by a Postgres release fails
/// here instead of silently producing a plan with every number missing.
/// </para>
/// </summary>
public class ExplainLiveTests
{
    private static async Task<(IQueryExecutor Exec, IDbConnectionFactory Factory)> Connect()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);
        return (provider.CreateQueryExecutor(factory), factory);
    }

    /// <summary>The JSON out of an EXPLAIN batch: the one result that came back with rows.</summary>
    private static string? PlanJson(System.Collections.Generic.IReadOnlyList<QueryResult> results)
        => results.FirstOrDefault(r => r.Rows.Count > 0)?.Rows[0][0]?.ToString();

    [SkippableFact]
    public async Task A_real_plan_only_explain_parses()
    {
        var (exec, factory) = await Connect();
        await using var _ = factory;

        var request = ExplainSql.Plan("select count(*) from film where length > 100");
        var results = await exec.ExecuteAsync(request.Sql, new QueryOptions(), CancellationToken.None);

        var plan = ExplainPlanParser.Parse(PlanJson(results), request.Analyzed, request.RolledBack);

        Assert.NotNull(plan);
        Assert.NotEqual("(unknown)", plan!.Root.NodeType);
        // Costs and row estimates are what a plan-only EXPLAIN is for; a null here means a renamed field.
        Assert.NotNull(plan.Root.EstimatedCost);
        Assert.NotNull(plan.Root.EstimatedRows);
        // …and no timings, because nothing ran.
        Assert.Null(plan.Root.ActualMs);
        Assert.False(plan.Analyzed);
    }

    [SkippableFact]
    public async Task A_real_analyzed_explain_carries_timings_and_buffers()
    {
        var (exec, factory) = await Connect();
        await using var _ = factory;

        var request = ExplainSql.Measured("select count(*) from film f join language l on l.language_id = f.language_id");
        var results = await exec.ExecuteAsync(request.Sql, new QueryOptions(), CancellationToken.None);

        var plan = ExplainPlanParser.Parse(PlanJson(results), request.Analyzed, request.RolledBack);

        Assert.NotNull(plan);
        Assert.True(plan!.Analyzed);
        Assert.True(plan.RolledBack);
        // Every one of these is a field name this parser guesses at, and each is the whole point of ANALYZE.
        Assert.NotNull(plan.ExecutionMs);
        Assert.NotNull(plan.Root.ActualMs);
        Assert.NotNull(plan.Root.ActualRows);
        Assert.NotNull(plan.Root.Loops);
        Assert.NotNull(plan.Root.SharedBlocksRead);
        // A join means more than one node, so the tree really is a tree.
        Assert.True(plan.Root.Flatten().Count() > 1, "a join produced a single-node plan");
        Assert.NotNull(plan.Root.SelfMs);
    }

    [SkippableFact]
    public async Task A_scan_reports_the_relation_it_reads()
    {
        // Relation Name is what makes a plan readable — a tree of node types with no table names is a puzzle.
        var (exec, factory) = await Connect();
        await using var _ = factory;

        var request = ExplainSql.Plan("select * from film where title = 'x'");
        var results = await exec.ExecuteAsync(request.Sql, new QueryOptions(), CancellationToken.None);

        var plan = ExplainPlanParser.Parse(PlanJson(results), request.Analyzed, request.RolledBack)!;

        Assert.Contains("film", plan.Root.Flatten().Select(n => n.Relation));
    }

    /// <summary>
    /// The safety claim, against a real server: an analysed write leaves nothing behind.
    /// <para>
    /// This is the assertion that matters most in the whole feature. <c>EXPLAIN ANALYZE</c> executes the
    /// statement, so without the surrounding transaction this would really delete the rows — and in a user's
    /// hands, from whatever they were pointed at.
    /// </para>
    /// <para>
    /// Against its own table, not pagila's. A first version deleted from <c>film</c>, which is referenced by
    /// <c>inventory</c>: the statement failed on the foreign key, the EXPLAIN returned no plan, and the row
    /// count was unchanged — so the test "passed" the count assertion while proving nothing about the
    /// rollback. A table nothing references is the only way the delete can actually succeed and therefore the
    /// only way the rollback is under test.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task An_analyzed_delete_runs_but_does_not_persist()
    {
        var (exec, factory) = await Connect();
        await using var _ = factory;
        await PgTestServer.RequireWritableAsync(factory);

        const string table = "bearing_explain_rollback";
        async Task Run(string sql) =>
            await exec.ExecuteAsync(sql, new QueryOptions(), CancellationToken.None);

        async Task<long> Count()
        {
            var r = await exec.ExecuteAsync($"select count(*) from {table}", new QueryOptions(), CancellationToken.None);
            return System.Convert.ToInt64(r[0].Rows[0][0]);
        }

        await Run($"drop table if exists {table}");
        await Run($"create table {table} (id int primary key)");
        try
        {
            await Run($"insert into {table} select generate_series(1, 50)");
            Assert.Equal(50, await Count());

            var request = ExplainSql.Measured($"delete from {table} where id > 0");
            var results = await exec.ExecuteAsync(request.Sql, new QueryOptions(), CancellationToken.None);

            // The statement really ran: an analysed plan exists, with measured timings.
            var plan = ExplainPlanParser.Parse(PlanJson(results), request.Analyzed, request.RolledBack);
            Assert.NotNull(plan);
            Assert.True(plan!.Analyzed);
            Assert.NotNull(plan.ExecutionMs);

            // …and every row survived it.
            Assert.Equal(50, await Count());
        }
        finally
        {
            await Run($"drop table if exists {table}");
        }
    }

    /// <summary>
    /// And the control: without the transaction, the same delete really does delete.
    /// <para>
    /// Otherwise the test above proves only that the delete did not happen — which a statement that failed,
    /// or an EXPLAIN that never ran it, would satisfy just as well. This is the half that shows the rollback
    /// is what saved the rows.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Without_the_transaction_the_same_delete_would_persist()
    {
        var (exec, factory) = await Connect();
        await using var _ = factory;
        await PgTestServer.RequireWritableAsync(factory);

        const string table = "bearing_explain_control";
        async Task Run(string sql) =>
            await exec.ExecuteAsync(sql, new QueryOptions(), CancellationToken.None);

        async Task<long> Count()
        {
            var r = await exec.ExecuteAsync($"select count(*) from {table}", new QueryOptions(), CancellationToken.None);
            return System.Convert.ToInt64(r[0].Rows[0][0]);
        }

        await Run($"drop table if exists {table}");
        await Run($"create table {table} (id int primary key)");
        try
        {
            await Run($"insert into {table} select generate_series(1, 50)");

            // Deliberately the unwrapped form — what ExplainSql.Measured would produce without its BEGIN.
            await Run($"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) delete from {table} where id > 0");

            Assert.Equal(0, await Count());
        }
        finally
        {
            await Run($"drop table if exists {table}");
        }
    }
}
