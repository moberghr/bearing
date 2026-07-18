using Squirrel.Sql;
using Xunit;

namespace Squirrel.Sql.Tests;

/// <summary>Toggling <c>-- </c> line comments over the lines a caret or selection touches (Ctrl+/).</summary>
public class LineCommenterTests
{
    [Fact]
    public void Comments_a_single_line_at_the_caret()
    {
        const string sql = "select 1";
        var r = LineCommenter.Toggle(sql, 3, 3);

        Assert.Equal("-- select 1", r.Text);
        Assert.Equal(6, r.SelectionStart);   // caret shifted right by the 3 inserted chars
        Assert.Equal(0, r.SelectionLength);
    }

    [Fact]
    public void Uncomments_a_commented_line_round_trip()
    {
        const string sql = "-- select 1";
        var r = LineCommenter.Toggle(sql, 6, 6);

        Assert.Equal("select 1", r.Text);
        Assert.Equal(3, r.SelectionStart);   // caret shifted left by the removed "-- "
    }

    [Fact]
    public void Toggle_twice_restores_original()
    {
        const string sql = "  select 1\n  from t";
        var once = LineCommenter.Toggle(sql, 0, sql.Length);
        var twice = LineCommenter.Toggle(once.Text, once.SelectionStart, once.SelectionStart + once.SelectionLength);

        Assert.Equal(sql, twice.Text);
    }

    [Fact]
    public void Comments_all_selected_lines_at_the_common_indent()
    {
        const string sql = "  select 1\n  from t";
        var r = LineCommenter.Toggle(sql, 0, sql.Length);

        Assert.Equal("  -- select 1\n  -- from t", r.Text);
        Assert.Equal(0, r.SelectionStart);
        Assert.Equal(r.Text.Length, r.SelectionLength);   // whole block reselected
    }

    [Fact]
    public void Mixed_lines_get_commented_when_any_is_uncommented()
    {
        const string sql = "-- select 1\nfrom t";
        var r = LineCommenter.Toggle(sql, 0, sql.Length);

        // 'from t' is uncommented, so the toggle comments both.
        Assert.Equal("-- -- select 1\n-- from t", r.Text);
    }

    [Fact]
    public void Blank_lines_in_selection_are_left_alone()
    {
        const string sql = "select 1\n\nfrom t";
        var r = LineCommenter.Toggle(sql, 0, sql.Length);

        Assert.Equal("-- select 1\n\n-- from t", r.Text);
    }

    [Fact]
    public void Selection_ending_at_next_line_start_excludes_that_line()
    {
        const string sql = "select 1\nselect 2";
        var end = sql.IndexOf("select 2");   // exactly the start of line 2
        var r = LineCommenter.Toggle(sql, 0, end);

        Assert.Equal("-- select 1\nselect 2", r.Text);
    }

    [Fact]
    public void Whitespace_only_selection_is_unchanged()
    {
        const string sql = "   \n  ";
        var r = LineCommenter.Toggle(sql, 0, sql.Length);

        Assert.Equal(sql, r.Text);
    }

    [Fact]
    public void Empty_buffer_is_unchanged()
    {
        var r = LineCommenter.Toggle("", 0, 0);
        Assert.Equal("", r.Text);
    }
}
