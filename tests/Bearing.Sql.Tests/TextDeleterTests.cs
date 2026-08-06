using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>Readline-style backward deletes behind Ctrl+U (to line start) and Ctrl+W (word before).</summary>
public class TextDeleterTests
{
    // ---- Ctrl+U: delete to the beginning of the line ----

    [Fact]
    public void ToLineStart_deletes_from_column_zero_up_to_the_caret()
    {
        const string sql = "select 1 from t";
        var r = TextDeleter.ToLineStart(sql, 9, 9); // caret just before "from"

        Assert.Equal(0, r.Start);
        Assert.Equal(9, r.Length);
        Assert.Equal("from t", Remove(sql, r));
    }

    [Fact]
    public void ToLineStart_stays_on_the_caret_line()
    {
        const string sql = "select 1\nfrom t";
        var r = TextDeleter.ToLineStart(sql, 13, 13); // inside "from t"

        Assert.Equal(9, r.Start); // the line start, not the buffer start
        Assert.Equal("select 1\n t", Remove(sql, r));
    }

    [Fact]
    public void ToLineStart_deletes_the_indentation_too()
    {
        const string sql = "    select 1";
        var r = TextDeleter.ToLineStart(sql, sql.Length, sql.Length);

        Assert.Equal(0, r.Start);
        Assert.Equal("", Remove(sql, r)); // column 0 is the stop, not the first non-blank
    }

    [Fact]
    public void ToLineStart_at_column_zero_deletes_nothing()
    {
        const string sql = "select 1\nfrom t";
        Assert.True(TextDeleter.ToLineStart(sql, 9, 9).IsEmpty); // never joins with the line above
    }

    [Fact]
    public void ToLineStart_with_a_selection_takes_the_selection_and_the_line_before_it()
    {
        const string sql = "select foo from t";
        var r = TextDeleter.ToLineStart(sql, 7, 10); // "foo" selected

        Assert.Equal(0, r.Start);
        Assert.Equal(10, r.Length);
        Assert.Equal(" from t", Remove(sql, r));
    }

    [Fact]
    public void ToLineStart_with_a_multiline_selection_never_leaves_part_of_it()
    {
        const string sql = "select a\nfrom t\nwhere x";
        var r = TextDeleter.ToLineStart(sql, 11, 20); // mid line 2 → mid line 3

        Assert.Equal(9, r.Start); // start of line 2, so no fragment of the selection survives
        Assert.Equal("select a\ne x", Remove(sql, r));
    }

    // ---- Ctrl+W: delete the whitespace-delimited word before the caret ----

    [Fact]
    public void WordBefore_deletes_the_token_at_the_caret()
    {
        const string sql = "select count";
        var r = TextDeleter.WordBefore(sql, sql.Length, sql.Length);

        Assert.Equal("select ", Remove(sql, r));
    }

    [Fact]
    public void WordBefore_consumes_trailing_whitespace_first()
    {
        const string sql = "select count   ";
        var r = TextDeleter.WordBefore(sql, sql.Length, sql.Length);

        Assert.Equal("select ", Remove(sql, r)); // the spaces and the word go together
    }

    [Fact]
    public void WordBefore_is_whitespace_delimited_so_a_qualified_name_dies_whole()
    {
        const string sql = "select * from public.orders";
        var r = TextDeleter.WordBefore(sql, sql.Length, sql.Length);

        // The distinction from Ctrl+Backspace: the schema qualifier goes too, not just "orders".
        Assert.Equal("select * from ", Remove(sql, r));
    }

    [Fact]
    public void WordBefore_in_leading_indent_deletes_only_the_indent()
    {
        const string sql = "select a\n    from t";
        var r = TextDeleter.WordBefore(sql, 13, 13); // caret at the end of the 4-space indent

        Assert.Equal(9, r.Start);
        Assert.Equal(4, r.Length);
        Assert.Equal("select a\nfrom t", Remove(sql, r));
    }

    [Fact]
    public void WordBefore_at_column_zero_deletes_nothing()
    {
        const string sql = "select a\nfrom t";
        Assert.True(TextDeleter.WordBefore(sql, 9, 9).IsEmpty); // must not join lines
    }

    [Fact]
    public void WordBefore_with_a_selection_deletes_the_selection()
    {
        const string sql = "select foo from t";
        var r = TextDeleter.WordBefore(sql, 7, 10);

        Assert.Equal(7, r.Start);
        Assert.Equal(3, r.Length);
        Assert.Equal("select  from t", Remove(sql, r));
    }

    // ---- edges ----

    [Fact]
    public void Empty_buffer_is_a_no_op()
    {
        Assert.True(TextDeleter.ToLineStart("", 0, 0).IsEmpty);
        Assert.True(TextDeleter.WordBefore("", 0, 0).IsEmpty);
    }

    [Fact]
    public void Out_of_range_offsets_are_clamped_not_thrown()
    {
        const string sql = "select 1";
        Assert.Equal(sql.Length, TextDeleter.ToLineStart(sql, -5, 999).Length);
        Assert.Equal("select ", Remove(sql, TextDeleter.WordBefore(sql, 999, 999)));
    }

    [Fact]
    public void Reversed_selection_offsets_are_normalized()
    {
        const string sql = "select foo from t";
        Assert.Equal(TextDeleter.WordBefore(sql, 7, 10), TextDeleter.WordBefore(sql, 10, 7));
    }

    private static string Remove(string text, DeleteRange r) => text.Remove(r.Start, r.Length);
}
