using System;
using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The two DDL shapes the layout pass claims: CREATE TABLE's element list and CREATE FUNCTION /
/// PROCEDURE's envelope. Everything else in DDL still comes back byte for byte — see
/// <see cref="SqlFormatTests.A_statement_shape_with_no_layout_rules_is_returned_byte_for_byte"/>.
/// <para>
/// The function <b>body</b> is the line that must not move. <c>$$ … $$</c> is a single lexer token, which
/// is what makes it survive untouched, and it is not necessarily SQL: <c>LANGUAGE plv8</c> is JavaScript
/// and <c>plpython3u</c> is Python. Laying one out as SQL would not be a cosmetic mistake.
/// </para>
/// </summary>
public class SqlFormatDdlTests
{
    private static string Format(string sql)
    {
        var result = SqlFormat.Format(sql);
        Assert.Null(result.Refusal);

        var again = SqlFormat.Format(result.Text);
        Assert.False(again.Refused, again.Refusal);
        Assert.Equal(result.Text, again.Text);   // idempotent, like every other shape
        return result.Text;
    }

    // ---- CREATE TABLE ------------------------------------------------------------------------------

    [Fact]
    public void A_table_puts_one_column_or_constraint_per_line()
        => Assert.Equal(
            """
            CREATE TABLE foo (
                id int PRIMARY KEY,
                name text NOT NULL,
                CONSTRAINT c CHECK (id > 0)
            );
            """,
            Format("create table foo (id int primary key, name text not null, constraint c check (id > 0));"));

    /// <summary>Column types stay as written. A type is closer to a name than to a keyword, and shouting
    /// <c>INT</c> beside a lower-case column name buys nothing.</summary>
    [Fact]
    public void Column_types_keep_their_case_while_constraints_are_raised()
    {
        var formatted = Format("create table bar (id int, foo_id int references foo(id), unique (id, foo_id));");
        Assert.Contains("id int,", formatted, StringComparison.Ordinal);
        Assert.Contains("foo_id int REFERENCES foo(id),", formatted, StringComparison.Ordinal);
        Assert.Contains("UNIQUE (id, foo_id)", formatted, StringComparison.Ordinal);
    }

    /// <summary>Unlike the FROM list, a single column still breaks: a table is read as a list of columns
    /// however many there are, and one that grows should not change shape on its second column.</summary>
    [Fact]
    public void A_single_column_table_still_breaks()
        => Assert.Equal(
            """
            CREATE TABLE one (
                id int
            );
            """,
            Format("create table one (id int);"));

    // ---- CREATE FUNCTION / PROCEDURE ---------------------------------------------------------------

    [Fact]
    public void A_function_breaks_at_returns_and_each_option()
        => Assert.Equal(
            """
            CREATE FUNCTION f()
            RETURNS int
            AS $$ begin  return  1; end $$
            LANGUAGE plpgsql;
            """,
            Format("create function f() returns int as $$ begin  return  1; end $$ language plpgsql;"));

    [Fact]
    public void Several_parameters_break_one_per_line()
        => Assert.Equal(
            """
            CREATE OR REPLACE FUNCTION g(
                a int,
                b text DEFAULT 'x'
            )
            RETURNS TABLE (x int)
            AS $b$ select 1; $b$
            LANGUAGE sql
            STABLE;
            """,
            Format("create or replace function g(a int, b text default 'x') returns table (x int) "
                   + "as $b$ select 1; $b$ language sql stable;"));

    [Fact]
    public void A_procedure_gets_the_same_envelope()
        => Assert.Equal(
            """
            CREATE PROCEDURE p(a int)
            LANGUAGE plpgsql
            AS $$ begin end $$;
            """,
            Format("create procedure p(a int) language plpgsql as $$ begin end $$;"));

    /// <summary>
    /// The body is passed through exactly, spacing and all — it is one token, so this holds by construction
    /// rather than by care. The odd double spaces below are the assertion: if anything ever laid the body
    /// out, they would be the first thing to go.
    /// </summary>
    [Theory]
    [InlineData("create function f() returns int as $$ begin  return  1; end $$ language plpgsql",
        "$$ begin  return  1; end $$")]
    [InlineData("create function j(a int) returns int as $$ return a + 1; $$ language plv8",
        "$$ return a + 1; $$")]
    [InlineData("create function k() returns int as $b$ select\n\n  1; $b$ language sql",
        "$b$ select\n\n  1; $b$")]
    public void The_body_is_never_touched(string sql, string body)
        => Assert.Contains(body, Format(sql), StringComparison.Ordinal);

    /// <summary>A language name is an identifier, not a keyword — <c>plv8</c> must not become
    /// <c>PLV8</c>.</summary>
    [Theory]
    [InlineData("plpgsql")]
    [InlineData("plv8")]
    [InlineData("plpython3u")]
    public void The_language_name_keeps_its_case(string language)
        => Assert.Contains($"LANGUAGE {language}",
            Format($"create function f() returns int as $$ x $$ language {language}"),
            StringComparison.Ordinal);

    // ---- still untouched ---------------------------------------------------------------------------

    /// <summary>Claiming CREATE TABLE and CREATE FUNCTION must not have quietly claimed the rest of DDL.</summary>
    [Theory]
    [InlineData("alter table t\n    add column b int")]
    [InlineData("create index i on t (a)")]
    [InlineData("create view v as select a from t")]
    [InlineData("create materialized view mv as select a from t")]
    [InlineData("drop table if exists t cascade")]
    [InlineData("create type mood as enum ('sad', 'ok')")]
    [InlineData("create trigger trg before insert on t for each row execute function f()")]
    public void Other_ddl_is_still_returned_byte_for_byte(string sql)
    {
        var result = SqlFormat.Format(sql);
        Assert.False(result.Refused, result.Refusal);
        Assert.False(result.Changed);
        Assert.Equal(sql, result.Text);
    }
}
