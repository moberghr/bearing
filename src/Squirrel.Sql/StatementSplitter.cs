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

            // A blank line only ends the previous statement when this token actually begins a new one.
            // Otherwise a statement wrapped across a blank line — e.g. a WHERE continued by "and", or a
            // trailing "order by" / "union" — would be mis-split into a fragment that fails to parse.
            if (firstStart is not null && prev is not null && depth == 0
                && HasBlankLine(sql, prev.StopIndex + 1, t.StartIndex)
                && StartsStatement(t) && !EndsWithSetOperator(prev))
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

    /// <summary>Keywords that can begin a top-level SQL statement — used to decide whether a blank line
    /// separates two statements or merely wraps one. Conservative: an unknown leader keeps the lines
    /// together (recoverable with an explicit <c>;</c>) rather than risking a false split.</summary>
    private static readonly HashSet<string> StatementStartKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "insert", "update", "delete", "merge", "with", "values", "table",
        "create", "alter", "drop", "truncate", "comment", "rename",
        "grant", "revoke",
        "begin", "start", "commit", "end", "rollback", "savepoint", "release", "abort",
        "set", "reset", "show",
        "explain", "analyze", "vacuum", "cluster", "reindex", "checkpoint",
        "copy", "call", "do",
        "declare", "fetch", "move", "close",
        "listen", "notify", "unlisten",
        "prepare", "execute", "deallocate",
        "lock", "refresh", "discard", "import", "load",
    };

    /// <summary>Whether a token could be the first token of a new statement: a statement-starting
    /// keyword, or an open paren (a parenthesized <c>(SELECT …) UNION …</c> query).</summary>
    private static bool StartsStatement(IToken t)
        => t.Type == PostgreSQLLexer.OPEN_PAREN
           || (t.Text is { } text && StatementStartKeywords.Contains(text));

    /// <summary>Whether a token is a set operator (<c>union</c>/<c>intersect</c>/<c>except</c>) — which
    /// can never end a complete statement, so a following blank line is a continuation, not a boundary.</summary>
    private static bool EndsWithSetOperator(IToken t)
        => t.Text is { } text
           && (text.Equals("union", StringComparison.OrdinalIgnoreCase)
               || text.Equals("intersect", StringComparison.OrdinalIgnoreCase)
               || text.Equals("except", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the last token of <paramref name="fragment"/> is a line comment (<c>-- …</c>),
    /// which runs to end of line and would swallow a <c>;</c> appended on the same line.</summary>
    private static bool EndsWithLineComment(string fragment)
    {
        IToken? last = null;
        foreach (var t in PgParsing.LexAll(fragment))
            if (t.Type != TokenConstants.EOF) last = t;
        return last?.Text is { } text && text.StartsWith("--", StringComparison.Ordinal);
    }

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

    /// <summary>
    /// Re-join a (possibly multi-statement) block so every statement is semicolon-terminated. Lets a
    /// selection of blank-line-separated statements — where the user relied on the blank line, not a
    /// <c>;</c> — run as a proper batch instead of one malformed command. A single statement is
    /// returned unchanged (no semicolon forced onto a lone run-at-caret).
    /// </summary>
    public static string EnsureSeparated(string sql)
    {
        var spans = Split(sql);
        if (spans.Count <= 1) return sql;

        var parts = new List<string>();
        foreach (var span in spans)
        {
            var t = span.Text.Trim();
            while (t.EndsWith(";", StringComparison.Ordinal)) t = t[..^1].TrimEnd();
            if (t.Length > 0) parts.Add(t);
        }
        if (parts.Count == 0) return sql;

        // Terminate each part with ';'. A part ending in a trailing "-- comment" needs the ';' on a
        // fresh line, or the comment (which runs to end of line) would swallow it and merge statements.
        var terminated = new List<string>(parts.Count);
        foreach (var p in parts)
            terminated.Add(EndsWithLineComment(p) ? p + "\n;" : p + ";");
        return string.Join("\n", terminated);
    }
}
