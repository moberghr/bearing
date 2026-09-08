using System;
using System.Collections.Generic;
using Antlr4.Runtime;
using Bearing.Core.Workspace;

namespace Bearing.Sql;

/// <summary>
/// Checks that formatted SQL is the same SQL: lex both texts and compare the token sequences, ignoring
/// whitespace and allowing only the keyword case the formatter is allowed to change.
/// <para>
/// This is the invariant the whole feature rests on, and it is deliberately stronger and cheaper than
/// "format, re-parse, compare the trees". Stronger, because two different statements can share a tree shape
/// while differing in a literal, and a token comparison catches a changed literal, a dropped comment and a
/// split operator alike. Cheaper, because lexing is linear and parsing this grammar is not.
/// </para>
/// <para>
/// It runs on every format, not only in tests. A formatter that can detect its own corruption and hand back
/// the original is one you can point at production; one that is merely well tested is not, because the
/// input that breaks it is by definition the one nobody wrote a test for.
/// </para>
/// </summary>
internal static class SqlFormatGuard
{
    /// <summary>Null when the two texts are the same statement; otherwise what differs, for the refusal.</summary>
    /// <param name="keywordCase">What the formatter was allowed to do to case. Under
    /// <see cref="SqlKeywordCase.Preserve"/> nothing may differ at all, so the check tightens to exact text
    /// on every token — a stricter guard for the stricter setting, rather than one that always allows the
    /// loosest thing any setting permits.</param>
    public static string? Difference(string before, string after, SqlKeywordCase keywordCase = SqlKeywordCase.Upper)
    {
        var original = Significant(before);
        var formatted = Significant(after);

        for (var i = 0; i < Math.Min(original.Count, formatted.Count); i++)
        {
            var (wasType, wasText) = original[i];
            var (isType, isText) = formatted[i];

            if (wasType != isType)
                return $"token {i + 1} changed from {Describe(wasType, wasText)} to {Describe(isType, isText)}";

            // Case may differ only where the formatter is allowed to change it. Everything else — every
            // identifier, literal, operator and comment — must be byte-identical.
            var recasable = keywordCase != SqlKeywordCase.Preserve
                            && SqlFormatKeywords.Uppercased.Contains(wasType);
            var sameText = recasable
                ? string.Equals(wasText, isText, StringComparison.OrdinalIgnoreCase)
                : string.Equals(wasText, isText, StringComparison.Ordinal);

            if (!sameText) return $"token {i + 1} changed from {Describe(wasType, wasText)} to {Describe(isType, isText)}";
        }

        if (original.Count != formatted.Count)
            return original.Count > formatted.Count
                ? $"{original.Count - formatted.Count} token(s) went missing"
                : $"{formatted.Count - original.Count} token(s) appeared";

        return null;
    }

    /// <summary>Every token that carries meaning — comments included, since losing one is exactly the kind
    /// of silent damage this exists to catch. Only whitespace is dropped.</summary>
    private static List<(int Type, string Text)> Significant(string sql)
    {
        var significant = new List<(int, string)>();
        foreach (var token in PgParsing.LexAll(sql))
        {
            if (token.Type == TokenConstants.EOF) break;
            if (token.Type is PostgreSQLLexer.Whitespace or PostgreSQLLexer.Newline) continue;
            significant.Add((token.Type, token.Text));
        }
        return significant;
    }

    private static string Describe(int type, string text)
    {
        var trimmed = text.Length <= 24 ? text : text[..21] + "...";
        return $"\"{trimmed}\"";
    }
}
