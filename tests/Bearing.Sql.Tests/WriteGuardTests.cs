using System.Linq;
using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

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
    [InlineData("create table t (id int)", "CREATE")]
    [InlineData("create table t as select * from film", "CREATE")]      // CTAS: data + schema write
    [InlineData("copy film from '/tmp/f.csv'", "COPY")]                 // bulk load
    [InlineData("call do_work()", "CALL")]
    [InlineData("do $$ begin delete from film; end $$", "DO")]          // procedural write
    [InlineData("grant select on film to bob", "GRANT")]
    [InlineData("revoke select on film from bob", "REVOKE")]
    [InlineData("refresh materialized view mv", "REFRESH")]
    [InlineData("select * into backup from film", "SELECT INTO")]       // table-creating SELECT
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

    [Fact]
    public void Plain_select_with_a_subquery_is_not_flagged_as_select_into()
    {
        // The only INTO is inside a subquery (not a top-level SELECT-INTO target) — must stay read-only.
        Assert.Empty(WriteGuard.FindRiskyStatements(
            "select * from film where film_id in (select film_id from inventory)"));
    }

    [Fact]
    public void Cte_feeding_a_select_into_is_flagged()
    {
        Assert.Equal(new[] { "SELECT INTO" },
            WriteGuard.FindRiskyStatements("with recent as (select * from rental) select * into snapshot from recent"));
    }

    // ---- Describe: the same scan, per statement, so a confirmation can show what is about to run ----

    [Fact]
    public void Describe_keeps_every_statement_in_execution_order_reads_included()
    {
        var described = WriteGuard.Describe("select 1;\ndelete from rental;\nselect 2;");

        Assert.Equal(new[] { "SELECT", "DELETE", "SELECT" }, described.Select(s => s.Verb));
        Assert.Equal(new[] { false, true, false }, described.Select(s => s.IsRisky));
        Assert.Equal(new[] { "select 1;", "delete from rental;", "select 2;" }, described.Select(s => s.Text));
    }

    [Fact]
    public void Describe_labels_a_read_by_its_verb_and_a_write_by_what_it_does()
    {
        var described = WriteGuard.Describe(
            "with removed as (delete from rental returning *) select * into snapshot from removed;");

        var only = Assert.Single(described);
        Assert.Equal("WITH", only.Verb);                                  // leading keyword
        Assert.Equal(new[] { "DELETE", "SELECT INTO" }, only.RiskyVerbs); // both writes hiding in it
        Assert.Equal("DELETE + SELECT INTO", only.Label);
    }

    [Fact]
    public void Describe_and_FindRiskyStatements_agree()
    {
        const string sql = "select 1; update film set title='x'; delete from rental; drop table t; update actor set x=1;";

        Assert.Equal(
            WriteGuard.FindRiskyStatements(sql),
            WriteGuard.Describe(sql).SelectMany(s => s.RiskyVerbs).Distinct().ToArray());
    }
}
