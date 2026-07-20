using Squirrel.Sql;
using Xunit;

namespace Squirrel.Sql.Tests;

public class WriteGuardTests
{
    [Theory]
    [InlineData("select * from film")]
    [InlineData("  -- just a comment\n")]
    [InlineData("select count(*) from rental where amount > 5")]
    [InlineData("explain select * from film")]                 // plain EXPLAIN doesn't execute
    [InlineData("with recent as (select * from rental) select * from recent")]
    public void Read_only_batches_have_no_risk(string sql)
        => Assert.Empty(WriteGuard.FindRiskyStatements(sql));

    [Theory]
    [InlineData("delete from rental", "DELETE")]
    [InlineData("update film set title = 'x' where film_id = 1", "UPDATE")]
    [InlineData("insert into actor(first_name) values ('a')", "INSERT")]
    [InlineData("truncate table rental", "TRUNCATE")]
    [InlineData("drop table film", "DROP")]
    [InlineData("alter table film add column note text", "ALTER")]
    public void Single_risky_statement_is_flagged(string sql, string verb)
    {
        var risky = WriteGuard.FindRiskyStatements(sql);
        Assert.Equal(new[] { verb }, risky);
        Assert.True(WriteGuard.HasRisk(sql));
    }

    [Fact]
    public void Multi_statement_batch_reports_distinct_verbs_in_order()
    {
        var sql = "select 1;\nupdate film set title='x' where film_id=1;\ndelete from rental;\nupdate actor set last_name='y';";
        Assert.Equal(new[] { "UPDATE", "DELETE" }, WriteGuard.FindRiskyStatements(sql));
    }

    [Fact]
    public void Data_modifying_cte_is_caught_through_the_with_preamble()
    {
        var sql = "with removed as (delete from rental where rental_id < 10 returning *) select count(*) from removed";
        Assert.Equal(new[] { "DELETE" }, WriteGuard.FindRiskyStatements(sql));
    }

    [Fact]
    public void Explain_analyze_that_actually_executes_is_caught()
    {
        // EXPLAIN ANALYZE runs its inner statement, so a delete really happens.
        Assert.Equal(new[] { "DELETE" }, WriteGuard.FindRiskyStatements("explain analyze delete from rental"));
    }

    [Fact]
    public void Identifier_that_merely_looks_like_a_verb_does_not_trip_the_guard()
    {
        // "update" here is a column alias, not a statement verb — a plain SELECT is never scanned interior.
        Assert.Empty(WriteGuard.FindRiskyStatements("select modified_at as update from film"));
    }
}
