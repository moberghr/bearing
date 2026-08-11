using System;
using System.Collections.Generic;
using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// One statement of a batch as the write-guard sees it: the statement text as written, its leading keyword,
/// and the risky verbs found in it (empty for a plain read). Lets a caller show the user the batch it is
/// about to run, not just the verdict — <see cref="WriteGuard.FindRiskyStatements"/> is this same scan
/// projected down to the distinct risky verbs.
/// </summary>
/// <param name="Text">The statement as written (trimmed), comments and formatting intact.</param>
/// <param name="Verb">Leading keyword, uppercased (<c>SELECT</c>, <c>WITH</c>, <c>DELETE</c>, …).</param>
/// <param name="RiskyVerbs">Risky verbs found in this statement, first-seen order. Empty = reads only.</param>
public sealed record StatementRisk(string Text, string Verb, IReadOnlyList<string> RiskyVerbs)
{
    /// <summary>True when this statement writes data or alters schema.</summary>
    public bool IsRisky => RiskyVerbs.Count > 0;

    /// <summary>How to label the statement: the risky verbs when it writes, else its leading keyword.</summary>
    public string Label => IsRisky ? string.Join(" + ", RiskyVerbs) : Verb;
}

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
        // Data writes.
        "INSERT", "UPDATE", "DELETE", "MERGE", "COPY",
        // Schema / object DDL (CREATE covers CREATE TABLE … AS SELECT; REFRESH covers materialized views).
        "CREATE", "DROP", "TRUNCATE", "ALTER", "REFRESH",
        // Privilege changes.
        "GRANT", "REVOKE",
        // Procedural blocks that can write arbitrarily.
        "CALL", "DO",
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
        foreach (var statement in Describe(sql))
            foreach (var verb in statement.RiskyVerbs)
                Add(found, verb);
        return found;
    }

    /// <summary>Every statement in the batch, in execution order, each tagged with the risky verbs it
    /// carries — so a confirmation can show what is about to run and which parts of it write. Reads are
    /// included (with an empty <see cref="StatementRisk.RiskyVerbs"/>): the batch runs them too.</summary>
    public static IReadOnlyList<StatementRisk> Describe(string sql)
    {
        var described = new List<StatementRisk>();
        if (string.IsNullOrWhiteSpace(sql)) return described;

        foreach (var span in StatementSplitter.Split(sql))
        {
            var keywords = OnChannelKeywords(span.Text);
            if (keywords.Count == 0) continue;

            var first = keywords[0];
            var risky = new List<string>();
            if (Risky.Contains(first))
            {
                Add(risky, first); // a risky lead verb settles it — no need to scan the interior
            }
            else
            {
                // Only scan the interior of preamble statements; a plain SELECT that merely mentions an
                // identifier called "update" must not trip the guard.
                if (Preambles.Contains(first))
                    foreach (var kw in keywords)
                        if (Risky.Contains(kw)) { Add(risky, kw); break; }

                // `SELECT … INTO tbl` (and `WITH … SELECT … INTO`) creates a table — a write that no
                // leading verb reveals. Detect a top-level INTO for select-shaped statements only, so a
                // subquery's `INTO` (PL/pgSQL, not top-level SQL) or nested selects don't false-positive.
                if ((first.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || Preambles.Contains(first))
                    && HasTopLevelInto(span.Text))
                    Add(risky, "SELECT INTO");
            }

            described.Add(new StatementRisk(span.Text.Trim(), first.ToUpperInvariant(), risky));
        }
        return described;
    }

    /// <summary>True if the statement has an <c>INTO</c> keyword at paren depth 0 (the SELECT-INTO target).</summary>
    private static bool HasTopLevelInto(string statement)
    {
        var depth = 0;
        foreach (var t in PgParsing.LexAll(statement))
        {
            if (t.Type == TokenConstants.EOF || t.Channel != TokenConstants.DefaultChannel) continue;
            if (t.Type == PostgreSQLLexer.OPEN_PAREN) depth++;
            else if (t.Type == PostgreSQLLexer.CLOSE_PAREN && depth > 0) depth--;
            else if (depth == 0 && t.Text is { Length: > 0 } text
                     && text.Equals("INTO", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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
