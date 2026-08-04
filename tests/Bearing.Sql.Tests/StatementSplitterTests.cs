using System;
using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Splitting a multi-statement buffer on top-level semicolons and resolving the statement under
/// the caret — the basis for "run the statement at the caret" and statement-scoped completion.
/// </summary>
public class StatementSplitterTests
{
    [Fact]
    public void Split_breaks_on_top_level_semicolons()
    {
        var spans = StatementSplitter.Split("select 1; select 2; select 3");

        Assert.Equal(3, spans.Count);
        // Each statement owns the whitespace after its ';' (up to the next statement's first token).
        Assert.Equal("select 1; ", spans[0].Text);
        Assert.Equal("select 2; ", spans[1].Text);
        Assert.Equal("select 3", spans[2].Text);
    }

    [Fact]
    public void EnsureSeparated_terminates_blank_line_separated_statements()
    {
        // Two statements separated only by a blank line, first without a semicolon.
        var sql = "select 1\n\nselect 2";
        var normalized = StatementSplitter.EnsureSeparated(sql);
        Assert.Equal("select 1;\nselect 2;", normalized);
    }

    [Fact]
    public void EnsureSeparated_leaves_a_single_statement_untouched()
    {
        Assert.Equal("select 1", StatementSplitter.EnsureSeparated("select 1"));
        Assert.Equal("select 1;", StatementSplitter.EnsureSeparated("select 1;"));
    }

    [Fact]
    public void EnsureSeparated_collapses_existing_semicolons_without_duplicating()
    {
        Assert.Equal("select 1;\nselect 2;", StatementSplitter.EnsureSeparated("select 1;\n\nselect 2;"));
    }

    [Fact]
    public void EnsureSeparated_puts_terminator_on_its_own_line_after_a_trailing_line_comment()
    {
        // The ';' must not land on the "-- note" line, or the comment swallows it and the two
        // statements merge into one malformed command.
        var normalized = StatementSplitter.EnsureSeparated("select 1 -- note\n\nselect 2");
        Assert.Equal("select 1 -- note\n;\nselect 2;", normalized);

        // Re-splitting the normalized text must see two statements (the ';' actually terminates).
        Assert.Equal(2, StatementSplitter.Split(normalized).Count);
    }

    [Fact]
    public void Split_does_not_break_a_statement_continued_after_a_blank_line()
    {
        // A blank line before a continuation keyword ("and") is inside one statement, not a boundary.
        var spans = StatementSplitter.Split("select *\nfrom t\nwhere x = 1\n\nand y = 2");
        Assert.Single(spans);
    }

    [Fact]
    public void Split_does_not_break_before_trailing_clauses_or_set_operators()
    {
        Assert.Single(StatementSplitter.Split("select *\nfrom t\n\norder by id"));   // trailing clause
        Assert.Single(StatementSplitter.Split("select 1\n\nunion\n\nselect 2"));      // set operator
    }

    [Fact]
    public void Split_still_breaks_when_a_blank_line_precedes_a_new_statement()
    {
        // The blank-line convention still splits when the next line genuinely starts a statement.
        var spans = StatementSplitter.Split("update t set x = 1\n\ninsert into t values (2)");
        Assert.Equal(2, spans.Count);
        Assert.Contains("update", spans[0].Text);
        Assert.Contains("insert", spans[1].Text);
    }

    [Fact]
    public void Split_ignores_semicolons_inside_strings()
    {
        var spans = StatementSplitter.Split("select ';not a boundary;' as x; select 2");

        Assert.Equal(2, spans.Count);
        Assert.Contains(";not a boundary;", spans[0].Text);
    }

    [Fact]
    public void Split_ignores_semicolons_inside_line_comments()
    {
        var spans = StatementSplitter.Split("select 1 -- one; two; three\n; select 2");

        Assert.Equal(2, spans.Count);
        Assert.Contains("one; two; three", spans[0].Text);
    }

    [Fact]
    public void StatementAt_returns_the_statement_covering_the_caret()
    {
        const string sql = "select 1; select 2; select 3";
        var caret = sql.IndexOf("select 2");

        var stmt = StatementSplitter.StatementAt(sql, caret);

        Assert.NotNull(stmt);
        Assert.Contains("select 2", stmt!.Text);
        Assert.DoesNotContain("select 3", stmt.Text);
    }

    [Fact]
    public void StatementAt_right_after_semicolon_stays_on_the_statement_it_terminates()
    {
        // Caret just past the ';', still on that line — belongs to the query it terminated.
        const string sql = "select 1;\nselect 2";
        var caret = sql.IndexOf(';') + 1;

        var stmt = StatementSplitter.StatementAt(sql, caret);

        Assert.Contains("select 1", stmt!.Text);
        Assert.DoesNotContain("select 2", stmt.Text);
    }

    [Fact]
    public void StatementAt_on_blank_line_between_statements_selects_the_previous()
    {
        const string sql = "select 1;\n\nselect 2;";
        var caret = sql.IndexOf("\n\n") + 1; // the blank line between the two statements

        var stmt = StatementSplitter.StatementAt(sql, caret);

        Assert.Contains("select 1", stmt!.Text);
        Assert.DoesNotContain("select 2", stmt.Text);
    }

    [Fact]
    public void StatementAt_switches_to_next_statement_at_its_first_character()
    {
        const string sql = "select 1;\n\nselect 2;";
        var caret = sql.LastIndexOf("select 2", StringComparison.Ordinal); // first char of statement 2

        var stmt = StatementSplitter.StatementAt(sql, caret);

        Assert.Contains("select 2", stmt!.Text);
        Assert.DoesNotContain("select 1", stmt.Text);
    }

    [Fact]
    public void StatementAt_in_trailing_whitespace_falls_back_to_previous_statement()
    {
        const string sql = "select 1;   ";

        var stmt = StatementSplitter.StatementAt(sql, sql.Length);

        Assert.NotNull(stmt);
        Assert.Contains("select 1", stmt!.Text);
    }

    [Fact]
    public void Split_breaks_on_blank_lines_without_semicolons()
    {
        var spans = StatementSplitter.Split("select 1\nfrom a\n\nselect 2\nfrom b");

        Assert.Equal(2, spans.Count);
        Assert.Contains("select 1", spans[0].Text);
        Assert.DoesNotContain("select 2", spans[0].Text);
        Assert.Contains("select 2", spans[1].Text);
    }

    [Fact]
    public void Split_ignores_blank_lines_inside_parentheses()
    {
        // A blank line inside a subquery must not split the enclosing statement.
        var spans = StatementSplitter.Split("select * from (\n select 1\n\n from t\n) x");

        Assert.Single(spans);
    }

    [Fact]
    public void StatementAt_selects_the_blank_line_separated_statement()
    {
        const string sql = "select 1\nfrom a\n\nselect 2\nfrom b";
        var caret = sql.IndexOf("from b", StringComparison.Ordinal);

        var stmt = StatementSplitter.StatementAt(sql, caret);

        Assert.Contains("select 2", stmt!.Text);
        Assert.DoesNotContain("select 1", stmt.Text);
    }

    [Fact]
    public void Leading_comment_groups_with_the_statement_below_it()
    {
        // Blank lines separate the first query from the comment; the comment documents the query
        // that follows, so the caret on the comment resolves to that following query.
        const string sql = "select 1\nfrom a\nlimit 100;\n\n\n-- select *\nfrom film f\norder by id";
        var caret = sql.IndexOf("-- select *", StringComparison.Ordinal);

        var stmt = StatementSplitter.StatementAt(sql, caret)!;

        Assert.Contains("-- select *", stmt.Text);
        Assert.Contains("from film f", stmt.Text);
        Assert.DoesNotContain("limit 100", stmt.Text);
    }

    [Fact]
    public void Leading_comment_groups_with_the_next_block_without_semicolons()
    {
        const string sql = "select 1\nfrom a\n\n-- header\nselect 2\nfrom b";
        var caret = sql.IndexOf("-- header", StringComparison.Ordinal);

        var stmt = StatementSplitter.StatementAt(sql, caret)!;

        Assert.Contains("-- header", stmt.Text);
        Assert.Contains("select 2", stmt.Text);
        Assert.DoesNotContain("select 1", stmt.Text);
    }

    [Fact]
    public void Trailing_comment_on_the_same_line_stays_with_its_statement()
    {
        const string sql = "select 1; -- note\nselect 2;";
        var caret = sql.IndexOf("-- note", StringComparison.Ordinal);

        var stmt = StatementSplitter.StatementAt(sql, caret)!;

        Assert.Contains("select 1", stmt.Text);
        Assert.Contains("-- note", stmt.Text);
        Assert.DoesNotContain("select 2", stmt.Text);
    }

    [Fact]
    public void StatementAt_returns_null_for_blank_buffer()
    {
        Assert.Null(StatementSplitter.StatementAt("   \n  ", 2));
    }

    [Fact]
    public void TrimmedSpan_excludes_surrounding_whitespace()
    {
        const string sql = "select 1;\n   select 2   ;";
        var caret = sql.IndexOf("select 2");

        var stmt = StatementSplitter.StatementAt(sql, caret)!;

        Assert.Equal("select 2   ;", sql[stmt.TrimmedStart..stmt.TrimmedEnd]);
        Assert.Equal('s', sql[stmt.TrimmedStart]);
        Assert.Equal(';', sql[stmt.TrimmedEnd - 1]);
    }
}
