using Bearing.Core.Completion;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Offering statement keywords on a fresh line (#68). Writing a query, pressing Enter and typing the next
/// one offered nothing useful: a lone newline is deliberately not a statement boundary, so the grammar was
/// asked what could <i>continue</i> <c>select * from users</c> — and <c>select</c> is not one of those
/// things. Terminating with a <c>;</c> was the workaround.
/// <para>
/// The fix is additive rather than a re-scoping: a line holding one bare word is genuinely ambiguous, so
/// both readings are offered and the typed fragment narrows them.
/// </para>
/// </summary>
public class StatementStartCompletionTests
{
    private static readonly CompletionEngine Engine = new();
    private static readonly Bearing.Core.Schema.SchemaSnapshot Schema = TestSchema.Build();

    private static string[] KeywordsFor(string sql)
        => Engine.Complete(sql, sql.Length, Schema).Suggestions
            .Where(s => s.Kind == SuggestionKind.Keyword)
            .Select(s => s.DisplayText.ToLowerInvariant())
            .ToArray();

    private const string Nl = "\n";

    // ---- the report -----------------------------------------------------------------------------

    [Fact]
    public void A_fresh_line_after_an_unterminated_query_offers_statement_keywords()
    {
        var keywords = KeywordsFor("select * from users" + Nl);

        Assert.Contains("select", keywords);
        Assert.Contains("with", keywords);
        Assert.Contains("insert", keywords);
        Assert.Contains("update", keywords);
        Assert.Contains("delete", keywords);
        Assert.Contains("create", keywords);
        Assert.Contains("explain", keywords);
    }

    [Fact]
    public void The_same_holds_while_the_keyword_is_being_typed()
    {
        // The case the splitter's own blank-line rule cannot serve: it matches a token's full text against
        // the keyword set, so it only fires once `select` is complete — after the suggestion is wanted.
        Assert.Contains("select", KeywordsFor("select * from users" + Nl + "s"));
        Assert.Contains("select", KeywordsFor("select * from users" + Nl + "sel"));
    }

    [Fact]
    public void A_blank_line_between_the_statements_works_too()
        => Assert.Contains("select", KeywordsFor("select * from users" + Nl + Nl + "s"));

    [Fact]
    public void Indentation_before_the_word_is_still_a_line_start()
        => Assert.Contains("select", KeywordsFor("select * from users" + Nl + "    s"));

    [Fact]
    public void A_terminated_statement_still_works_as_it_always_did()
        => Assert.Contains("select", KeywordsFor("select * from users;" + Nl));

    // ---- what must not change -------------------------------------------------------------------

    [Fact]
    public void A_continuation_keyword_is_still_offered_on_that_same_line()
    {
        // The reason this is additive: `w` on a fresh line could be `where` continuing the query above just
        // as easily as a new statement. Re-scoping to the current line would have lost this one.
        var keywords = KeywordsFor("select * from users" + Nl + "w");

        Assert.Contains("where", keywords);
        Assert.Contains("with", keywords);   // …and the statement-start reading is there beside it
    }

    [Fact]
    public void Mid_statement_is_untouched()
    {
        // Not at a line start, so nothing is added — the grammar already knows what belongs here.
        var keywords = KeywordsFor("select a, ");
        Assert.DoesNotContain("drop", keywords);
        Assert.DoesNotContain("vacuum", keywords);
    }

    [Fact]
    public void After_a_completed_clause_on_the_same_line_is_untouched()
    {
        var keywords = KeywordsFor("select * from users where ");
        Assert.DoesNotContain("drop", keywords);
        Assert.DoesNotContain("truncate", keywords);
    }

    [Fact]
    public void A_second_word_on_the_line_is_a_continuation_not_a_start()
    {
        // `order b` is two words: the line has stopped being a bare statement opener.
        var keywords = KeywordsFor("select * from users" + Nl + "order b");
        Assert.DoesNotContain("drop", keywords);
    }

    [Fact]
    public void A_half_typed_alias_on_the_same_line_is_still_an_alias_slot()
    {
        // The alias rule must survive: `from users u` is the user inventing a name, and nothing in the
        // catalog or the grammar belongs there. Only a *half-typed* one returns nothing — an empty alias
        // slot still gets keywords, which is what `as` / `join` / `where` need.
        const string sql = "select * from users u";
        Assert.Empty(Engine.Complete(sql, sql.Length, Schema).Suggestions);
    }

    // ---- the rule itself ------------------------------------------------------------------------

    /// <summary>The rule on its own. Newlines are written as the pilcrow below and substituted, because an
    /// escape inside an attribute is the one thing that cannot be read at a glance in a table this size.</summary>
    [Theory]
    [InlineData("select 1¶", true)]           // a fresh line after content
    [InlineData("select 1¶s", true)]          // ...while typing
    [InlineData("select 1¶   sel", true)]     // indented
    [InlineData("select 1¶¶", true)]          // after a blank line
    [InlineData("select 1", false)]           // same line as the content
    [InlineData("select 1¶order b", false)]   // a second word: a continuation
    [InlineData("select 1¶a.", false)]        // a qualifier, not a bare word
    [InlineData("select 1¶x, ", false)]       // punctuation
    [InlineData("", false)]                   // nothing to relax
    [InlineData("¶", false)]                  // no earlier content
    [InlineData("   ¶  ", false)]             // whitespace only
    public void The_hint_fires_only_at_a_bare_line_start(string pattern, bool expected)
    {
        var sql = Lines(pattern);
        Assert.Equal(expected, StatementStartHint.AtLineStart(sql, sql.Length));
    }

    [Fact]
    public void The_hint_reads_the_caret_not_the_end_of_the_buffer()
    {
        // A caret parked at a line start in the middle of a buffer is still a line start.
        var sql = Lines("select 1¶¶select 2");
        Assert.True(StatementStartHint.AtLineStart(sql, 9));    // the blank line
        Assert.False(StatementStartHint.AtLineStart(sql, 4));   // inside the first `select`
    }

    [Fact]
    public void An_out_of_range_caret_is_clamped_rather_than_thrown()
    {
        Assert.False(StatementStartHint.AtLineStart("select 1", -5));
        Assert.False(StatementStartHint.AtLineStart("select 1", 500));
    }

    /// <summary>The marker the table above writes a newline as.</summary>
    private const char Marker = '¶';

    private static string Lines(string pattern) => pattern.Replace(Marker, NlChar);

    private static readonly char NlChar = (char)10;

    [Fact]
    public void Every_keyword_is_offered_once()
    {
        var keywords = KeywordsFor("select * from users" + Nl + "s");
        Assert.Equal(keywords.Length, keywords.Distinct().Count());
    }
}
