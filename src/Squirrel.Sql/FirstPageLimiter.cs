using System;
using System.Collections.Generic;
using Antlr4.Runtime;

namespace Squirrel.Sql;

/// <summary>
/// Decides whether a top-level row limit can be appended to a query so the first page can be fetched
/// server-side — the server then produces only ~one page instead of computing and streaming the whole
/// result set for the client to read a few rows of and discard (slow on a remote server, and the drain
/// isn't even reflected in the reported query time). Safe only for a single read-only, row-returning
/// statement that has no row cap of its own; every other shape falls back to the caller's unbounded
/// execution (which is capped client-side). Pure and lexer-based (reuses <see cref="PgParsing"/>).
/// </summary>
public static class FirstPageLimiter
{
    // Only these can lead a plain row-returning read we can safely suffix with LIMIT. Deliberately
    // conservative: TABLE/VALUES/EXPLAIN/SHOW etc. fall back to unbounded rather than risk a bad suffix.
    private static readonly HashSet<string> RowReturningStarts =
        new(StringComparer.OrdinalIgnoreCase) { "SELECT", "WITH" };

    // A top-level occurrence of any of these means we must NOT append a limit:
    //   LIMIT/FETCH — the query already caps its own rows (a second clause is a syntax error);
    //   FOR         — a locking clause (FOR UPDATE/SHARE) must follow LIMIT, so a suffix is invalid;
    //   INTO        — SELECT … INTO is a table-creating write, not a read — limiting it would still
    //                 create the table (with N rows). Leave it to the caller / write-guard.
    private static readonly HashSet<string> BlockingKeywords =
        new(StringComparer.OrdinalIgnoreCase) { "LIMIT", "FETCH", "FOR", "INTO" };

    /// <summary>
    /// Returns <paramref name="sql"/> with <c>limit &lt;limit&gt;</c> appended when it is safe to do so,
    /// or <c>null</c> when the caller should execute the original SQL unbounded. The clause is placed on
    /// its own line so a trailing line-comment (<c>-- …</c>) can't swallow it.
    /// </summary>
    public static string? TryAppendLimit(string sql, int limit) => TryAppendPage(sql, 0, limit);

    /// <summary>
    /// Returns <paramref name="sql"/> with a top-level <c>limit &lt;limit&gt; offset &lt;offset&gt;</c>
    /// appended when it is safe to do so, or <c>null</c> otherwise. Used to page a query with the same
    /// top-level suffix on every page (offset 0 for the first) — so a query's <c>ORDER BY</c> is honored
    /// consistently across pages, unlike a subquery wrap whose ordering the planner needn't preserve. The
    /// clause goes on its own line so a trailing line-comment can't swallow it; <c>offset 0</c> is omitted
    /// so a first page reads identically to <see cref="TryAppendLimit"/>.
    /// </summary>
    public static string? TryAppendPage(string sql, int offset, int limit)
    {
        if (string.IsNullOrWhiteSpace(sql) || limit <= 0 || offset < 0) return null;
        if (!CanSuffixLimit(sql)) return null;

        var clause = offset > 0 ? $"limit {limit} offset {offset}" : $"limit {limit}";
        return $"{StripTrailingSemicolon(sql)}\n{clause}";
    }

    /// <summary>True when a top-level LIMIT/OFFSET can be safely suffixed to <paramref name="sql"/>:
    /// a single read-only, row-returning statement with no row cap / locking / INTO of its own.</summary>
    private static bool CanSuffixLimit(string sql)
    {
        // A batch would bind the limit to the wrong (last) statement — only a lone statement qualifies.
        if (StatementSplitter.Split(sql).Count != 1) return false;

        // Never reshape a write / destructive DDL; this also catches data-modifying CTEs.
        if (WriteGuard.HasRisk(sql)) return false;

        string? firstKeyword = null;
        var depth = 0;
        foreach (var t in PgParsing.LexAll(sql))
        {
            if (t.Type == TokenConstants.EOF || t.Channel != TokenConstants.DefaultChannel) continue;
            if (t.Text is not { Length: > 0 } text) continue;

            firstKeyword ??= text;

            if (t.Type == PostgreSQLLexer.OPEN_PAREN) depth++;
            else if (t.Type == PostgreSQLLexer.CLOSE_PAREN && depth > 0) depth--;
            else if (depth == 0 && BlockingKeywords.Contains(text)) return false;
        }

        return firstKeyword is not null && RowReturningStarts.Contains(firstKeyword);
    }

    private static string StripTrailingSemicolon(string sql)
    {
        var s = sql.TrimEnd();
        return s.EndsWith(';') ? s[..^1] : s;
    }
}
