using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The properties that have to hold for <b>every</b> input, not the layout for a chosen few. #102's standard
/// is that a formatter which is slightly wrong is worse than none, because it rewrites SQL you then run
/// against production — so these are the tests that matter, and the golden layout in
/// <see cref="SqlFormatTests"/> is the negotiable part.
/// <para>
/// Each property runs over <see cref="Corpus"/>, which is deliberately stocked with the Postgres shapes
/// generic formatters get wrong: multi-character operators, dollar-quoted bodies, escape strings, casts,
/// arrays and comments.
/// </para>
/// </summary>
public class SqlFormatSafetyTests
{
    /// <summary>The SQL every property below is run against — see <see cref="SqlFormatCorpus"/>.</summary>
    public static IEnumerable<object[]> Corpus()
        => SqlFormatCorpus.All().Select(sql => new object[] { sql });

    /// <summary>
    /// The core invariant: formatting changes whitespace and keyword case, and nothing else. Every
    /// identifier, literal, operator and comment comes through identical, in the same order.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void Formatting_preserves_every_token(string sql)
    {
        var formatted = SqlFormat.Format(sql);
        Assert.False(formatted.Refused, formatted.Refusal);

        var before = Tokens(sql);
        var after = Tokens(formatted.Text);
        Assert.Equal(before.Count, after.Count);
        foreach (var ((wasType, wasText), (isType, isText)) in before.Zip(after))
        {
            Assert.Equal(wasType, isType);
            // Case may move only on the keywords the formatter is allowed to touch.
            if (SqlFormatKeywords.Uppercased.Contains(wasType))
                Assert.Equal(wasText, isText, ignoreCase: true);
            else
                Assert.Equal(wasText, isText);
        }
    }

    /// <summary>Formatting a second time must change nothing. A formatter that keeps moving text is one you
    /// cannot put in a save hook, and drifting output makes every diff noise.</summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void Formatting_is_idempotent(string sql)
    {
        var once = SqlFormat.Format(sql);
        Assert.False(once.Refused, once.Refusal);

        var twice = SqlFormat.Format(once.Text);
        Assert.False(twice.Refused, twice.Refusal);
        Assert.Equal(once.Text, twice.Text);
        Assert.False(twice.Changed);
    }

    /// <summary>Whatever the layout did, the result must still parse — and parse without the formatter's
    /// help, since that is how it will be run.</summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void Formatted_sql_still_parses(string sql)
    {
        var formatted = SqlFormat.Format(sql);
        Assert.False(formatted.Refused, formatted.Refusal);

        // Re-formatting is a parse: a refusal here would mean the output no longer reads as SQL.
        Assert.False(SqlFormat.Format(formatted.Text).Refused);
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Every_comment_survives_with_its_text_intact(string sql)
    {
        var formatted = SqlFormat.Format(sql);
        Assert.False(formatted.Refused, formatted.Refusal);
        Assert.Equal(Comments(sql), Comments(formatted.Text));
    }

    /// <summary>A dollar-quoted body is one token, so it comes through byte for byte — including the spacing
    /// and quoting inside it, which is not SQL at all and must never be treated as such.</summary>
    [Theory]
    [InlineData("select $$ a '' \" quoted $$ from t", "$$ a '' \" quoted $$")]
    [InlineData("select $tag$ nested $$ inside $tag$ from t", "$tag$ nested $$ inside $tag$")]
    [InlineData("create function f() returns int as $b$ select  1 ; $b$ language sql", "$b$ select  1 ; $b$")]
    public void A_dollar_quoted_body_is_untouched(string sql, string body)
        => Assert.Contains(body, SqlFormat.Format(sql).Text, StringComparison.Ordinal);

    /// <summary>
    /// Splitting a multi-character operator is the classic way a formatter silently produces SQL that no
    /// longer runs — <c>a # >> b</c>, <c>b | | c</c>, <c>e &lt; @ f</c>. It cannot happen here, because each
    /// of these is a single lexer token rather than a run of characters, and the writer only ever chooses
    /// the whitespace between tokens.
    /// <para>
    /// The list is enumerated from Postgres's operator table rather than from anything the formatter itself
    /// declares. That is deliberate: a test generated from the same list the code works off can only ever
    /// confirm what someone already remembered, and an omission stays invisible to it.
    /// </para>
    /// </summary>
    [Theory]
    // json / jsonb
    [InlineData("->")] [InlineData("->>")] [InlineData("#>")] [InlineData("#>>")] [InlineData("#-")]
    [InlineData("@>")] [InlineData("<@")] [InlineData("?|")] [InlineData("?&")]
    // pattern matching (single-character ~ is covered by the corpus; this theory is about splitting)
    [InlineData("~*")] [InlineData("!~")] [InlineData("!~*")]
    [InlineData("~~")] [InlineData("~~*")] [InlineData("!~~")] [InlineData("!~~*")]
    // comparison, bitwise, arithmetic, text search
    [InlineData("<>")] [InlineData("!=")] [InlineData(">=")] [InlineData("<=")]
    [InlineData("<<")] [InlineData(">>")] [InlineData("||")] [InlineData("@@")]
    [InlineData("::")]
    public void A_multi_character_operator_is_never_split(string op)
    {
        // Both spaced and unspaced in the source: the unspaced form is where a naive tokenizer breaks, and
        // the spaced form is where a naive "tighten everything" rule would merge it into its neighbour.
        foreach (var sql in new[] { $"select a {op} b from t", $"select a{op}b from t" })
        {
            var result = SqlFormat.Format(sql);
            Assert.False(result.Refused, $"{sql}: {result.Refusal}");
            Assert.Contains(op, result.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(string.Join(" ", op.ToCharArray()), result.Text, StringComparison.Ordinal);
        }
    }

    /// <summary>Prefix operators have no left operand, so a rule that reasoned "operator, therefore infix"
    /// would put a space where the operand belongs.</summary>
    [Theory]
    [InlineData("select |/ 25 from t", "|/")]
    [InlineData("select ||/ 27 from t", "||/")]
    [InlineData("select @ -5 from t", "@")]
    public void A_prefix_operator_survives(string sql, string op)
        => Assert.Contains(op, SqlFormat.Format(sql).Text, StringComparison.Ordinal);

    /// <summary>
    /// Line endings follow the file. Emitting LF into a CRLF buffer would rewrite every line of the
    /// statement, turning a formatting change into a whole-file diff for anyone on Windows — which is most
    /// of the people this feature is for.
    /// </summary>
    [Fact]
    public void Crlf_input_stays_crlf_and_lf_stays_lf()
    {
        var crlf = SqlFormat.Format("select a, b\r\nfrom t\r\nwhere c = 1");
        Assert.False(crlf.Refused);
        Assert.Contains("\r\n", crlf.Text, StringComparison.Ordinal);
        Assert.False(HasLoneLf(crlf.Text), "a CRLF buffer came back with bare LF line endings");

        var lf = SqlFormat.Format("select a, b\nfrom t\nwhere c = 1");
        Assert.False(lf.Refused);
        Assert.Contains("\n", lf.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", lf.Text, StringComparison.Ordinal);
    }

    /// <summary>Whether the text holds an LF that is not the tail of a CRLF pair.</summary>
    private static bool HasLoneLf(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n' && (i == 0 || text[i - 1] != '\r'))
                return true;
        return false;
    }

    /// <summary>
    /// A Postgres 16 hexadecimal literal is refused rather than formatted, and this pins that it stays a
    /// <b>refusal</b> rather than becoming a corruption.
    /// <para>
    /// The cause is in the vendored grammar, not here: <c>PostgreSQLLexer.g4</c> declares
    /// <c>HexadecimalIntegral: '0x' Digits</c> over <c>Digits: [0-9]+</c>, so <c>0x19</c> lexes and
    /// <c>0x1f</c> cannot — the <c>f</c> is not a decimal digit. <c>0x1f</c> then comes through as <c>0</c>
    /// followed by an identifier <c>x1f</c>, and the parse fails at the cast after it.
    /// </para>
    /// <para>
    /// Worth a test of its own because the safe outcome is not the obvious one: the formatter must decline
    /// the whole statement, not lay out the wreckage of a mis-lex. If the grammar is ever updated, this test
    /// fails and the entry belongs back in <see cref="Corpus"/>.
    /// </para>
    /// </summary>
    [Fact]
    public void A_hex_literal_the_grammar_cannot_lex_is_refused_not_mangled()
    {
        const string sql = "select 0x1f::text from t";
        var result = SqlFormat.Format(sql);

        Assert.True(result.Refused);
        Assert.Equal(sql, result.Text);
        // The digits-only form the grammar does accept still formats, which is what shows the limit is the
        // grammar's hex rule rather than hex literals as such.
        Assert.False(SqlFormat.Format("select 0x19::text from t").Refused);
    }

    // ---- the guard itself --------------------------------------------------------------------------

    /// <summary>
    /// The guard is the last line of defence, so it needs its own tests: a guard that passes everything
    /// would leave the formatter looking safe while doing nothing of the kind.
    /// </summary>
    [Theory]
    [InlineData("select a from t", "select b from t", "an identifier changed")]
    [InlineData("select 'x' from t", "select 'y' from t", "a literal changed")]
    [InlineData("select a from t", "select a from t where 1 = 1", "tokens appeared")]
    [InlineData("select a, b from t", "select a from t", "tokens went missing")]
    [InlineData("select a #>> b from t", "select a # >> b from t", "an operator was split")]
    [InlineData("select a from t -- note", "select a from t", "a comment was dropped")]
    public void The_guard_catches_a_changed_statement(string before, string after, string why)
        => Assert.True(SqlFormatGuard.Difference(before, after) is not null, $"the guard missed: {why}");

    [Theory]
    [InlineData("select a from t", "SELECT a FROM t")]
    [InlineData("select a from t", "SELECT\n    a\nFROM t")]
    [InlineData("select a from t where b and c", "SELECT a FROM t WHERE b AND c")]
    public void The_guard_allows_whitespace_and_keyword_case(string before, string after)
        => Assert.Null(SqlFormatGuard.Difference(before, after));

    /// <summary>Case is only ever cosmetic for an unquoted identifier, but a quoted one is a different name
    /// — the guard must not wave that through.</summary>
    [Fact]
    public void The_guard_does_not_allow_a_quoted_identifier_to_change_case()
        => Assert.NotNull(SqlFormatGuard.Difference("select \"Mixed\" from t", "select \"MIXED\" from t"));

    // ---- helpers -----------------------------------------------------------------------------------

    private static List<(int Type, string Text)> Tokens(string sql)
        => PgParsing.LexAll(sql)
            .Where(t => t.Type != Antlr4.Runtime.TokenConstants.EOF && !IsWhitespace(t.Type))
            .Select(t => (t.Type, t.Text))
            .ToList();

    private static List<string> Comments(string sql)
        => PgParsing.LexAll(sql)
            .Where(t => t.Type is PostgreSQLLexer.LineComment or PostgreSQLLexer.BlockComment)
            .Select(t => t.Text)
            .ToList();

    private static bool IsWhitespace(int type)
        => type is PostgreSQLLexer.Whitespace or PostgreSQLLexer.Newline;
}
