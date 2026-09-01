using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The decisions behind auto-closing quotes and brackets (#70). All of the correctness lives here: the
/// behaviour class around it is event plumbing, and keyboard wiring cannot be driven headlessly (§4.5), so
/// every awkward case is a test rather than something to try in the running app.
/// <para>
/// A caret is written as <c>|</c> and a selection as <c>[…]</c> in the fixtures below, which keeps the
/// interesting part of each case readable.
/// </para>
/// </summary>
public class AutoCloseTests
{
    /// <summary>Parse a fixture: <c>|</c> marks the caret, <c>[…]</c> marks a selection.</summary>
    private static (string Text, int Start, int Length) Parse(string fixture)
    {
        if (fixture.Contains('['))
        {
            var open = fixture.IndexOf('[');
            var close = fixture.IndexOf(']');
            var text = fixture.Remove(close, 1).Remove(open, 1);
            return (text, open, close - open - 1);
        }
        var caret = fixture.IndexOf('|');
        return (fixture.Remove(caret, 1), caret, 0);
    }

    private static AutoCloseDecision Type(string fixture, char typed)
    {
        var (text, start, length) = Parse(fixture);
        return AutoClose.ForTyped(text, start, length, typed);
    }

    // ---- opening a pair -------------------------------------------------------------------------

    [Theory]
    [InlineData('\'', "''")]
    [InlineData('"', "\"\"")]
    [InlineData('(', "()")]
    public void An_opener_brings_its_closer_and_the_caret_lands_between(char typed, string expected)
    {
        var decision = Type("select |", typed);

        Assert.Equal(AutoCloseAction.Pair, decision.Action);
        Assert.Equal(expected, decision.Text);
        Assert.Equal(1, decision.Caret);
    }

    [Fact]
    public void An_opener_at_the_very_end_of_the_buffer_still_pairs()
        => Assert.Equal(AutoCloseAction.Pair, Type("select * from t where x = |", '\'').Action);

    [Fact]
    public void An_opener_before_a_closing_paren_pairs()
        // `in (|)` — the next character is punctuation, so a closer sits fine beside it.
        => Assert.Equal(AutoCloseAction.Pair, Type("where id in (|)", '\'').Action);

    [Theory]
    [InlineData("select |abc")]          // a name follows
    [InlineData("select |1")]            // a number follows
    [InlineData("select |_x")]           // an identifier can start with _
    [InlineData("select |$1")]           // a placeholder or dollar quote
    [InlineData("select |(a)")]          // another opener
    public void An_opener_does_not_pair_when_it_would_split_what_follows(string fixture)
    {
        // Typing `(` in front of something is someone wrapping it; `(|)abc` is not what they meant.
        Assert.Equal(AutoCloseAction.None, Type(fixture, '(').Action);
    }

    // ---- context -------------------------------------------------------------------------------

    [Theory]
    [InlineData("select 'abc|")]                       // inside an unterminated literal
    [InlineData("select 'abc| def' from t")]           // inside a closed one
    [InlineData("-- a comment |")]                     // a line comment
    [InlineData("/* a block | */ select 1")]           // a block comment
    public void No_pairing_inside_a_literal_or_a_comment(string fixture)
    {
        // The character is content there, not syntax, and pairing is pure obstruction.
        Assert.Equal(AutoCloseAction.None, Type(fixture, '\'').Action);
        Assert.Equal(AutoCloseAction.None, Type(fixture, '(').Action);
    }

    [Fact]
    public void Typing_the_quote_that_ends_the_literal_you_are_in_steps_over_it()
    {
        // Inside `'it''s |'` the next character is the literal's own closing quote, so this is the skip-over
        // case rather than the no-pairing one — which is what a person typing the closer means. A paren
        // there is still content, and left alone.
        Assert.Equal(AutoCloseAction.SkipOver, Type("select 'it''s |' from t", '\'').Action);
        Assert.Equal(AutoCloseAction.None, Type("select 'it''s |' from t", '(').Action);
    }

    [Fact]
    public void A_multi_line_literal_is_still_a_literal()
    {
        // Postgres string literals may span lines, which is why this reads the lexer rather than counting
        // quotes on the current line.
        var fixture = "select 'first" + ((char)10).ToString() + "second |" + ((char)10).ToString() + "third'";
        Assert.Equal(AutoCloseAction.None, Type(fixture, '\'').Action);
    }

    [Fact]
    public void A_dollar_quoted_body_is_left_alone()
    {
        var fixture = "create function f() as $$ select |";
        Assert.Equal(AutoCloseAction.None, Type(fixture, '\'').Action);
    }

    [Fact]
    public void Closing_a_literal_is_not_pairing()
        // At `'abc|` typing a quote means "I am done" — and the skip-over rule does not apply either,
        // because there is no closer under the caret. The editor just inserts it.
        => Assert.Equal(AutoCloseAction.None, Type("select 'abc|", '\'').Action);

    // ---- stepping over -------------------------------------------------------------------------

    [Theory]
    [InlineData("select '|'", '\'')]
    [InlineData("select \"|\"", '"')]
    [InlineData("count(|)", ')')]
    public void Typing_the_closer_under_the_caret_steps_over_it(string fixture, char typed)
    {
        // Without this, auto-close is worse than none for anyone who types closers out of habit: you would
        // get '' then ''' .
        Assert.Equal(AutoCloseAction.SkipOver, Type(fixture, typed).Action);
    }

    [Fact]
    public void Stepping_over_wins_against_opening_when_the_halves_are_the_same()
    {
        // `'` is both opener and closer, so order matters: with the caret at `'|'` the answer is skip, not
        // a nested pair.
        Assert.Equal(AutoCloseAction.SkipOver, Type("'|'", '\'').Action);
    }

    [Fact]
    public void A_closer_with_nothing_under_the_caret_is_left_to_the_editor()
        => Assert.Equal(AutoCloseAction.None, Type("count(a|", ')').Action);

    // ---- surrounding a selection ---------------------------------------------------------------

    [Theory]
    [InlineData('\'', "'abc'")]
    [InlineData('"', "\"abc\"")]
    [InlineData('(', "(abc)")]
    public void An_opener_wraps_the_selection(char typed, string expected)
    {
        var decision = Type("select [abc] from t", typed);

        Assert.Equal(AutoCloseAction.Surround, decision.Action);
        Assert.Equal(expected, decision.Text);
        Assert.Equal(4, decision.Caret);   // past the wrapped text, before the closer
    }

    [Fact]
    public void A_selection_wraps_even_where_a_bare_caret_would_not_pair()
        // Wrapping is unambiguous: the user has said what to enclose, so the "would this split a name"
        // rule does not apply.
        => Assert.Equal(AutoCloseAction.Surround, Type("select [abc]def", '(').Action);

    [Fact]
    public void A_non_opener_still_replaces_the_selection()
        => Assert.Equal(AutoCloseAction.None, Type("select [abc] from t", 'x').Action);

    // ---- backspace -----------------------------------------------------------------------------

    [Theory]
    [InlineData("select ''", 8)]
    [InlineData("select \"\"", 8)]
    [InlineData("count()", 6)]
    public void Backspace_takes_both_halves_of_an_empty_pair(string text, int caret)
        => Assert.True(AutoClose.DeletesEmptyPair(text, caret));

    [Theory]
    [InlineData("select 'a'", 9)]       // not empty
    [InlineData("select ''", 9)]        // caret past the closer
    [InlineData("select ''", 7)]        // caret before the opener
    [InlineData("", 0)]
    public void Backspace_is_ordinary_everywhere_else(string text, int caret)
        => Assert.False(AutoClose.DeletesEmptyPair(text, caret));

    // ---- the pair table ------------------------------------------------------------------------

    [Fact]
    public void The_pairs_are_the_two_quote_forms_and_the_paren()
    {
        Assert.Equal('\'', AutoClose.CloserFor('\''));
        Assert.Equal('"', AutoClose.CloserFor('"'));
        Assert.Equal(')', AutoClose.CloserFor('('));
        // Deliberately not brackets or braces: neither opens anything in Postgres that wants closing as you
        // type — `[` is array subscripting, and `{` appears only inside literals.
        Assert.Null(AutoClose.CloserFor('['));
        Assert.Null(AutoClose.CloserFor('{'));
        Assert.False(AutoClose.IsOpener('$'));
    }

    [Fact]
    public void An_out_of_range_selection_is_clamped_rather_than_thrown()
    {
        // Clamped to the ends of the buffer: offset 0 is before a digit so it does not pair, and a caret
        // past the end lands at the end, where it does.
        Assert.Equal(AutoCloseAction.None, AutoClose.ForTyped("select 1", -3, 0, '(').Action);
        Assert.Equal(AutoCloseAction.Pair, AutoClose.ForTyped("select 1", 99, 0, '(').Action);
        Assert.Equal(AutoCloseAction.Surround, AutoClose.ForTyped("select 1", 0, 999, '(').Action);
        // An empty buffer pairs, which is right — typing `(` in an empty editor should give you `(|)`.
        Assert.Equal(AutoCloseAction.Pair, AutoClose.ForTyped(null!, 0, 0, '(').Action);
        Assert.Equal(AutoCloseAction.Pair, AutoClose.ForTyped("", 0, 0, '(').Action);
    }
}
