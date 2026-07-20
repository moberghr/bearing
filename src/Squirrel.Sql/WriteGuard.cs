using System;
using System.Collections.Generic;
using Antlr4.Runtime;

namespace Squirrel.Sql;

/// <summary>
/// Classifies a (possibly multi-statement) SQL batch for statements that write data or alter schema,
/// so a caller can require confirmation before running them against a guarded connection. Pure and
/// lexer-based (reuses <see cref="PgParsing"/>); errs toward caution — a WITH/EXPLAIN preamble is
/// scanned through so a data-modifying CTE or <c>EXPLAIN ANALYZE DELETE …</c> is not missed.
/// </summary>
public static class WriteGuard
{
    private static readonly HashSet<string> Risky = new(StringComparer.OrdinalIgnoreCase)
    {
        // Writes.
        "INSERT", "UPDATE", "DELETE", "MERGE",
        // Destructive DDL.
        "DROP", "TRUNCATE", "ALTER",
    };

    // A statement whose first meaningful keyword is one of these may still hide a risky verb further
    // in (a data-modifying CTE, or EXPLAIN ANALYZE which actually executes its inner statement).
    private static readonly HashSet<string> Preambles = new(StringComparer.OrdinalIgnoreCase)
    {
        "WITH", "EXPLAIN",
    };

    /// <summary>Distinct risky verbs found in the batch (e.g. ["DELETE", "DROP"]), in first-seen order.
    /// Empty when nothing writes data or alters schema.</summary>
    public static IReadOnlyList<string> FindRiskyStatements(string sql)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(sql)) return found;

        foreach (var span in StatementSplitter.Split(sql))
        {
            var keywords = OnChannelKeywords(span.Text);
            if (keywords.Count == 0) continue;

            var first = keywords[0];
            if (Risky.Contains(first)) { Add(found, first); continue; }

            // Only scan the interior of preamble statements; a plain SELECT that merely mentions an
            // identifier called "update" must not trip the guard.
            if (Preambles.Contains(first))
                foreach (var kw in keywords)
                    if (Risky.Contains(kw)) { Add(found, kw); break; }
        }
        return found;
    }

    /// <summary>True when the batch contains at least one write or destructive-DDL statement.</summary>
    public static bool HasRisk(string sql) => FindRiskyStatements(sql).Count > 0;

    private static void Add(List<string> found, string verb)
    {
        var upper = verb.ToUpperInvariant();
        if (!found.Contains(upper)) found.Add(upper);
    }

    /// <summary>The uppercased text of each on-channel (non-comment/whitespace) token in a statement.</summary>
    private static List<string> OnChannelKeywords(string statement)
    {
        var list = new List<string>();
        foreach (var t in PgParsing.LexAll(statement))
        {
            if (t.Type == TokenConstants.EOF || t.Channel != TokenConstants.DefaultChannel) continue;
            if (t.Text is { Length: > 0 } text) list.Add(text);
        }
        return list;
    }
}
