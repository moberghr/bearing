using System;
using System.Collections.Generic;
using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>What typing a character should do, when auto-close is on.</summary>
public enum AutoCloseAction
{
    /// <summary>Nothing special — let the editor insert the character as it normally would.</summary>
    None,

    /// <summary>Insert the pair and leave the caret between the two halves.</summary>
    Pair,

    /// <summary>The closer is already there: step over it instead of typing a second one.</summary>
    SkipOver,

    /// <summary>Wrap the selection in the pair rather than replacing it.</summary>
    Surround,
}

/// <summary>What to do about one keystroke. <see cref="Text"/> and <see cref="Caret"/> are only meaningful
/// for the actions that insert.</summary>
/// <param name="Action">The behaviour to apply.</param>
/// <param name="Text">Text to put in the document in place of the typed character (or the selection).</param>
/// <param name="Caret">Where the caret lands, as an offset from the start of <paramref name="Text"/>.</param>
public readonly record struct AutoCloseDecision(AutoCloseAction Action, string Text = "", int Caret = 0)
{
    public static readonly AutoCloseDecision None = new(AutoCloseAction.None);
}

/// <summary>
/// The decisions behind auto-closing quotes and brackets (#70): which characters pair, when typing an opener
/// should bring its closer along, when typing a closer should step over one that is already there, and when
/// Backspace should take a whole empty pair.
/// <para>
/// Pure over the buffer text, so the awkward cases are tests rather than things to try in the running app —
/// which matters here because the wiring around it cannot be driven headlessly (§4.5). The behaviour class
/// that uses this stays thin event plumbing.
/// </para>
/// <para>
/// Context comes from the real lexer rather than from counting quotes. A string literal in Postgres can span
/// lines, <c>''</c> is an escaped quote inside one, and <c>$$…$$</c> quotes anything at all; a heuristic gets
/// those wrong, and getting them wrong means auto-close fires in the middle of a literal, where it is pure
/// obstruction.
/// </para>
/// </summary>
public static class AutoClose
{
    /// <summary>The pairs this closes. Deliberately short: the two quote forms Postgres uses for values and
    /// for identifiers, and the paren that wraps everything. Dollar-quoting is a different shape (a
    /// multi-character, user-chosen delimiter) and is left out of a first pass.</summary>
    private static readonly Dictionary<char, char> Pairs = new()
    {
        ['\''] = '\'',
        ['"'] = '"',
        ['('] = ')',
    };

    /// <summary>Token types the caret can be inside where auto-close would be an obstruction rather than a
    /// help: a value, an identifier, or a comment. The unterminated variants matter more than the closed
    /// ones — mid-typing, an opened literal is exactly what the lexer reports.</summary>
    private static readonly HashSet<int> Literals =
    [
        PostgreSQLLexer.StringConstant, PostgreSQLLexer.UnterminatedStringConstant,
        PostgreSQLLexer.EscapeStringConstant, PostgreSQLLexer.UnterminatedEscapeStringConstant,
        PostgreSQLLexer.UnicodeEscapeStringConstant, PostgreSQLLexer.UnterminatedUnicodeEscapeStringConstant,
        PostgreSQLLexer.BinaryStringConstant, PostgreSQLLexer.UnterminatedBinaryStringConstant,
        PostgreSQLLexer.HexadecimalStringConstant, PostgreSQLLexer.UnterminatedHexadecimalStringConstant,
        PostgreSQLLexer.QuotedIdentifier, PostgreSQLLexer.UnterminatedQuotedIdentifier,
        PostgreSQLLexer.UnterminatedUnicodeQuotedIdentifier,
        // Dollar quoting is three tokens: the opening delimiter, the body, and the closing delimiter. The
        // body is the part a caret sits in, so all three have to be here — the opener alone spans only `$$`.
        PostgreSQLLexer.BeginDollarStringConstant, PostgreSQLLexer.DollarText,
        PostgreSQLLexer.EndDollarStringConstant,
        PostgreSQLLexer.LineComment, PostgreSQLLexer.BlockComment, PostgreSQLLexer.UnterminatedBlockComment,
    ];

    /// <summary>Whether <paramref name="c"/> opens a pair this closes.</summary>
    public static bool IsOpener(char c) => Pairs.ContainsKey(c);

    /// <summary>The closer for an opener, or null when it opens nothing.</summary>
    public static char? CloserFor(char c) => Pairs.TryGetValue(c, out var closer) ? closer : null;

    /// <summary>
    /// What typing <paramref name="typed"/> should do, given the buffer and the current selection
    /// (<paramref name="length"/> 0 for a bare caret).
    /// </summary>
    public static AutoCloseDecision ForTyped(string text, int start, int length, char typed)
    {
        text ??= "";
        start = Clamp(start, 0, text.Length);
        length = Clamp(length, 0, text.Length - start);

        // With text selected, an opener wraps it. That is the one case where the selection is not replaced,
        // and it is the reason to check this before anything else.
        if (length > 0)
            return CloserFor(typed) is { } around
                ? new AutoCloseDecision(
                    AutoCloseAction.Surround,
                    typed + text.Substring(start, length) + around,
                    1 + length)
                : AutoCloseDecision.None;

        // Typing the closer that is already under the caret steps over it. Checked before the opener case so
        // that '' and "" — where the two halves are the same character — skip rather than nest.
        if (start < text.Length && text[start] == typed && IsCloser(typed))
            return new AutoCloseDecision(AutoCloseAction.SkipOver);

        if (CloserFor(typed) is not { } closer) return AutoCloseDecision.None;

        // Not inside a literal or a comment, where the character is content rather than syntax.
        if (IsInsideLiteral(text, start)) return AutoCloseDecision.None;

        // And not where a closer would land against something that continues the expression: `(` before an
        // identifier is the user wrapping what follows, not opening an empty pair.
        if (!ClosesCleanlyBefore(text, start)) return AutoCloseDecision.None;

        return new AutoCloseDecision(AutoCloseAction.Pair, string.Concat(typed, closer), 1);
    }

    /// <summary>Whether Backspace at <paramref name="caret"/> should take both halves of an empty pair —
    /// the caret sitting between an opener and its own closer, with nothing between them.</summary>
    public static bool DeletesEmptyPair(string text, int caret)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (caret <= 0 || caret >= text.Length) return false;
        return CloserFor(text[caret - 1]) is { } closer && text[caret] == closer;
    }

    /// <summary>Whether the caret sits just inside a pair whose closer is immediately to its right, ignoring
    /// what is between. Used by the behaviour class to decide whether Enter escapes the pair.</summary>
    public static bool AtCloser(string text, int caret)
    {
        if (string.IsNullOrEmpty(text)) return false;
        caret = Clamp(caret, 0, text.Length);
        return caret < text.Length && IsCloser(text[caret]);
    }

    private static bool IsCloser(char c)
    {
        foreach (var closer in Pairs.Values)
            if (closer == c) return true;
        return false;
    }

    /// <summary>
    /// Whether the offset falls inside a string, a quoted identifier or a comment. Read off the lexer, and
    /// deliberately inclusive of the token's end: with the caret at <c>'abc|</c> the literal is
    /// unterminated and runs to the end of the buffer, which is exactly the case that must not auto-close.
    /// </summary>
    private static bool IsInsideLiteral(string text, int offset)
    {
        if (offset <= 0) return false;
        foreach (var token in PgParsing.LexAll(text))
        {
            if (token.Type == TokenConstants.EOF) break;
            if (token.StartIndex >= offset) break;                 // tokens are in order; past the caret
            if (!Literals.Contains(token.Type)) continue;
            // StopIndex is inclusive, so a caret at StopIndex + 1 has just left the token — except for the
            // closing character itself, which is where typing a quote means "I am done", not "open a pair".
            if (offset <= token.StopIndex + 1) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a closer inserted at <paramref name="offset"/> would sit somewhere sensible. It would not if
    /// the next character continues a name or a literal: typing <c>(</c> before <c>abc</c> is someone
    /// wrapping what is already there, and <c>(|)abc</c> is not what they meant.
    /// </summary>
    private static bool ClosesCleanlyBefore(string text, int offset)
    {
        if (offset >= text.Length) return true;                    // end of buffer
        var next = text[offset];
        if (char.IsWhiteSpace(next)) return true;
        return !char.IsLetterOrDigit(next) && next != '_' && !IsOpener(next) && next != '$';
    }

    private static int Clamp(int value, int min, int max)
        => value < min ? min : value > max ? max : value;
}
