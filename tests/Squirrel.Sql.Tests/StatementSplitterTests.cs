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
        Assert.Equal("select 1;", spans[0].Text);
        Assert.Equal(" select 2;", spans[1].Text);
        Assert.Equal(" select 3", spans[2].Text);
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
    public void StatementAt_treats_boundary_as_the_following_statement()
    {
        const string sql = "select 1;select 2";
        var caret = sql.IndexOf(';') + 1; // right after the semicolon

        var stmt = StatementSplitter.StatementAt(sql, caret);

        Assert.Equal("select 2", stmt!.Text);
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
