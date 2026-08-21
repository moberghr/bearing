using System;
using AvaloniaEdit.Document;
using Bearing.App.Completion;
using Bearing.Core.Completion;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// What accepting a completion types (#41). Accepting used to leave the caret hard against the inserted
/// text, so every completion cost an extra space keystroke — but the space is wrong for some kinds, so
/// this pins the decision per kind. Driving the popup itself needs eyeball QA (§4.3).
/// </summary>
public class CompletionInsertionTests
{
    private static Suggestion Sug(SuggestionKind kind, string replacement) => new()
    {
        DisplayText = replacement,
        ReplacementText = replacement,
        Kind = kind,
    };

    private static string Insert(SuggestionKind kind, string replacement, char? next = null)
        => CompletionInsertion.TextFor(Sug(kind, replacement), next);

    [Theory]
    [InlineData(SuggestionKind.Table, "settlements s", "settlements s ")]
    [InlineData(SuggestionKind.View, "active_users a", "active_users a ")]
    [InlineData(SuggestionKind.Column, "u.id", "u.id ")]
    [InlineData(SuggestionKind.Keyword, "select", "select ")]
    [InlineData(SuggestionKind.Join, "orders o on o.user_id = u.id", "orders o on o.user_id = u.id ")]
    public void The_kinds_more_sql_follows_get_a_trailing_space(
        SuggestionKind kind, string replacement, string expected)
        => Assert.Equal(expected, Insert(kind, replacement));

    [Fact]
    public void A_schema_qualifier_keeps_the_caret_glued_to_its_dot()
    {
        // A space here breaks the relation completion the popup reopens for (OnInserted).
        Assert.Equal("audit.", Insert(SuggestionKind.Schema, "audit."));
        Assert.Equal("", CompletionInsertion.SuffixFor(SuggestionKind.Schema));
    }

    [Fact]
    public void A_function_opens_its_argument_list_instead()
        => Assert.Equal("coalesce(", Insert(SuggestionKind.Function, "coalesce"));

    [Fact]
    public void Every_kind_has_a_decision_and_none_of_them_mangles_the_sql()
    {
        foreach (var kind in Enum.GetValues<SuggestionKind>())
        {
            var text = Insert(kind, "orders o");
            Assert.StartsWith("orders o", text);
            Assert.InRange(text.Length - "orders o".Length, 0, 1);
        }
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('\t')]
    public void The_space_is_dropped_when_the_line_already_separates_the_tokens(char next)
    {
        // Re-completing an identifier mid-statement (`select id| from t`) must not double the space.
        Assert.Equal("u.id", Insert(SuggestionKind.Column, "u.id", next));
    }

    [Theory]
    [InlineData('\n')]
    [InlineData('\r')]
    public void A_line_break_after_the_caret_is_not_a_space(char next)
    {
        // The regression that made this invisible in the app: completing at the end of a line — i.e.
        // almost every completion in a real buffer — took the "already spaced" path via
        // char.IsWhiteSpace and inserted nothing. A newline separates lines, not tokens.
        Assert.Equal("u.id ", Insert(SuggestionKind.Column, "u.id", next));
    }

    [Fact]
    public void The_paren_is_dropped_when_the_document_already_has_one()
        => Assert.Equal("coalesce", Insert(SuggestionKind.Function, "coalesce", '('));

    [Theory]
    [InlineData(',')]
    [InlineData(')')]
    [InlineData(';')]
    public void A_delimiter_already_sitting_there_gets_no_space_at_all(char next)
    {
        // `select id|, name` -> `select c.id, name`. The typed-character reclaim only fires on a
        // keystroke, so a delimiter that is *already* in the document has to be handled up front or
        // the space would stand forever.
        Assert.Equal("u.id", Insert(SuggestionKind.Column, "u.id", next));
    }

    [Fact]
    public void At_the_end_of_the_document_the_suffix_still_goes_in()
        => Assert.Equal("select ", Insert(SuggestionKind.Keyword, "select", next: null));

    [Theory]
    [InlineData(',', true)]
    [InlineData(')', true)]
    [InlineData(';', true)]
    [InlineData('=', false)]   // `u.id = 1` wants the space — that is the point of appending it
    [InlineData('a', false)]
    [InlineData(' ', false)]
    [InlineData('.', false)]
    public void Only_characters_that_read_wrong_after_a_space_reclaim_it(char typed, bool swallows)
        => Assert.Equal(swallows, CompletionInsertion.SwallowsTrailingSpace(typed));

    [Fact]
    public void No_operator_reclaims_the_space()
        => Assert.All("=<>+-*/%|&!", ch => Assert.False(CompletionInsertion.SwallowsTrailingSpace(ch)));

    // ---- Against a real document ---------------------------------------------------------------
    // TextDocument is AvaloniaEdit's model, not a control, so the whole insert-then-reclaim path runs
    // here — the anchor arithmetic and the caret guard included. Only the popup itself needs eyeball QA.

    /// <summary>Accept <paramref name="s"/> over the <c>|</c> in <paramref name="sqlWithCaret"/>,
    /// returning the document text and the soft-space offset, exactly as BearingCompletionData does.</summary>
    private static (TextDocument Document, int SoftSpace) Accept(string sqlWithCaret, Suggestion s, int replaced = 0)
    {
        var caret = sqlWithCaret.IndexOf('|');
        var document = new TextDocument(sqlWithCaret.Remove(caret, 1));
        // The span covers the half-typed word *behind* the caret, which is how the engine reports it.
        var segment = new AnchorSegment(document, caret - replaced, replaced);
        return (document, CompletionInsertion.Apply(document, segment, s));
    }

    [Fact]
    public void Accepting_a_column_after_an_alias_dot_leaves_the_caret_past_a_space()
    {
        // The reported case: `select * from customer c where c.|`, pick a column, press Enter.
        var (document, softSpace) = Accept("select * from customer c where c.|", Sug(SuggestionKind.Column, "store_id"));
        Assert.Equal("select * from customer c where c.store_id ", document.Text);
        Assert.Equal(document.TextLength - 1, softSpace);
    }

    [Fact]
    public void Accepting_over_a_half_typed_name_replaces_it_and_still_spaces()
    {
        var (document, _) = Accept("select * from customer c where c.sto|", Sug(SuggestionKind.Column, "store_id"), replaced: 3);
        Assert.Equal("select * from customer c where c.store_id ", document.Text);
    }

    [Fact]
    public void A_schema_pick_reports_no_soft_space_so_nothing_can_reclaim_one()
    {
        var (document, softSpace) = Accept("select * from |", Sug(SuggestionKind.Schema, "audit."));
        Assert.Equal("select * from audit.", document.Text);
        Assert.Equal(-1, softSpace);
    }

    [Fact]
    public void Re_completing_mid_statement_does_not_leave_a_double_space()
    {
        var (document, softSpace) = Accept("select id| from customer c", Sug(SuggestionKind.Column, "c.id"), replaced: 2);
        Assert.Equal("select c.id from customer c", document.Text);
        Assert.Equal(-1, softSpace);   // the document's own space stands; there is nothing to reclaim
    }

    [Fact]
    public void Completing_at_the_end_of_a_line_still_gets_its_space()
    {
        // The exact shape of the reported failure: the query is not the last thing in the buffer.
        var (document, softSpace) = Accept("select * from customer c where c.|\nselect 1", Sug(SuggestionKind.Column, "store_id"));
        Assert.Equal("select * from customer c where c.store_id \nselect 1", document.Text);
        Assert.Equal(41, softSpace);
    }

    [Fact]
    public void An_existing_comma_after_the_caret_gets_no_space()
    {
        var (document, softSpace) = Accept("select id|, name from customer c", Sug(SuggestionKind.Column, "c.id"), replaced: 2);
        Assert.Equal("select c.id, name from customer c", document.Text);
        Assert.Equal(-1, softSpace);
    }

    [Fact]
    public void The_next_comma_reclaims_the_space()
    {
        var (document, softSpace) = Accept("select |", Sug(SuggestionKind.Column, "c.id"));
        Assert.Equal("select c.id ", document.Text);

        // Typing lands the character first (TextEntered), then the swallow runs.
        document.Insert(document.TextLength, ",");
        Assert.True(CompletionInsertion.TrySwallow(document, document.TextLength, softSpace, ','));
        Assert.Equal("select c.id,", document.Text);
    }

    [Fact]
    public void A_word_typed_next_keeps_the_space()
    {
        var (document, softSpace) = Accept("select c.id |", Sug(SuggestionKind.Keyword, "from"));
        Assert.Equal("select c.id from ", document.Text);

        document.Insert(document.TextLength, "c");
        Assert.False(CompletionInsertion.TrySwallow(document, document.TextLength, softSpace, 'c'));
        Assert.Equal("select c.id from c", document.Text);
    }

    [Fact]
    public void A_comma_typed_somewhere_else_leaves_the_space_alone()
    {
        var (document, softSpace) = Accept("select c.id, |", Sug(SuggestionKind.Column, "c.name"));
        Assert.Equal("select c.id, c.name ", document.Text);

        // The caret jumped to the head of the line; that comma has nothing to do with the completion,
        // and the space it left behind has to survive.
        document.Insert(0, ",");
        Assert.False(CompletionInsertion.TrySwallow(document, 1, softSpace, ','));
        Assert.Equal(",select c.id, c.name ", document.Text);
    }

    [Fact]
    public void The_offset_is_only_good_once()
    {
        var (document, softSpace) = Accept("select |", Sug(SuggestionKind.Column, "c.id"));
        document.Insert(document.TextLength, ",");
        Assert.True(CompletionInsertion.TrySwallow(document, document.TextLength, softSpace, ','));

        // A second comma cannot reclaim anything: the space is gone and the caret has moved on.
        document.Insert(document.TextLength, ",");
        Assert.False(CompletionInsertion.TrySwallow(document, document.TextLength, softSpace, ','));
        Assert.Equal("select c.id,,", document.Text);
    }
}
