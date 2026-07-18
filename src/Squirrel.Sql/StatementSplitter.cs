using System;
using System.Collections.Generic;
using Antlr4.Runtime;

namespace Squirrel.Sql;

/// <summary>A single top-level SQL statement and the span it occupies in the original buffer.</summary>
public sealed record StatementSpan(int Start, int End, string Text)
{
    /// <summary>Start offset of the first non-whitespace character (for tight highlighting).</summary>
    public int TrimmedStart => Start + (Text.Length - Text.TrimStart().Length);

    /// <summary>End offset just past the last non-whitespace character.</summary>
    public int TrimmedEnd => Start + Text.TrimEnd().Length;
}

/// <summary>
/// Splits a SQL buffer into top-level statements on unquoted semicolons — using the PostgreSQL
/// lexer, so semicolons inside strings, comments, or dollar-quoted bodies never split — and
/// locates the statement under a caret. Powers "run the statement at the caret" and scopes
/// completion to the current statement instead of the whole buffer.
/// </summary>
public static class StatementSplitter
{
    public static IReadOnlyList<StatementSpan> Split(string sql)
    {
        var spans = new List<StatementSpan>();
        if (string.IsNullOrEmpty(sql)) return spans;

        // First index of each statement. Statements are delimited by SEMI or, at paren depth 0, by a
        // blank line between tokens (a common "run this block" convention). A comment block sitting
        // directly above a statement (no blank line between them) is a leading comment and starts
        // that statement, so it groups with the query it documents rather than the previous one.
        var starts = new List<int>();
        int? firstStart = null;
        IToken? prev = null;                 // last on-channel token
        int? commentBlockStart = null;       // start of the current contiguous comment block
        var lastCommentEnd = -1;
        var depth = 0;
        foreach (var t in PgParsing.LexAll(sql))
        {
            if (t.Type == TokenConstants.EOF) continue;

            if (t.Channel != TokenConstants.DefaultChannel)
            {
                // Only own-line comments can lead a statement; a comment sharing a line with the
                // previous token trails that statement (e.g. "select 1; -- note").
                if (IsComment(t) && (prev is null || ContainsNewline(sql, prev.StopIndex + 1, t.StartIndex)))
                {
                    // A blank line ends the previous comment block; start a fresh one.
                    if (commentBlockStart is null || HasBlankLine(sql, lastCommentEnd + 1, t.StartIndex))
                        commentBlockStart = t.StartIndex;
                    lastCommentEnd = t.StopIndex;
                }
                continue;
            }

            // A leading comment block counts only when it abuts this token (no blank line between).
            int? leading = commentBlockStart is { } cbs && !HasBlankLine(sql, lastCommentEnd + 1, t.StartIndex)
                ? cbs
                : null;

            if (firstStart is not null && prev is not null && depth == 0
                && HasBlankLine(sql, prev.StopIndex + 1, t.StartIndex))
            {
                starts.Add(firstStart.Value);   // a blank line ended the previous statement
                firstStart = null;
            }

            if (t.Type == PostgreSQLLexer.OPEN_PAREN) depth++;
            else if (t.Type == PostgreSQLLexer.CLOSE_PAREN && depth > 0) depth--;

            firstStart ??= leading ?? t.StartIndex;
            commentBlockStart = null;           // consumed (or not leading) — don't leak forward
            if (t.Type == PostgreSQLLexer.SEMI)
            {
                starts.Add(firstStart.Value);
                firstStart = null;
            }
            prev = t;
        }
        if (firstStart is not null) starts.Add(firstStart.Value); // trailing statement, no ';'
        if (starts.Count == 0) return spans;                       // only whitespace/comments

        // Tile the buffer: statement i owns from its own start (0 for the first, so leading
        // whitespace stays with it) up to the next statement's first token. The ';' and any blank
        // lines after it therefore belong to the statement they follow, not the next one.
        for (var i = 0; i < starts.Count; i++)
        {
            var start = i == 0 ? 0 : starts[i];
            var end = i == starts.Count - 1 ? sql.Length : starts[i + 1];
            spans.Add(new StatementSpan(start, end, sql[start..end]));
        }
        return spans;
    }

    /// <summary>True for a line (<c>--</c>) or block (<c>/* */</c>) comment token.</summary>
    private static bool IsComment(IToken t)
        => t.Text is { } text && (text.StartsWith("--") || text.StartsWith("/*"));

    /// <summary>True if the text in [from,to) contains at least one newline.</summary>
    private static bool ContainsNewline(string sql, int from, int to)
    {
        if (from < 0 || to > sql.Length || from >= to) return false;
        return sql.IndexOf('\n', from, to - from) >= 0;
    }

    /// <summary>True if the text in [from,to) spans a blank line (≥2 newlines) — an empty line gap.</summary>
    private static bool HasBlankLine(string sql, int from, int to)
    {
        if (from < 0 || to > sql.Length || from >= to) return false;
        var newlines = 0;
        for (var i = from; i < to; i++)
            if (sql[i] == '\n' && ++newlines >= 2) return true;
        return false;
    }

    /// <summary>
    /// The statement the caret sits in. The caret belongs to a statement through its terminating
    /// <c>;</c> and any trailing blank lines, only switching to the next statement once it reaches
    /// that statement's first character. Null when the buffer has nothing runnable.
    /// </summary>
    public static StatementSpan? StatementAt(string sql, int caret)
    {
        var spans = Split(sql);
        if (spans.Count == 0) return null;
        caret = Math.Clamp(caret, 0, sql.Length);

        for (var i = 0; i < spans.Count; i++)
        {
            // TrimmedEnd sits just past the statement's last real char (its ';'); everything up to
            // it — and the whitespace gap after — stays with statement i.
            if (caret <= spans[i].TrimmedEnd) return spans[i];
            if (i == spans.Count - 1) return spans[i];       // trailing whitespace at EOF
            if (caret < spans[i + 1].Start) return spans[i]; // in the gap before the next statement
        }
        return spans[^1];
    }
}
