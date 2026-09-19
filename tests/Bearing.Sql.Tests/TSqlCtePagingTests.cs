using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Paging a CTE query on SQL Server. A CTE cannot sit inside a derived table (Msg 156), so before this
/// the dialect refused the wrap and a CTE query ran unbounded: the whole result set streamed once, with
/// paging retired and no total. With the grammar in hand the CTE list can stay at statement level while
/// only the body goes in the derived table — a statement-level CTE is in scope throughout the statement,
/// subqueries included.
/// <para>
/// Half of these tests are refusals, and that is the point rather than an omission: a mis-placed cut
/// produces SQL the server accepts and answers <em>wrongly</em>, which is worse than a query that cannot
/// be paged. The boundary comes from the parse tree or it does not come at all.
/// </para>
/// </summary>
public class TSqlCtePagingTests
{
    private static readonly ISqlDialect Ss = SqlServerDialect.Instance;

    // ---- The split ----------------------------------------------------------------------------

    [Fact]
    public void The_boundary_lands_between_the_cte_list_and_the_body()
    {
        var split = TSqlCteSplitter.Split("with c as (select 1 as x) select * from c");

        Assert.NotNull(split);
        Assert.Equal("with c as (select 1 as x)", split.With);
        Assert.Equal("select * from c", split.Body);
    }

    [Fact]
    public void Several_ctes_all_stay_in_the_preamble()
    {
        // The shape a keyword scan cannot do: `select` appears three times before the outer one, and the
        // second CTE's body is itself a full select over the first.
        var split = TSqlCteSplitter.Split(
            "with a as (select 1 as x), b as (select x from a) select * from b order by x");

        Assert.NotNull(split);
        Assert.Equal("with a as (select 1 as x), b as (select x from a)", split.With);
        Assert.Equal("select * from b order by x", split.Body);
    }

    [Fact]
    public void A_cte_named_with_a_bracketed_keyword_and_a_column_list_still_splits()
    {
        var split = TSqlCteSplitter.Split(
            "with [Order] (OrderId) as (select OrderId from dbo.Orders) select * from [Order]");

        Assert.NotNull(split);
        Assert.Equal(
            "with [Order] (OrderId) as (select OrderId from dbo.Orders)",
            split.With);
        Assert.Equal("select * from [Order]", split.Body);
    }

    [Fact]
    public void A_comment_between_the_preamble_and_the_body_goes_with_the_preamble()
        // Slicing at the body's first token rather than the preamble's last means nothing between them
        // is dropped or duplicated, whatever it is.
        => Assert.Equal(
            "with c as (select 1 as x)\n-- now read it",
            TSqlCteSplitter.Split("with c as (select 1 as x)\n-- now read it\nselect * from c")!.With);

    [Theory]
    [InlineData("select * from Orders")]                                  // no CTE at all
    [InlineData("with c as (select 1 as x) select * from c; select 2")]   // two statements
    [InlineData("with c as (select 1 as x) select * from c\nGO")]         // a batch separator
    [InlineData("with c as (select 1 as x select * from c")]              // a syntax error
    [InlineData("with c as (select 1 as x) select * from c junk junk")]   // does not reach EOF
    [InlineData("with c as (select 1 as x) insert into T select * from c")] // a CTE feeding a write
    [InlineData("with c as (select 1 as x) delete from c")]
    [InlineData("with c as (select 1 as x) update c set x = 2")]
    public void Anything_the_grammar_will_not_vouch_for_is_refused(string sql)
        => Assert.Null(TSqlCteSplitter.Split(sql));

    // ---- The hoist ----------------------------------------------------------------------------

    [Fact]
    public void The_cte_list_is_hoisted_over_the_derived_table()
        => Assert.Equal(
            "with c as (select 1 as x)\nselect * from (\nselect * from c\n) as _sq"
            + " order by (select null) offset 50 rows fetch next 25 rows only",
            Ss.Wrap("with c as (select 1 as x) select * from c;", offset: 50, limit: 25));

    [Fact]
    public void The_count_wrap_hoists_it_too()
        => Assert.Equal(
            "with c as (select 1 as x)\nselect count(*) from (\nselect * from c\n) as _sq",
            Ss.CountWrap("with c as (select 1 as x) select * from c"));

    [Fact]
    public void A_bodys_own_order_by_is_still_repaired_after_the_hoist()
        // The body is a derived table like any other, so T-SQL's Msg 1033 rule applies to it unchanged.
        => Assert.Equal(
            "with c as (select 1 as x)\nselect count(*) from (\nselect * from c order by x\noffset 0 rows\n) as _sq",
            Ss.CountWrap("with c as (select 1 as x) select * from c order by x"));

    [Fact]
    public void A_cte_with_no_order_by_can_now_be_paged()
        // This is the whole user-visible change: the shape that used to make the caller retire paging.
        => Assert.Equal(
            "with c as (select 1 as x)\nselect * from (\nselect * from c\n) as _sq"
            + " order by (select null) offset 0 rows fetch next 10 rows only",
            PageSql.Page(Ss, "with c as (select 1 as x) select * from c", offset: 0, limit: 10));

    [Fact]
    public void A_cte_that_orders_itself_still_prefers_the_suffix_over_the_hoist()
    {
        // Unchanged, and still the better answer: the CTE stays where it is, the page clause rides the
        // query's own ORDER BY, and the page boundaries are the user's order rather than the plan's.
        const string sql = "with c as (select 1 as x) select * from c order by x";
        Assert.Equal(
            sql + "\noffset 0 rows fetch next 10 rows only",
            PageSql.Page(Ss, sql, offset: 0, limit: 10));
    }

    [Theory]
    [InlineData("with c as (select 1 as x) select * from c option (recompile)")]
    [InlineData("with c as (select 1 as x) select * from c for json path")]
    public void A_body_that_cannot_be_a_derived_table_is_refused_even_though_the_cte_could_be_hoisted(
        string sql)
    {
        // The preamble moving out does not make the body legal: OPTION and FOR are clauses *of* the
        // query being wrapped, so they are re-checked against the body rather than assumed away.
        Assert.Null(Ss.Wrap(sql, offset: 0, limit: 10));
        Assert.Null(Ss.CountWrap(sql));
    }
}
