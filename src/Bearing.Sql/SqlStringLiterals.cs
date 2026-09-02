using System.Collections.Generic;
using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// Whether an offset falls inside a single-quoted string literal.
/// <para>
/// Lexer-based, like <see cref="WriteGuard"/> and <see cref="SqlRedactor"/> and for the same reason: a regex
/// cannot tell an apostrophe that opens a string from one inside a dollar-quoted body, or from the doubled
/// <c>''</c> that escapes a quote within a string. Getting that wrong here would suppress completion for the
/// rest of the buffer.
/// </para>
/// <para>
/// <b>Single quotes only.</b> A double-quoted span in Postgres is a quoted <i>identifier</i> — the way you
/// name a table with a capital or a space — so it is exactly where table and column names belong, and
/// completion stays on inside it. Only <c>'text'</c> is data, and nothing in the catalog or the grammar
/// belongs there.
/// </para>
/// </summary>
public static class SqlStringLiterals
{
    /// <summary>
    /// Every token type the lexer uses for a single-quoted literal, including the unterminated forms — a
    /// buffer being typed into is unterminated most of the time, which is the state that matters here.
    /// </summary>
    private static readonly HashSet<int> Types =
    [
        PostgreSQLLexer.StringConstant, PostgreSQLLexer.UnterminatedStringConstant,
        PostgreSQLLexer.EscapeStringConstant, PostgreSQLLexer.UnterminatedEscapeStringConstant,
        PostgreSQLLexer.UnicodeEscapeStringConstant, PostgreSQLLexer.UnterminatedUnicodeEscapeStringConstant,
        PostgreSQLLexer.BinaryStringConstant, PostgreSQLLexer.UnterminatedBinaryStringConstant,
        PostgreSQLLexer.HexadecimalStringConstant, PostgreSQLLexer.UnterminatedHexadecimalStringConstant,
    ];

    /// <summary>
    /// Whether <paramref name="offset"/> sits within a single-quoted literal, or within a dollar-quoted body.
    /// </summary>
    /// <remarks>
    /// The opening quote itself does not count, so completion still works at the position where the literal
    /// begins — the caret is not inside anything yet. Everything after it does, up to and including the
    /// position just past a closing quote's content.
    /// </remarks>
    public static bool Contains(string sql, int offset)
    {
        if (string.IsNullOrEmpty(sql)) return false;
        if (offset <= 0 || offset > sql.Length) return false;

        var inDollar = false;
        foreach (var token in PgParsing.LexAll(sql))
        {
            if (token.Type == TokenConstants.EOF) break;

            // A dollar-quoted body is a run of arbitrary tokens between two markers, so it is tracked as a
            // state rather than matched as one token — the same shape SqlRedactor uses.
            if (token.Type == PostgreSQLLexer.BeginDollarStringConstant)
            {
                inDollar = true;
                if (Inside(token, offset)) return true;
                continue;
            }
            if (inDollar)
            {
                if (token.Type == PostgreSQLLexer.EndDollarStringConstant) inDollar = false;
                if (Inside(token, offset)) return true;
                continue;
            }

            if (Types.Contains(token.Type) && Inside(token, offset)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the offset is strictly past the token's first character and no further than one past its
    /// last. The opening quote is excluded so the caret arriving at a fresh <c>'</c> is still "outside";
    /// the end is inclusive because a caret typing at the end of an unterminated literal sits there.
    /// </summary>
    private static bool Inside(IToken token, int offset)
        => offset > token.StartIndex && offset <= token.StopIndex + 1;
}
