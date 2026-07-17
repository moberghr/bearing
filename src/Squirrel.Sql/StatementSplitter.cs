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

        var start = 0;
        foreach (var t in PgParsing.LexAll(sql))
        {
            if (t.Type != PostgreSQLLexer.SEMI) continue;
            var end = t.StopIndex + 1; // include the semicolon in the statement it terminates
            spans.Add(new StatementSpan(start, end, sql[start..end]));
            start = end;
        }

        if (start < sql.Length)
            spans.Add(new StatementSpan(start, sql.Length, sql[start..]));

        return spans;
    }

    /// <summary>
    /// The statement the caret sits in. A caret exactly on an interior boundary belongs to the
    /// following statement; if the chosen segment is blank (e.g. trailing whitespace after the
    /// last <c>;</c>), falls back to the nearest non-blank statement. Null when nothing runnable.
    /// </summary>
    public static StatementSpan? StatementAt(string sql, int caret)
    {
        var spans = Split(sql);
        if (spans.Count == 0) return null;
        caret = Math.Clamp(caret, 0, sql.Length);

        var idx = -1;
        for (var i = 0; i < spans.Count; i++)
        {
            var last = i == spans.Count - 1;
            if (caret >= spans[i].Start && (last ? caret <= spans[i].End : caret < spans[i].End))
            {
                idx = i;
                break;
            }
        }
        if (idx < 0) idx = spans.Count - 1;

        if (!string.IsNullOrWhiteSpace(spans[idx].Text)) return spans[idx];
        for (var i = idx - 1; i >= 0; i--)
            if (!string.IsNullOrWhiteSpace(spans[i].Text)) return spans[i];
        for (var i = idx + 1; i < spans.Count; i++)
            if (!string.IsNullOrWhiteSpace(spans[i].Text)) return spans[i];
        return null;
    }
}
