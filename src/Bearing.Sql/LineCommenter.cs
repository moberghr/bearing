using System;
using System.Collections.Generic;
using System.Linq;

namespace Bearing.Sql;

/// <summary>The buffer and selection after toggling line comments.</summary>
public readonly record struct CommentToggleResult(string Text, int SelectionStart, int SelectionLength);

/// <summary>
/// Toggles SQL line comments (<c>-- </c>) over the lines a selection (or caret) touches — the
/// editor's Ctrl+/ command. If every non-blank covered line is already commented it uncomments;
/// otherwise it comments, inserting at the common (minimum) indentation column so the markers align.
/// </summary>
public static class LineCommenter
{
    public static CommentToggleResult Toggle(string sql, int selStart, int selEnd)
    {
        sql ??= "";
        if (sql.Length == 0) return new CommentToggleResult(sql, 0, 0);
        selStart = Math.Clamp(selStart, 0, sql.Length);
        selEnd = Math.Clamp(selEnd, 0, sql.Length);
        var selMin = Math.Min(selStart, selEnd);
        var selMax = Math.Max(selStart, selEnd);
        var hadSelection = selMin != selMax;

        var firstLineStart = LineStartOf(sql, selMin);
        // A selection ending exactly at a line start doesn't really include that (empty) next line.
        var endProbe = hadSelection && selMax > 0 && sql[selMax - 1] == '\n' ? selMax - 1 : selMax;
        var lastLineStart = LineStartOf(sql, endProbe);

        // Collect the covered lines (content only, newline excluded).
        var lines = new List<(int Start, string Content)>();
        var pos = firstLineStart;
        while (pos <= lastLineStart)
        {
            var nl = sql.IndexOf('\n', pos);
            var contentEnd = nl < 0 ? sql.Length : nl;
            lines.Add((pos, sql[pos..contentEnd]));
            if (nl < 0) break;
            pos = nl + 1;
        }

        var nonBlank = lines.Where(l => l.Content.Trim().Length > 0).ToList();
        if (nonBlank.Count == 0)
            return new CommentToggleResult(sql, selStart, selEnd - selStart); // nothing to toggle

        var commenting = nonBlank.Any(l => !l.Content.TrimStart().StartsWith("--", StringComparison.Ordinal));
        var minIndent = nonBlank.Min(l => l.Content.Length - l.Content.TrimStart().Length);

        var transformed = lines
            .Select(l => l.Content.Trim().Length == 0 ? l.Content : Transform(l.Content, commenting, minIndent))
            .ToList();

        var regionEnd = lines[^1].Start + lines[^1].Content.Length;
        var transformedBlock = string.Join("\n", transformed);
        var text = sql[..firstLineStart] + transformedBlock + sql[regionEnd..];

        if (hadSelection)
            return new CommentToggleResult(text, firstLineStart, transformedBlock.Length);

        // Caret-only: keep it a caret, remapped within its single (unblank) line.
        var line = lines[0];
        var caretCol = selMin - line.Start;
        var newCol = RemapCaretColumn(line.Content, caretCol, commenting, minIndent);
        return new CommentToggleResult(text, line.Start + newCol, 0);
    }

    private static string Transform(string content, bool commenting, int minIndent)
    {
        if (commenting)
            return content[..minIndent] + "-- " + content[minIndent..];

        var indent = content.Length - content.TrimStart().Length;
        var removeLen = indent + 2 < content.Length && content[indent + 2] == ' ' ? 3 : 2;
        return content[..indent] + content[(indent + removeLen)..];
    }

    private static int RemapCaretColumn(string content, int caretCol, bool commenting, int minIndent)
    {
        if (content.Trim().Length == 0) return caretCol;
        if (commenting)
            return caretCol >= minIndent ? caretCol + 3 : caretCol;

        var indent = content.Length - content.TrimStart().Length;
        var removeLen = indent + 2 < content.Length && content[indent + 2] == ' ' ? 3 : 2;
        if (caretCol <= indent) return caretCol;
        return caretCol >= indent + removeLen ? caretCol - removeLen : indent;
    }

    /// <summary>Offset of the first character of the line containing <paramref name="offset"/>.</summary>
    private static int LineStartOf(string sql, int offset)
    {
        var nl = sql.LastIndexOf('\n', Math.Max(0, offset - 1));
        return nl < 0 || offset == 0 ? 0 : nl + 1;
    }
}
