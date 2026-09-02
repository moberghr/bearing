using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Whether an offset sits inside a single-quoted literal — the gate that stops the completion popup
/// appearing over string data.
/// <para>
/// Lexer-based, so the cases below are the ones a regex gets wrong: a doubled <c>''</c> escaping a quote
/// inside a string, and an apostrophe inside a dollar-quoted body. Getting either wrong flips the answer for
/// the rest of the buffer, which would suppress completion everywhere after one stray quote.
/// </para>
/// </summary>
public class SqlStringLiteralTests
{
    /// <summary>The offset of the character after <paramref name="marker"/>, for readable fixtures.</summary>
    private static int After(string sql, string marker) => sql.IndexOf(marker) + marker.Length;

    [Fact]
    public void Inside_a_string_literal()
    {
        const string sql = "select * from t where name = 'joh";
        Assert.True(SqlStringLiterals.Contains(sql, sql.Length));
        Assert.True(SqlStringLiterals.Contains(sql, After(sql, "'j")));
    }

    [Fact]
    public void Outside_a_string_literal()
    {
        const string sql = "select * from t where name = 'john' and i";
        Assert.False(SqlStringLiterals.Contains(sql, sql.Length));
        Assert.False(SqlStringLiterals.Contains(sql, After(sql, "select ")));
    }

    [Fact]
    public void The_opening_quote_itself_is_still_outside()
    {
        // The caret arriving at a fresh quote is not inside anything yet, and suppressing there would mean
        // the popup vanished a keystroke before the user typed the value they wanted help with.
        const string sql = "select * from t where name = '";
        Assert.False(SqlStringLiterals.Contains(sql, sql.Length - 1));
        Assert.True(SqlStringLiterals.Contains(sql, sql.Length));
    }

    [Fact]
    public void A_double_quoted_identifier_is_not_a_string()
    {
        // The whole point of restricting this to single quotes: "My Table" is how you name an identifier
        // with a capital or a space, so it is exactly where a table name belongs.
        const string sql = "select * from \"My Tab";
        Assert.False(SqlStringLiterals.Contains(sql, sql.Length));
    }

    [Fact]
    public void A_doubled_quote_escapes_and_does_not_end_the_string()
    {
        // 'it''s' is one literal containing an apostrophe. A regex reading quotes pairwise sees it end early
        // and would call the rest of the statement "outside".
        const string sql = "select * from t where s = 'it''s here' and col";
        Assert.False(SqlStringLiterals.Contains(sql, sql.Length));
        Assert.True(SqlStringLiterals.Contains(sql, After(sql, "'it''s ")));
    }

    [Fact]
    public void An_apostrophe_inside_a_dollar_quoted_body_does_not_open_a_string()
    {
        // $$ … $$ takes arbitrary text, apostrophes included. Treating that lone quote as an opener would
        // mark everything after it as string, and completion would be dead for the rest of the file.
        const string sql = "create function f() returns void as $$ begin perform 'it's fine'; end $$ language plpgsql; select co";
        Assert.False(SqlStringLiterals.Contains(sql, sql.Length));
    }

    [Fact]
    public void Inside_a_dollar_quoted_body_counts_as_a_literal()
    {
        // The body is data too, not SQL the catalog can help with.
        const string sql = "do $$ begin perform 1; en";
        Assert.True(SqlStringLiterals.Contains(sql, sql.Length));
    }

    [Fact]
    public void An_escape_string_is_a_literal_too()
    {
        const string sql = @"select * from t where s = E'line\nbrea";
        Assert.True(SqlStringLiterals.Contains(sql, sql.Length));
    }

    [Fact]
    public void An_empty_buffer_and_out_of_range_offsets_are_outside()
    {
        Assert.False(SqlStringLiterals.Contains("", 0));
        Assert.False(SqlStringLiterals.Contains("select 1", -5));
        Assert.False(SqlStringLiterals.Contains("select 1", 999));
    }
}
