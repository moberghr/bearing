using System;
using Squirrel.Sql;
using Xunit;

namespace Squirrel.Sql.Tests;

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
