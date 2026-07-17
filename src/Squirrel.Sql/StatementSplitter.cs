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

        // First on-channel token index of each statement (statements are delimited by SEMI).
        var starts = new List<int>();
        int? firstStart = null;
        foreach (var t in PgParsing.LexAll(sql))
        {
            if (t.Channel != TokenConstants.DefaultChannel || t.Type == TokenConstants.EOF) continue;
            firstStart ??= t.StartIndex;
            if (t.Type == PostgreSQLLexer.SEMI)
            {
                starts.Add(firstStart.Value);
                firstStart = null;
            }
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
