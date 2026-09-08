using System.Collections.Generic;
using System.Linq;
using Bearing.Core.Workspace;
using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The formatter's dials: keyword case and indent width. Two things to establish — that each option does
/// what it says, and that none of them weakens the safety properties, which is why the corpus is run again
/// under non-default settings rather than only under the shipped ones.
/// </summary>
public class SqlFormatOptionsTests
{
    private const string Sql = "select id, name from users where id = 1";

    private static string Format(string sql, SqlFormatOptions options)
    {
        var result = SqlFormat.Format(sql, options);
        Assert.Null(result.Refusal);
        return result.Text;
    }

    // ---- keyword case ------------------------------------------------------------------------------

    [Fact]
    public void Upper_is_the_default_and_what_no_options_means()
    {
        var expected = """
            SELECT
                id,
                name
            FROM users
            WHERE id = 1
            """;
        Assert.Equal(expected, Format(Sql, SqlFormatOptions.Default));
        Assert.Equal(expected, SqlFormat.Format(Sql).Text);
        Assert.Equal(SqlKeywordCase.Upper, SqlFormatOptions.Default.KeywordCase);
    }

    [Fact]
    public void Lower_lowercases_the_keywords_and_nothing_else()
        => Assert.Equal(
            """
            select
                id,
                name
            from users
            where id = 1
            """,
            Format("SELECT ID, Name FROM users WHERE id = 1".Replace("ID, Name", "id, name"),
                new SqlFormatOptions { KeywordCase = SqlKeywordCase.Lower }));

    /// <summary>Preserve is layout only: every keyword comes back exactly as typed, however inconsistent.</summary>
    [Fact]
    public void Preserve_leaves_every_keyword_as_it_was_typed()
        => Assert.Equal(
            """
            SeLeCt
                id,
                name
            frOM users
            WHERe id = 1
            """,
            Format("SeLeCt id, name frOM users WHERe id = 1",
                new SqlFormatOptions { KeywordCase = SqlKeywordCase.Preserve }));

    /// <summary>An identifier's case is the user's under every setting — this is the one thing case could
    /// actually change, for a quoted name, and it is never touched.</summary>
    [Theory]
    [InlineData(SqlKeywordCase.Upper)]
    [InlineData(SqlKeywordCase.Lower)]
    [InlineData(SqlKeywordCase.Preserve)]
    public void Identifiers_keep_their_case_whatever_the_setting(SqlKeywordCase keywordCase)
    {
        var formatted = Format("select \"MixedCase\", PlainName from \"Quoted Table\"",
            new SqlFormatOptions { KeywordCase = keywordCase });

        Assert.Contains("\"MixedCase\"", formatted);
        Assert.Contains("PlainName", formatted);
        Assert.Contains("\"Quoted Table\"", formatted);
    }

    // ---- indent width ------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, " ")]
    [InlineData(2, "  ")]
    [InlineData(4, "    ")]
    [InlineData(8, "        ")]
    public void Indent_width_sets_the_step_per_level(int width, string indent)
    {
        var formatted = Format(Sql, new SqlFormatOptions { IndentWidth = width });
        Assert.Contains($"\n{indent}id,", formatted);
        Assert.DoesNotContain($"\n{indent} id,", formatted);   // not one level deeper
    }

    /// <summary>Nested levels are multiples of the step, so a subquery at depth two is two steps in.</summary>
    [Fact]
    public void Nesting_multiplies_the_step()
    {
        var formatted = Format("select * from (select id from users) u",
            new SqlFormatOptions { IndentWidth = 2 });
        Assert.Contains("\n  SELECT\n    id\n  FROM users\n) u", formatted);
    }

    /// <summary>Clamped rather than trusted: this is arithmetic in the writer, and a zero would silently
    /// produce output with no structure while a negative would throw far from the setting that caused it.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(999, 16)]
    public void Indent_width_is_clamped(int given, int expected)
        => Assert.Equal(expected, new SqlFormatOptions { IndentWidth = given }.IndentWidth);

    // ---- the safety properties still hold under every setting ---------------------------------------

    public static IEnumerable<object[]> CorpusUnderOptions()
        => SqlFormatCorpus.All().SelectMany(sql => new[]
        {
            new object[] { sql, SqlKeywordCase.Lower, 2 },
            new object[] { sql, SqlKeywordCase.Preserve, 8 },
        });

    /// <summary>
    /// The invariant that matters, re-run under non-default settings. A dial that could break token
    /// preservation would be a dial that can corrupt SQL, so the options are not allowed to be a path the
    /// safety argument does not cover.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusUnderOptions))]
    public void Every_token_survives_under_any_setting(string sql, SqlKeywordCase keywordCase, int indent)
    {
        var options = new SqlFormatOptions { KeywordCase = keywordCase, IndentWidth = indent };

        var result = SqlFormat.Format(sql, options);
        Assert.False(result.Refused, result.Refusal);

        var before = Tokens(sql);
        var after = Tokens(result.Text);
        Assert.Equal(before.Count, after.Count);
        foreach (var ((wasType, wasText), (isType, isText)) in before.Zip(after))
        {
            Assert.Equal(wasType, isType);
            if (keywordCase != SqlKeywordCase.Preserve && SqlFormatKeywords.Uppercased.Contains(wasType))
                Assert.Equal(wasText, isText, ignoreCase: true);
            else
                Assert.Equal(wasText, isText);   // Preserve may not move so much as a letter
        }

        // And still idempotent — a dial that made the output drift would make every diff noise.
        var again = SqlFormat.Format(result.Text, options);
        Assert.False(again.Refused, again.Refusal);
        Assert.Equal(result.Text, again.Text);
    }

    private static List<(int Type, string Text)> Tokens(string sql)
        => PgParsing.LexAll(sql)
            .Where(t => t.Type != Antlr4.Runtime.TokenConstants.EOF
                        && t.Type is not (PostgreSQLLexer.Whitespace or PostgreSQLLexer.Newline))
            .Select(t => (t.Type, t.Text))
            .ToList();
}
