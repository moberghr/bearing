using System;
using System.Diagnostics;
using System.Linq;
using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The formatter on input that is large, deep or wide. <see cref="SqlFormatCorpus"/> is broad but every
/// entry in it is small, so none of it says anything about a real script — and the two things that only
/// break at scale are the two worst failure modes: a parser that overflows the stack, and a format that
/// takes long enough to look like a hang.
/// <para>
/// Timings are asserted only against deliberately loose ceilings. The point is to catch a change that makes
/// something superlinear, not to pin a number that will flake on a busy machine.
/// </para>
/// </summary>
public class SqlFormatScaleTests
{
    /// <summary>A realistic analytical query — CTE chain, joins, windows, CASE, group/having/order.</summary>
    private const string Analytical = """
        with monthly as (
            select date_trunc('month', o.created_at) as m, o.customer_id, sum(o.total) as revenue,
                   count(*) as orders
            from orders o join customers c on c.id = o.customer_id
            left join addresses a on a.customer_id = c.id and a.is_primary
            where o.status in ('paid', 'shipped') and o.created_at >= now() - interval '2 years'
              and c.active and coalesce(a.country, 'XX') <> 'ZZ'
            group by 1, 2
        ), ranked as (
            select m, customer_id, revenue, orders,
                   row_number() over (partition by m order by revenue desc) as rn,
                   sum(revenue) over (partition by m) as month_total,
                   case when revenue > 10000 then 'whale' when revenue > 1000 then 'mid' else 'small' end as tier
            from monthly
        ), top as (
            select * from ranked where rn <= 10
        )
        select t.m, t.customer_id, c.name, t.revenue, t.orders, t.tier,
               round(100.0 * t.revenue / nullif(t.month_total, 0), 2) as pct
        from top t join customers c on c.id = t.customer_id
        where t.revenue between 100 and 1000000 and t.tier <> 'small'
        order by t.m desc, t.revenue desc
        limit 100
        """;

    private static string FormatWithin(string sql, int millis, string what)
    {
        var sw = Stopwatch.StartNew();
        var result = SqlFormat.Format(sql);
        sw.Stop();

        Assert.False(result.Refused, $"{what}: {result.Refusal}");
        Assert.True(sw.ElapsedMilliseconds < millis,
            $"{what} took {sw.ElapsedMilliseconds}ms, over the {millis}ms ceiling");

        // Whatever the size, the invariants still hold.
        var again = SqlFormat.Format(result.Text);
        Assert.False(again.Refused, $"{what} (second pass): {again.Refusal}");
        Assert.Equal(result.Text, again.Text);
        return result.Text;
    }

    [Fact]
    public void A_real_analytical_query_formats_and_keeps_its_structure()
    {
        var formatted = FormatWithin(Analytical, 5_000, "analytical query");

        // Spot-check the shape rather than pinning every line: the layout rules have their own tests, and a
        // golden copy of a query this size would be re-pasted rather than read on the next change.
        Assert.Contains("WITH monthly AS (", formatted, StringComparison.Ordinal);
        Assert.Contains("\n),\nranked AS (", formatted, StringComparison.Ordinal);
        Assert.Contains("    JOIN customers c ON c.id = o.customer_id", formatted, StringComparison.Ordinal);
        Assert.Contains("    LEFT JOIN addresses a ON", formatted, StringComparison.Ordinal);
        // BETWEEN keeps its AND even buried in a big query.
        Assert.Contains("BETWEEN 100 AND 1000000", formatted, StringComparison.Ordinal);
        Assert.Contains("CASE\n", formatted, StringComparison.Ordinal);
        Assert.Contains("ORDER BY t.m DESC, t.revenue DESC", formatted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    public void A_long_script_of_real_queries_formats(int statements)
    {
        var script = string.Join(";\n", Enumerable.Repeat(Analytical, statements)) + ";";
        var formatted = FormatWithin(script, 20_000, $"{statements}-statement script");
        Assert.Equal(statements, formatted.Split("WITH monthly AS (").Length - 1);
    }

    [Fact]
    public void A_thousand_small_statements_format() =>
        FormatWithin(string.Join(";\n", Enumerable.Range(0, 1000).Select(i => $"select {i} from t{i}")),
            10_000, "1000-statement batch");

    [Theory]
    [InlineData(200)]
    [InlineData(2000)]
    public void A_very_wide_select_list_formats(int columns)
    {
        var sql = "select " + string.Join(", ", Enumerable.Range(0, columns).Select(i => $"c{i}")) + " from t";
        var formatted = FormatWithin(sql, 10_000, $"{columns}-column select list");
        Assert.Equal(columns, formatted.Split(",\n").Length);   // one per line
    }

    [Fact]
    public void A_five_thousand_element_in_list_formats() =>
        FormatWithin("select a from t where b in (" + string.Join(", ", Enumerable.Range(0, 5000)) + ")",
            10_000, "5000-element IN list");

    [Fact]
    public void A_five_hundred_term_and_chain_formats()
    {
        var sql = "select a from t where "
                  + string.Join(" and ", Enumerable.Range(0, 500).Select(i => $"c{i} = {i}"));
        var formatted = FormatWithin(sql, 10_000, "500-term AND chain");
        Assert.Equal(499, formatted.Split("\n    AND ").Length - 1);
    }

    [Fact]
    public void A_large_unrecognised_statement_comes_back_byte_for_byte()
    {
        var ddl = "create table big (\n"
                  + string.Join(",\n", Enumerable.Range(0, 500).Select(i => $"    col{i} text"))
                  + "\n)";
        var result = SqlFormat.Format(ddl);
        Assert.False(result.Refused);
        Assert.False(result.Changed);
        Assert.Equal(ddl, result.Text);
    }

    // ---- the stack ---------------------------------------------------------------------------------

    /// <summary>Nesting well past what a default 1 MB stack survives (~150) still formats, because the
    /// parse runs on <c>PgParsing.OnDeepStack</c>. The guard must never decline SQL the parser handles.</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(200)]
    [InlineData(400)]
    public void Nesting_up_to_the_limit_still_formats(int depth)
    {
        var sql = "select * from " + string.Concat(Enumerable.Repeat("(select x from ", depth))
                  + "t" + string.Concat(Enumerable.Range(0, depth).Select(i => $") a{i}"));
        var result = SqlFormat.Format(sql);
        Assert.False(result.Refused, result.Refusal);
    }

    /// <summary>
    /// Past the limit the formatter declines instead of parsing. Not politeness, and not a size limit: even
    /// on the deep stack this grammar overflows somewhere past 6400 levels, and a
    /// <see cref="StackOverflowException"/> cannot be caught in .NET — the process dies and takes the
    /// user's unsaved buffer with it. There is no "handle it anyway" to prefer here; the choice is between
    /// declining and crashing. If this test ever crashes the run rather than passing, that is the bug it
    /// exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(PgParsing.MaxNestingDepth + 1)]
    [InlineData(5000)]
    [InlineData(50000)]
    public void Nesting_past_the_limit_is_declined_rather_than_crashing_the_process(int depth)
    {
        var sql = $"select {new string('(', depth)}1{new string(')', depth)} from t";

        var result = SqlFormat.Format(sql);

        Assert.True(result.Refused);
        Assert.Contains("nests more than", result.Refusal);
        Assert.Equal(sql, result.Text);
    }

    [Fact]
    public void The_depth_guard_counts_brackets_and_case_and_never_goes_negative()
    {
        Assert.Equal(0, PgParsing.NestingDepth(PgParsing.LexAll("select a from t")));
        Assert.Equal(1, PgParsing.NestingDepth(PgParsing.LexAll("select (a) from t")));
        Assert.Equal(3, PgParsing.NestingDepth(PgParsing.LexAll("select ((( a ))) from t")));
        Assert.Equal(2, PgParsing.NestingDepth(PgParsing.LexAll("select x[(1)] from t")));
        Assert.Equal(1, PgParsing.NestingDepth(PgParsing.LexAll("select case when a then 1 end from t")));

        // Unbalanced text is normal mid-edit and must not make the counter drift below zero, or a later
        // run of open brackets would be under-counted and slip past the guard.
        Assert.Equal(1, PgParsing.NestingDepth(PgParsing.LexAll(")))) select (a")));
    }
}
