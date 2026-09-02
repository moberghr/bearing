using System.Linq;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The generated-text half of SQL Server support. Everything here is a string assertion — no server is
/// involved and none is reachable on this box, so these prove the <em>shape</em> of the SQL, not that a
/// server accepts it. Batch 5's integration tests are what would make that claim.
/// </summary>
public class SqlServerDialectTests
{
    private static readonly ISqlDialect Ss = SqlServerDialect.Instance;

    [Fact]
    public void Id_matches_the_provider_id()
        => Assert.Equal("sqlserver", Ss.Id);

    // ---- Paging: the top-level suffix ----

    [Fact]
    public void A_query_with_a_top_level_order_by_takes_an_offset_fetch_suffix()
    {
        var paged = Ss.TryAppendPage("select * from Orders order by OrderId", offset: 200, limit: 100);
        Assert.Equal("select * from Orders order by OrderId\noffset 200 rows fetch next 100 rows only", paged);
    }

    [Fact]
    public void The_first_page_still_spells_out_offset_zero_because_fetch_needs_one()
    {
        // Unlike Postgres, where `offset 0` is dropped: T-SQL's FETCH is only legal after an OFFSET.
        var paged = Ss.TryAppendPage("select * from Orders order by OrderId", offset: 0, limit: 100);
        Assert.Equal("select * from Orders order by OrderId\noffset 0 rows fetch next 100 rows only", paged);
    }

    [Fact]
    public void A_trailing_semicolon_is_stripped_before_the_suffix()
    {
        var paged = Ss.TryAppendPage("select * from Orders order by OrderId;  ", offset: 0, limit: 10);
        Assert.Equal("select * from Orders order by OrderId\noffset 0 rows fetch next 10 rows only", paged);
    }

    [Fact]
    public void A_query_without_a_top_level_order_by_refuses_the_suffix_rather_than_inventing_one()
    {
        // Synthesising an order would make paging silently non-deterministic: the same row could appear
        // on two pages or on none. So page 2 is refused and the caller wraps instead...
        Assert.Null(Ss.TryAppendPage("select * from Orders", offset: 100, limit: 100));
    }

    [Fact]
    public void The_first_page_of_an_unordered_query_is_capped_with_top()
    {
        // ...but the FIRST page still gets a server-side limit, via TOP, which needs no ORDER BY. Without
        // it the commonest query of all made the server produce the whole result set for the client to
        // read 100 rows of and discard — the exact waste a first-page limit exists to prevent, and a gap
        // Postgres never had because LIMIT carries no such restriction.
        Assert.Equal("select top (101) * from Orders", Ss.TryAppendPage("select * from Orders", 0, 101));
    }

    [Theory]
    // TOP goes after DISTINCT/ALL, which is the order T-SQL's grammar specifies.
    [InlineData("select distinct City from Customers", "select distinct top (50) City from Customers")]
    [InlineData("select all City from Customers", "select all top (50) City from Customers")]
    // The clause is spliced in with one space of its own; whatever spacing the user had after that point
    // is left exactly as they wrote it.
    [InlineData("select  *  from Orders", "select top (50)  *  from Orders")]
    [InlineData("select * from Orders;", "select top (50) * from Orders")]
    public void Top_is_placed_where_t_sql_wants_it(string sql, string expected)
        => Assert.Equal(expected, Ss.TryAppendPage(sql, 0, 50));

    [Theory]
    // Not a bare leading SELECT, so the insertion point cannot be located safely — refused, unchanged.
    [InlineData("with c as (select 1 x) select * from c")]
    [InlineData("select top 5 * from Orders")]        // already caps itself
    [InlineData("select * into Snapshot from Orders")] // a write
    [InlineData("delete from Orders")]
    public void Top_is_not_injected_where_it_would_be_wrong(string sql)
        => Assert.Null(Ss.TryAppendPage(sql, 0, 50));

    [Theory]
    [InlineData("select * from (select * from Orders order by OrderId offset 0 rows) x")] // order is nested
    [InlineData("select row_number() over (order by OrderId) rn from Orders")]            // order is in OVER
    public void An_order_by_that_is_not_top_level_does_not_qualify(string sql)
        => Assert.Null(Ss.TryAppendPage(sql, offset: 100, limit: 100));

    [Theory]
    [InlineData("select top 10 * from Orders order by OrderId")]              // TOP and OFFSET are exclusive
    [InlineData("select * from Orders order by OrderId offset 5 rows")]       // already pages itself
    [InlineData("select * from Orders order by OrderId offset 5 rows fetch next 5 rows only")]
    [InlineData("select * from Orders order by OrderId for json auto")]       // FOR must come last
    [InlineData("select * from Orders order by OrderId option (recompile)")]  // OPTION must come last
    [InlineData("select * into Snapshot from Orders order by OrderId")]       // SELECT INTO is a write
    public void A_clause_that_must_stay_last_blocks_the_suffix(string sql)
        => Assert.Null(Ss.TryAppendPage(sql, offset: 100, limit: 100));

    [Theory]
    [InlineData("select 1 order by 1; select 2 order by 1")]                  // a batch
    [InlineData("update Orders set Total = 1")]                               // a write
    [InlineData("delete from Orders")]
    [InlineData("exec dbo.Rebuild")]                                          // T-SQL risky verb
    public void Only_a_lone_row_returning_read_can_be_suffixed(string sql)
        => Assert.Null(Ss.TryAppendPage(sql, offset: 100, limit: 100));

    [Fact]
    public void Nonsense_paging_arguments_are_refused()
    {
        const string sql = "select * from Orders order by OrderId";
        Assert.Null(Ss.TryAppendPage(sql, offset: -1, limit: 100));
        Assert.Null(Ss.TryAppendPage(sql, offset: 0, limit: 0));
        Assert.Null(Ss.TryAppendPage("   ", offset: 0, limit: 100));
    }

    // ---- Paging: the derived-table wrap ----

    [Fact]
    public void The_wrap_orders_the_outer_query_by_a_constant_so_offset_fetch_is_legal()
    {
        var wrapped = Ss.Wrap("select * from Orders;", offset: 50, limit: 25);
        Assert.Equal(
            "select * from (\nselect * from Orders\n) as _sq order by (select null) offset 50 rows fetch next 25 rows only",
            wrapped);
    }

    [Fact]
    public void An_inner_order_by_is_made_legal_with_offset_zero_rows()
    {
        // T-SQL rejects ORDER BY in a derived table unless the subquery also has TOP/OFFSET/FOR XML.
        var wrapped = Ss.Wrap("select * from Orders order by OrderId", offset: 0, limit: 10);
        Assert.Equal(
            "select * from (\nselect * from Orders order by OrderId\noffset 0 rows\n) as _sq"
            + " order by (select null) offset 0 rows fetch next 10 rows only",
            wrapped);
    }

    [Theory]
    [InlineData("select top 5 * from Orders order by OrderId")]
    [InlineData("select * from Orders order by OrderId offset 5 rows")]
    public void An_inner_order_by_that_is_already_legal_is_not_repaired_twice(string sql)
    {
        Assert.Equal($"select count(*) from (\n{sql}\n) as _sq", Ss.CountWrap(sql));
    }

    // ---- Shapes that cannot be a derived table at all ----
    // These used to be wrapped and handed to the server, which rejected them: the first page succeeded,
    // then load-more died on a syntax error and [Count] silently reported no total (Msg 156/1033 are in
    // the executor's uncountable-shape set). Refusing is the honest answer — the caller retires paging and
    // says so. `for xml` was previously asserted as merely "not repaired twice", which encoded the wrong
    // behaviour: FOR XML legalises an ORDER BY *in place*, but the clause itself is illegal in a subquery.

    [Theory]
    [InlineData("with c as (select 1 as x) select * from c")]               // a CTE must lead a statement
    [InlineData("with c as (select 1 as x) select * from c order by x")]
    [InlineData("select * from Orders order by OrderId for xml auto")]      // FOR XML yields a stream
    [InlineData("select * from Orders for json path")]
    [InlineData("select * from Orders order by OrderId option (recompile)")] // hints are statement-level
    public void A_shape_that_cannot_sit_in_a_derived_table_is_refused_not_wrapped(string sql)
    {
        Assert.Null(Ss.Wrap(sql, offset: 0, limit: 10));
        Assert.Null(Ss.CountWrap(sql));
    }

    [Fact]
    public void A_cte_with_no_order_by_cannot_be_paged_at_all()
        // No ORDER BY, so no suffix; and a CTE cannot be a derived table, so no wrap. This is the shape
        // that makes the caller retire paging rather than retry a doomed page on every scroll.
        => Assert.Null(PageSql.Page(Ss, "with c as (select 1 as x) select * from c", offset: 0, limit: 10));

    [Fact]
    public void A_cte_that_orders_itself_still_pages_through_the_suffix()
    {
        // The refusal is about the WRAP, not about CTEs as such: `with … select … order by x offset …
        // fetch next …` is perfectly legal, because the CTE stays at statement level and the page clause
        // rides the ORDER BY. Refusing to page this would have been a needless regression.
        const string sql = "with c as (select 1 as x) select * from c order by x";
        Assert.Equal(
            sql + "\noffset 0 rows fetch next 10 rows only",
            PageSql.Page(Ss, sql, offset: 0, limit: 10));
    }

    [Fact]
    public void Postgres_wraps_every_one_of_those_shapes_without_complaint()
    {
        // The refusal is the T-SQL dialect's alone — Postgres accepts a CTE, a locking clause and the rest
        // inside a subquery, so its wrap must never start returning null.
        var pg = PostgresDialect.Instance;
        Assert.NotNull(pg.Wrap("with c as (select 1 as x) select * from c", 0, 10));
        Assert.NotNull(pg.CountWrap("with c as (select 1 as x) select * from c"));
        Assert.NotNull(PageSql.Page(pg, "with c as (select 1 as x) select * from c", 0, 10));
    }

    [Fact]
    public void An_inner_query_with_no_order_by_is_wrapped_untouched()
        => Assert.Equal(
            "select count(*) from (\nselect * from Orders\n) as _sq",
            Ss.CountWrap("select * from Orders;"));

    [Fact]
    public void The_count_wrap_survives_an_inner_order_by()
        => Assert.Equal(
            "select count(*) from (\nselect * from Orders order by OrderId\noffset 0 rows\n) as _sq",
            Ss.CountWrap("select * from Orders order by OrderId"));

    // ---- PageSql composition ----

    [Fact]
    public void Page_prefers_the_suffix_and_falls_back_to_the_wrap()
    {
        Assert.Equal(
            "select * from Orders order by OrderId\noffset 20 rows fetch next 10 rows only",
            PageSql.Page(Ss, "select * from Orders order by OrderId", offset: 20, limit: 10));

        Assert.StartsWith("select * from (", PageSql.Page(Ss, "select * from Orders", offset: 20, limit: 10));
    }

    // ---- DML ----

    // ---- Insert, both forms ----

    [Fact]
    public void Insert_also_offers_a_form_with_no_output_clause()
    {
        // SQL Server refuses OUTPUT without INTO on a table with an enabled trigger (Msg 334), which is an
        // ordinary shape for an audited table. The generator emits both forms so the executor can retry;
        // without this an insert was impossible there, not merely unable to refill the row.
        Assert.Equal(
            "insert into [dbo].[T] ([a]) output inserted.* values (@p0)",
            Ss.InsertStatement("[dbo].[T]", "[a]", "@p0", withReturning: true));
        Assert.Equal(
            "insert into [dbo].[T] ([a]) values (@p0)",
            Ss.InsertStatement("[dbo].[T]", "[a]", "@p0", withReturning: false));
    }

    [Fact]
    public void The_generator_carries_both_forms_on_the_command()
    {
        var cmd = DmlGenerator.Insert(Ss, "dbo", "T", new[] { new ColumnValue("a", 1) });
        Assert.Contains("output inserted.*", cmd.Sql);
        Assert.NotNull(cmd.SqlWithoutReturning);
        Assert.DoesNotContain("output", cmd.SqlWithoutReturning!);
        // Same parameters either way — that is what lets the retry reuse the list verbatim.
        Assert.Equal(new[] { "@p0" }, cmd.Parameters.Select(p => p.Name));
    }

    [Fact]
    public void Postgres_carries_both_forms_too_even_though_it_never_needs_the_second()
    {
        var cmd = DmlGenerator.Insert(PostgresDialect.Instance, "public", "t", new[] { new ColumnValue("a", 1) });
        Assert.EndsWith("returning *", cmd.Sql);
        Assert.Equal("insert into \"public\".\"t\" (\"a\") values (@p0)", cmd.SqlWithoutReturning);
    }

    // ---- Bracketed identifiers ----
    // The PostgreSQL lexer has no notion of a T-SQL [delimited name]: it emits the words inside as
    // ordinary tokens. Those words reached the depth-0 list, so a table called [Order Details] read as a
    // top-level ORDER BY and the paging gates — which are POSITIVE gates on that — appended OFFSET/FETCH
    // to a query that had none. SQL Server rejects it (Msg 102), so the user's first page died on a syntax
    // error they never typed. Every fixture below failed before TopLevelTokens learned to skip the span.

    [Theory]
    [InlineData("select * from [Order Details]")]
    [InlineData("select [Order Date], Total from dbo.Sales")]
    [InlineData("select * from dbo.[Order]")]
    [InlineData("select o.Id from dbo.[Order Lines] o")]
    [InlineData("select [order], [group], [by] from dbo.[Order By]")]
    public void A_bracketed_name_containing_a_clause_keyword_is_not_an_order_by(string sql)
    {
        // The bug: these read as having a top-level ORDER BY, so OFFSET/FETCH was appended to a statement
        // that had none and SQL Server rejected it (Msg 102). What must never appear is the page clause.
        var paged = Ss.TryAppendPage(sql, 0, 100);
        Assert.NotNull(paged);
        Assert.DoesNotContain("offset", paged!, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fetch next", paged!, System.StringComparison.OrdinalIgnoreCase);
        // With no ORDER BY the limit is TOP instead, which needs none.
        Assert.Contains("top (100)", paged!);
        // ...and the derived-table repair must not inject an OFFSET the inner query has no ORDER BY for.
        Assert.DoesNotContain("offset 0 rows", Ss.CountWrap(sql));
    }

    [Fact]
    public void A_bracketed_name_does_not_hide_a_real_order_by_either()
    {
        // The skip must not swallow the clause that follows the bracketed name.
        var sql = "select [Order Date] from dbo.[Order Details] order by [Order Date]";
        Assert.Equal(
            sql + "\noffset 0 rows fetch next 50 rows only",
            Ss.TryAppendPage(sql, 0, 50));
        Assert.Contains("offset 0 rows", Ss.CountWrap(sql));
    }

    [Fact]
    public void A_bare_order_token_without_by_is_not_the_clause()
    {
        // ORDER BY is the two words adjacent; an `order` token on its own is not a gate — so this takes
        // the TOP form, not the OFFSET/FETCH one that would be a syntax error here.
        var paged = Ss.TryAppendPage("select order from dbo.T", 0, 10);
        Assert.Equal("select top (10) order from dbo.T", paged);
        Assert.Contains(
            "offset 0 rows",
            Ss.CountWrap("select x from dbo.T order  by  x")); // odd spacing is still the clause
    }

    [Fact]
    public void Insert_puts_output_before_values_because_a_trailing_output_is_a_syntax_error()
    {
        var cmd = DmlGenerator.Insert(Ss, "dbo", "Orders", new[] { new ColumnValue("Total", 12) });

        Assert.Equal(
            "insert into [dbo].[Orders] ([Total]) output inserted.* values (@p0)", cmd.Sql);
        Assert.Equal(12, Assert.Single(cmd.Parameters).Value);
        Assert.DoesNotContain("returning", cmd.Sql);
        Assert.True(cmd.Sql.IndexOf("output inserted.*", StringComparison.Ordinal)
                    < cmd.Sql.IndexOf("values", StringComparison.Ordinal));
    }

    [Fact]
    public void Update_and_delete_bracket_their_identifiers_and_keep_the_at_p_parameters()
    {
        var update = DmlGenerator.Update(Ss, "dbo", "Orders",
            assignments: new[] { new ColumnValue("Total", 5) },
            keys: new[] { new ColumnValue("OrderId", 1) });
        Assert.Equal("update [dbo].[Orders] set [Total] = @p0 where [OrderId] = @p1", update.Sql);

        var delete = DmlGenerator.Delete(Ss, null, "Ord]ers", new[] { new ColumnValue("OrderId", null) });
        Assert.Equal("delete from [Ord]]ers] where [OrderId] is null", delete.Sql);
        Assert.Empty(delete.Parameters);
    }

    // ---- Identifiers, through the dialect ----

    [Fact]
    public void The_dialect_serves_the_bracket_rules()
    {
        Assert.Equal("[Customers]", Ss.Quote("Customers"));
        Assert.Equal("Customers", Ss.QuoteIfNeeded("Customers"));   // NOT bracketed: case is preserved
        Assert.False(Ss.NeedsQuoting("Customers"));
        Assert.True(Ss.NeedsQuoting("select"));
        Assert.Equal("Customers", Ss.Unquote("[Customers]"));
    }
}
