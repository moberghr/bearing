using Bearing.App.Editing;
using TextMateSharp.Registry;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The packaged SQL grammar colours <c>null</c> and has no rule at all for <c>true</c>/<c>false</c>, so the
/// booleans rendered as plain text; <see cref="SqlGrammarInjection"/> adds the missing rule.
/// <para>§4.5 rules out reading a colour back off a rendered visual line — the tokenizer colours a line as it
/// is drawn, and a suite written that way was dropped as flaky. This asks the tokenizer and the theme
/// directly instead, which is deterministic and needs no window at all.</para>
/// </summary>
public class SqlHighlightingTests
{
    private const string SqlScope = "source.sql";

    /// <summary>
    /// The scopes on the token covering <paramref name="word"/>.
    /// <para>The span is asserted rather than assumed: every regression here <em>changes where the token
    /// boundaries fall</em> — a lost <c>\b</c> splits <c>is_true</c> into two tokens, a lost rule merges the
    /// boolean back into the run beside it — so a bare <c>Single</c> would throw its own exception and report
    /// a broken fixture instead of the rule that broke.</para>
    /// </summary>
    private static List<string> ScopesOn(string sql, string word)
    {
        var at = sql.IndexOf(word, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{word}' is not in the fixture");

        var tokens = new Registry(EditorChrome.SqlOptions)
            .LoadGrammar(SqlScope)
            .TokenizeLine(sql).Tokens;

        var covering = tokens
            .Where(t => t.StartIndex <= at && t.EndIndex >= at + word.Length)
            .ToList();

        Assert.True(covering.Count == 1,
            $"'{word}' is not one token in \"{sql}\" — the tokenizer split it, so the rule under test changed "
            + $"the boundaries: [{string.Join(", ", tokens.Select(t => $"{t.StartIndex}..{t.EndIndex}"))}]");

        return covering[0].Scopes;
    }

    /// <summary>The theme's resolved foreground for the token covering <paramref name="word"/>.</summary>
    private static string ColourOf(string sql, string word)
    {
        var theme = new Registry(EditorChrome.SqlOptions).GetTheme();
        var rules = theme.Match(ScopesOn(sql, word));
        var foreground = rules.Select(r => r.foreground).FirstOrDefault(f => f > 0);
        return theme.GetColor(foreground) ?? "";
    }

    [Fact]
    public void A_boolean_literal_is_tagged_as_one()
    {
        Assert.Contains(SqlGrammarInjection.BooleanScope,
            ScopesOn("where t.is_financial = FALSE;", "FALSE"));
    }

    [Fact]
    public void True_and_false_are_coloured_like_null()
    {
        // The whole ask: a boolean reads as the literal beside the null it sits next to, rather than as the
        // surrounding plain text. Compared against null rather than against a hex constant, so a theme
        // change moves both or fails here — and it should fail here, because "the same colour as null" is
        // the requirement, not an accident of DarkPlus giving keyword and constant.language one hue.
        const string sql = "where a is null and b = false and c = true";

        var forNull = ColourOf(sql, "null");
        Assert.False(string.IsNullOrEmpty(forNull), "the fixture's null is not coloured either — wrong scope");

        Assert.Equal(forNull, ColourOf(sql, "false"));
        Assert.Equal(forNull, ColourOf(sql, "true"));
    }

    [Fact]
    public void A_boolean_inside_a_string_is_left_alone()
    {
        // The injection selector declines the string stack; without that, the word inside a literal would be
        // recoloured mid-string.
        var scopes = ScopesOn("select 'true' as t", "true");

        Assert.DoesNotContain(SqlGrammarInjection.BooleanScope, scopes);
        Assert.Contains(scopes, s => s.StartsWith("string.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_boolean_inside_a_comment_is_left_alone()
    {
        var scopes = ScopesOn("-- true", "true");

        Assert.DoesNotContain(SqlGrammarInjection.BooleanScope, scopes);
        Assert.Contains(scopes, s => s.StartsWith("comment.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_word_ending_in_true_is_not_a_boolean()
    {
        Assert.DoesNotContain(SqlGrammarInjection.BooleanScope,
            ScopesOn("select is_true from t", "is_true"));
    }
}
