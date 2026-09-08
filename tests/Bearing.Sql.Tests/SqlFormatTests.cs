using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The formatter's layout, stated as exact output. These are the readable half of the suite — what a person
/// checks when they disagree with a break. The half that actually guards production is
/// <see cref="SqlFormatSafetyTests"/>, which asserts that whatever the layout does, it is still the same SQL.
/// </summary>
public class SqlFormatTests
{
    private static string Format(string sql)
    {
        var result = SqlFormat.Format(sql);
        Assert.Null(result.Refusal);
        return result.Text;
    }

    [Fact]
    public void A_select_puts_one_target_per_line_and_each_clause_on_its_own()
        => Assert.Equal(
            """
            SELECT
                id,
                name
            FROM users
            WHERE id = 1
            """,
            Format("select id, name from users where id = 1"));

    [Fact]
    public void A_single_table_stays_on_the_from_line_but_joins_do_not()
        => Assert.Equal(
            """
            SELECT
                o.id,
                c.name
            FROM orders o
                JOIN customers c ON c.id = o.customer_id
                LEFT JOIN addresses a ON a.customer_id = c.id
            WHERE o.total > 100
                AND c.active
            ORDER BY o.id DESC
            LIMIT 10
            """,
            Format("select o.id, c.name from orders o join customers c on c.id = o.customer_id "
                   + "left join addresses a on a.customer_id = c.id "
                   + "where o.total > 100 and c.active order by o.id desc limit 10"));

    /// <summary>The first join of a chain must break like the rest — it did not, for a while, because the
    /// left-hand side of a chain is not a nested table_ref (see <c>SqlFormatLayout.VisitTable_ref</c>).</summary>
    [Fact]
    public void The_first_join_breaks_too()
        => Assert.Equal(
            """
            SELECT
                *
            FROM a
                JOIN b ON b.id = a.id
            """,
            Format("select * from a join b on b.id = a.id"));

    [Fact]
    public void A_comma_separated_from_list_breaks_one_per_line()
        => Assert.Equal(
            """
            SELECT
                *
            FROM
                a,
                b
            """,
            Format("select * from a, b"));

    [Fact]
    public void Cte_bodies_are_indented_and_each_cte_starts_a_line()
        => Assert.Equal(
            """
            WITH recent AS (
                SELECT
                    id
                FROM orders
                WHERE created_at > now()
            ),
            total AS (
                SELECT
                    count(*) c
                FROM recent
            )
            SELECT
                *
            FROM recent
                JOIN total ON TRUE
            """,
            Format("with recent as (select id from orders where created_at > now()), "
                   + "total as (select count(*) c from recent) "
                   + "select * from recent join total on true"));

    [Fact]
    public void A_subquery_indents_its_body_and_brings_the_paren_back_out()
        => Assert.Equal(
            """
            SELECT
                *
            FROM (
                SELECT
                    id
                FROM users
                WHERE active
            ) u
            WHERE u.id > 5
            """,
            Format("select * from (select id, name from users where active) u where u.id > 5"
                .Replace("id, name", "id")));

    [Fact]
    public void Set_operations_start_a_line_at_the_query_level()
        => Assert.Equal(
            """
            SELECT
                a
            FROM t1
            UNION ALL
            SELECT
                a
            FROM t2
            ORDER BY a
            """,
            Format("select a from t1 union all select a from t2 order by a"));

    [Fact]
    public void Insert_breaks_at_values_conflict_and_returning()
        => Assert.Equal(
            """
            INSERT INTO t (a, b)
            VALUES (1, 2)
            ON CONFLICT (a) DO UPDATE SET b = excluded.b
            RETURNING *
            """,
            Format("insert into t (a, b) values (1, 2) on conflict (a) do update set b = excluded.b returning *"));

    [Fact]
    public void Update_breaks_its_assignments_when_there_is_more_than_one()
        => Assert.Equal(
            """
            UPDATE t
            SET
                a = 1,
                b = 2
            WHERE id = 3
            RETURNING id
            """,
            Format("update t set a = 1, b = 2 where id = 3 returning id"));

    [Fact]
    public void A_single_assignment_rides_on_the_set_line()
        => Assert.Equal(
            """
            UPDATE t
            SET a = 1
            WHERE id = 3
            """,
            Format("update t set a = 1 where id = 3"));

    [Fact]
    public void Delete_keeps_its_target_inline_and_breaks_the_rest()
        => Assert.Equal(
            """
            DELETE FROM t
            WHERE id = 1
            RETURNING *
            """,
            Format("delete from t where id = 1 returning *"));

    /// <summary>FILTER's own WHERE is inside brackets, so it must not be pulled onto a line of its own the
    /// way the statement's WHERE is.</summary>
    [Fact]
    public void A_filtered_aggregate_stays_on_one_line()
        => Assert.Equal(
            """
            SELECT
                count(*) FILTER (WHERE status = 'x') AS n,
                sum(amt)
            FROM t
            GROUP BY a
            HAVING count(*) > 1
            """,
            Format("select count(*) filter (where status = 'x') as n, sum(amt) from t group by a having count(*) > 1"));

    /// <summary>
    /// BETWEEN's AND is part of the operator, not a conjunction. Breaking there splits one comparison over
    /// two lines and reads as though a term went missing — and it is easy to get wrong, because every other
    /// top-level AND in a WHERE does break. Found by reading the sql-formatter family's suite, which pins
    /// the same case.
    /// </summary>
    [Fact]
    public void Between_keeps_its_own_and_but_the_real_conjunction_still_breaks()
        => Assert.Equal(
            """
            SELECT
                a
            FROM t
            WHERE b BETWEEN 1 AND 2
                AND c = 3
            """,
            Format("select a from t where b between 1 and 2 and c = 3"));

    [Fact]
    public void Not_between_and_between_symmetric_keep_theirs_too()
    {
        Assert.Contains("WHERE b NOT BETWEEN 1 AND 2", Format("select a from t where b not between 1 and 2"));
        Assert.Contains("WHERE b BETWEEN SYMMETRIC 1 AND 2",
            Format("select a from t where b between symmetric 1 and 2"));
    }

    /// <summary>A long CASE on one line is among the least readable things SQL produces. Its indent comes
    /// from where the expression sits, not from the query nesting, so it works in a select list as well as
    /// in a predicate.</summary>
    [Fact]
    public void Case_puts_each_when_on_its_own_line()
        => Assert.Equal(
            """
            SELECT
                CASE
                    WHEN a = 1 THEN 'x'
                    WHEN a = 2 THEN 'y'
                    ELSE 'z'
                END AS v
            FROM t
            """,
            Format("select case when a = 1 then 'x' when a = 2 then 'y' else 'z' end as v from t"));

    /// <summary>
    /// Postgres has ~400 keywords and plenty of the non-reserved ones are ordinary names, so uppercasing on
    /// the word alone would turn <c>select value from t</c> into <c>select VALUE from t</c>. The grammar
    /// draws the line for us: a keyword reached through <c>colid</c> is in an identifier position.
    /// </summary>
    [Theory]
    [InlineData("select value from t", "value")]
    [InlineData("select name, type, source from t", "    name,\n    type,\n    source")]
    [InlineData("select left(a, 1) from t", "left(a, 1)")]
    [InlineData("select a as value from t", "a AS value")]
    public void A_keyword_standing_in_for_an_identifier_keeps_the_users_case(string sql, string expected)
        => Assert.Contains(expected, Format(sql));

    /// <summary>The same word in a real keyword position still uppercases — which is what makes the
    /// distinction worth drawing rather than just never touching these words.</summary>
    [Fact]
    public void The_same_word_used_as_a_keyword_is_still_uppercased()
    {
        Assert.Contains("SET name = 'x'", Format("update t set name = 'x'"));
        Assert.Contains("VALUES (1)", Format("insert into t (value) values (1)"));
        Assert.Contains("(value)", Format("insert into t (value) values (1)"));
    }

    [Fact]
    public void Statements_in_a_batch_are_separated_by_a_blank_line()
        => Assert.Equal(
            """
            SELECT
                1;

            SELECT
                2
            """,
            Format("select 1; select 2"));

    // ---- what it refuses to touch ------------------------------------------------------------------

    [Theory]
    [InlineData("create table foo (\n    id int primary key,\n    name text\n)")]
    [InlineData("create function f() returns int as $$ begin return 1; end $$ language plpgsql")]
    [InlineData("explain analyze select a from t")]
    [InlineData("alter table t\n    add column b int")]
    public void A_statement_shape_with_no_layout_rules_is_returned_byte_for_byte(string sql)
    {
        var result = SqlFormat.Format(sql);
        Assert.False(result.Refused);
        Assert.False(result.Changed);
        Assert.Equal(sql, result.Text);
    }

    /// <summary>An unrecognised statement beside a recognised one must not drag the recognised one down, nor
    /// be dragged up by it.</summary>
    [Fact]
    public void A_batch_formats_what_it_understands_and_leaves_the_rest()
        => Assert.Equal(
            """
            create index i on t (a);

            SELECT
                a
            FROM t
            """,
            Format("create index i on t (a);\nselect a from t"));

    [Theory]
    [InlineData("select a from")]
    [InlineData("select from where")]
    [InlineData("this is not sql at all")]
    public void Sql_that_does_not_parse_is_refused_and_left_alone(string sql)
    {
        var result = SqlFormat.Format(sql);
        Assert.True(result.Refused);
        Assert.Contains("could not be parsed", result.Refusal);
        Assert.Equal(sql, result.Text);       // the caller can put Text back unconditionally
        Assert.False(result.Changed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void Blank_input_is_a_no_op_rather_than_a_refusal(string sql)
    {
        var result = SqlFormat.Format(sql);
        Assert.False(result.Refused);
        Assert.False(result.Changed);
        Assert.Equal(sql, result.Text);
    }

    [Fact]
    public void Formatting_already_formatted_sql_reports_no_change()
    {
        var once = Format("select id from t");
        var twice = SqlFormat.Format(once);
        Assert.False(twice.Changed);
        Assert.Equal(once, twice.Text);
    }

    // ---- comments ----------------------------------------------------------------------------------

    [Fact]
    public void A_comment_on_its_own_line_keeps_one_at_the_indent_of_what_follows()
        => Assert.Equal(
            """
            SELECT
                -- which one
                a
            FROM t
            """,
            Format("select\n-- which one\na from t"));

    [Fact]
    public void A_trailing_comment_stays_trailing()
        => Assert.Equal(
            """
            SELECT
                a
            FROM t -- only this table
            WHERE a = 1
            """,
            Format("select a from t -- only this table\nwhere a = 1"));

    /// <summary>A line comment swallows the rest of its line, so whatever the layout wanted next has to
    /// start on a new one. This is the single way the writer could change what the SQL means.</summary>
    [Fact]
    public void Code_after_a_line_comment_is_never_pulled_up_onto_its_line()
    {
        var formatted = Format("select a, -- note\nb from t");
        Assert.Contains("-- note\n", formatted);
        var afterComment = formatted[(formatted.IndexOf("-- note", StringComparison.Ordinal) + 7)..];
        Assert.StartsWith("\n", afterComment);
    }
}
