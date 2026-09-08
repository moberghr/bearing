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
    /// <summary>SQL that must survive formatting unharmed. Every entry parses — an entry that does not would
    /// be silently refused and would prove nothing.</summary>
    public static IEnumerable<object[]> Corpus() => new[]
    {
        "select id, name from users where id = 1",
        "select o.id from orders o join customers c on c.id = o.customer_id where o.total > 100 and c.active",
        "with a as (select 1 as x), b as (select x from a) select * from b join a on a.x = b.x",
        "select * from (select id from users where active) u where u.id > 5",
        "select a from t1 union all select a from t2 order by a limit 10 offset 5",
        "insert into t (a, b) values (1, 2), (3, 4) on conflict (a) do update set b = excluded.b returning *",
        "insert into t (a) select x from other where x is not null",
        "update t set a = 1, b = 2 where id = 3 returning id",
        "delete from t using u where t.id = u.id returning *",
        "select count(*) filter (where status = 'x') as n, sum(amt) from t group by a having count(*) > 1",
        "select row_number() over (partition by a order by b desc) from t",
        "select * from orders o, lateral (select 1 from items i where i.o = o.id) s",

        // Multi-character operators: the exact thing a regex tokenizer splits and thereby corrupts.
        "select a #>> '{x}', b -> 'k', c ->> 'k', d #- '{y}' from t",
        "select a || ' ' || b from t where c <@ d and e @> f and g ?| array['h'] and i ?& array['j']",
        "select a from t where b ~* 'pat' and c !~ 'other' and d is distinct from e",
        "select a <> b, c >= d, e <= f, g << h, i >> j from t",

        // Literals and quoting that must come through byte for byte.
        "select $$ a '' \" quoted $$ from t",
        "select $tag$ nested $$ inside $tag$ from t",
        "select E'line\\nbreak', U&'\\0041', 'it''s' from t",
        "select '{\"k\": [1, 2]}'::jsonb -> 'k' from t",
        "select a::text, b::numeric(10, 2), cast(c as int) from t",
        "select array[1, 2, 3], x[1], y[1:2] from t",
        "select \"MixedCase\", \"with space\" from \"Quoted Table\"",
        "select 1.5, 1e10, .5, 42, -1 from t",

        // Comments in every position that matters.
        "-- leading\nselect a from t",
        "select a, /* between */ b from t",
        "select a from t -- trailing\nwhere a = 1",
        "select a from t /* multi\nline\ncomment */ where a = 1",
        "select a -- one\n, b -- two\nfrom t",

        // Shapes with no layout rules: these must come back untouched, which is itself a property.
        "create table foo (\n    id int primary key,\n    name text\n)",
        "create function f() returns int as $$ begin return 1; end $$ language plpgsql",
        "explain analyze select a from t",

        // Batches, mixed recognised and not.
        "select 1; select 2",
        "create index i on t (a);\nselect a from t",
        "select a from t;\n",
    }.Select(sql => new object[] { sql });

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

    /// <summary>The failure that disqualified the off-the-shelf library: it emitted <c>a # >> b</c>,
    /// <c>b | | c</c> and <c>e &lt; @ f</c>, none of which run.</summary>
    [Theory]
    [InlineData("select a #>> '{x}' from t", "#>>")]
    [InlineData("select a || b from t", "||")]
    [InlineData("select a from t where b <@ c", "<@")]
    [InlineData("select a from t where b @> c", "@>")]
    [InlineData("select a ->> 'k' from t", "->>")]
    [InlineData("select a::text from t", "::")]
    public void A_multi_character_operator_is_never_split(string sql, string op)
    {
        var formatted = SqlFormat.Format(sql).Text;
        Assert.Contains(op, formatted, StringComparison.Ordinal);
        // And not with a gap opened up inside it.
        Assert.DoesNotContain(string.Join(" ", op.ToCharArray()), formatted, StringComparison.Ordinal);
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
