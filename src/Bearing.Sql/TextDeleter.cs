using System;

namespace Bearing.Sql;

/// <summary>The span a delete command should remove. <see cref="Length"/> 0 means "nothing to do".</summary>
public readonly record struct DeleteRange(int Start, int Length)
{
    public static readonly DeleteRange None = new(0, 0);

    public bool IsEmpty => Length <= 0;
}

/// <summary>
/// Readline-style backward deletions for the editor (Ctrl+U, Ctrl+W). Pure: given the buffer and the
/// caret/selection offsets it returns the span to remove, so the caller applies one document edit and
/// keeps undo granular (unlike <see cref="LineCommenter"/>, which rewrites the whole buffer).
/// <para>
/// Both operations are deliberately <b>line-local</b> — neither can join lines, so a stray Ctrl+W at
/// column 0 is a no-op rather than a silent merge with the line above.
/// </para>
/// </summary>
public static class TextDeleter
{
    /// <summary>
    /// Ctrl+U — delete backwards to the first column of the line (readline <c>unix-line-discard</c>).
    /// Column 0 is the stop, not the indentation, so the behavior doesn't depend on how a line is
    /// formatted. With a selection the whole selection goes too: the span runs from the start of the
    /// line the selection begins on to the selection's end, so nothing partial is ever left behind.
    /// A caret already at column 0 (with no selection) deletes nothing.
    /// </summary>
    public static DeleteRange ToLineStart(string text, int selStart, int selEnd)
    {
        if (string.IsNullOrEmpty(text)) return DeleteRange.None;
        var (from, to) = Span(text, selStart, selEnd);

        var start = LineStartOf(text, from);
        return to > start ? new DeleteRange(start, to - start) : DeleteRange.None;
    }

    /// <summary>
    /// Ctrl+W — delete the whitespace-delimited word before the caret (readline
    /// <c>unix-word-rubout</c>): any whitespace immediately behind the caret is consumed first, then
    /// everything back to the previous whitespace. Whitespace-delimited, not identifier-delimited, so
    /// <c>public.orders</c> dies whole — that is what makes this worth having next to Avalonia's
    /// built-in Ctrl+Backspace, which stops at word characters. (A quoted identifier with a space in it
    /// is the trade-off: <c>"Mixed Case"</c> takes two presses.) Inside a line's leading indentation it
    /// deletes just that indentation; with a selection it deletes the selection.
    /// </summary>
    public static DeleteRange WordBefore(string text, int selStart, int selEnd)
    {
        if (string.IsNullOrEmpty(text)) return DeleteRange.None;
        var (from, to) = Span(text, selStart, selEnd);
        if (to > from) return new DeleteRange(from, to - from); // a selection is its own answer

        var caret = from;
        var lineStart = LineStartOf(text, caret);
        var i = caret;
        while (i > lineStart && char.IsWhiteSpace(text[i - 1])) i--;
        // Caret sat in the leading indent (or at column 0): stop here rather than crossing the newline.
        if (i > lineStart)
            while (i > lineStart && !char.IsWhiteSpace(text[i - 1])) i--;

        return caret > i ? new DeleteRange(i, caret - i) : DeleteRange.None;
    }

    /// <summary>Clamped, ordered (start, end) — equal offsets mean a bare caret.</summary>
    private static (int From, int To) Span(string text, int selStart, int selEnd)
    {
        selStart = Math.Clamp(selStart, 0, text.Length);
        selEnd = Math.Clamp(selEnd, 0, text.Length);
        return (Math.Min(selStart, selEnd), Math.Max(selStart, selEnd));
    }

    /// <summary>Offset of the first character of the line containing <paramref name="offset"/>.</summary>
    private static int LineStartOf(string text, int offset)
    {
        if (offset <= 0) return 0;
        var nl = text.LastIndexOf('\n', offset - 1);
        return nl < 0 ? 0 : nl + 1;
    }
}
