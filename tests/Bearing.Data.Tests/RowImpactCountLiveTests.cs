using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Sql;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// #112's count against a real server: the number the confirmation shows is the number of rows the statement
/// then changes. Live rather than a fixture (§4.2) because the claim is about Postgres' agreement between a
/// predicate counted and the same predicate executed — a fixture would only confirm our own arithmetic, and
/// a reduction that quietly counted the wrong rows would still look right in one.
/// </summary>
public class RowImpactCountLiveTests
{
    private static ConnectionInfo Info() => PgTestServer.Info();
    private static string Password => PgTestServer.Password;

    private const string Table = "bearing_row_impact_test";

    /// <summary>
    /// Counted, then run, then compared. This is the whole promise of the feature: "this will update N rows"
    /// has to be the N that <c>UPDATE</c> reports back, or the prompt is worse than no prompt.
    /// </summary>
    [SkippableTheory]
    // A predicate that matches some rows, in both verbs.
    [InlineData("update {0} set note = 'touched' where id % 2 = 0", 5)]
    [InlineData("delete from {0} where id > 7", 3)]
    // An aliased target — the case that fails outright if the alias does not travel into the count.
    [InlineData("update {0} t set note = 'x' where t.id <= 4", 4)]
    [InlineData("delete from {0} t where t.id <= 4", 4)]
    // A predicate that matches nothing. Zero is a real answer and must not be reported as "could not count".
    [InlineData("delete from {0} where id > 1000", 0)]
    // A subquery in the predicate: counting it is exactly as correct as running it.
    [InlineData("delete from {0} where id in (select id from {0} where note = 'seed-1')", 1)]
    public async Task The_counted_rows_are_the_rows_the_statement_changes(string template, int expected)
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        var statement = string.Format(template, Table);

        await Seed(exec);
        try
        {
            var target = UpdateDeleteTarget.TryReduce(statement);
            Assert.NotNull(target);
            Assert.False(target.EveryRow);

            var counted = await exec.CountAsync(CountSql(target), CancellationToken.None);
            Assert.Equal(expected, counted);

            // …and the server agrees when the statement actually runs.
            var run = await exec.ExecuteAsync(statement, new QueryOptions(), CancellationToken.None);
            Assert.True(run[0].Success, run[0].Error?.Message);
            Assert.Equal(expected, run[0].RowCount);
        }
        finally
        {
            await Drop(exec);
        }
    }

    /// <summary>
    /// #100's case end to end: no WHERE, so the reduction says "every row" without asking the server — and
    /// the statement then touches exactly the table's row count.
    /// </summary>
    [SkippableFact]
    public async Task A_statement_with_no_where_clause_touches_every_row()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        await Seed(exec);
        try
        {
            var target = UpdateDeleteTarget.TryReduce($"delete from {Table}");
            Assert.NotNull(target);
            Assert.True(target.EveryRow);

            // The count is not taken for this case, but the query it would run must still be the whole
            // table — that is what makes "every row" the same claim as a count of it.
            Assert.Equal(10, await exec.CountAsync(CountSql(target), CancellationToken.None));

            var run = await exec.ExecuteAsync($"delete from {Table}", new QueryOptions(), CancellationToken.None);
            Assert.Equal(10, run[0].RowCount);
        }
        finally
        {
            await Drop(exec);
        }
    }

    /// <summary>
    /// A relation the connecting role cannot read makes the count fail, and that failure must reach the
    /// caller as an exception rather than as a zero — <c>CountAsync</c> reserves null for a shape that
    /// cannot be wrapped at all, and #112 turns a throw into "could not count", never into a number.
    /// </summary>
    [SkippableFact]
    public async Task A_count_against_a_missing_relation_fails_rather_than_reporting_zero()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        var target = UpdateDeleteTarget.TryReduce("delete from bearing_no_such_table_112 where id = 1");
        Assert.NotNull(target);

        await Assert.ThrowsAnyAsync<Exception>(
            () => exec.CountAsync(CountSql(target), CancellationToken.None));
    }

    /// <summary>
    /// The count query as the app builds it: <see cref="UpdateDeleteTarget.RowsSql"/> wrapped by the
    /// connection's dialect, because <see cref="IQueryExecutor.CountAsync"/> runs the text it is handed and
    /// does not wrap it. Handing it <c>RowsSql</c> bare returns the first column of the first row, which is
    /// a plausible-looking number and the one failure this suite exists to catch.
    /// </summary>
    private static string CountSql(UpdateDeleteTarget target)
        => PostgresDialect.Instance.CountWrap(target.RowsSql)
           ?? throw new InvalidOperationException($"Postgres refused to wrap: {target.RowsSql}");

    private static async Task Seed(IQueryExecutor exec)
    {
        await exec.ExecuteAsync(
            $"""
             drop table if exists {Table};
             create table {Table} (id int primary key, note text);
             insert into {Table} (id, note)
             select i, 'seed-' || i from generate_series(1, 10) as g(i);
             """,
            new QueryOptions(), CancellationToken.None);
    }

    private static Task Drop(IQueryExecutor exec)
        => exec.ExecuteAsync($"drop table if exists {Table};", new QueryOptions(), CancellationToken.None);
}
