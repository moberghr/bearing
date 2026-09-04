using System;
using System.Collections.Generic;
using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// One statement of a batch as the write-guard sees it: the statement text as written, its leading keyword,
/// and the risky verbs found in it (empty for a plain read). Lets a caller show the user the batch it is
/// about to run, not just the verdict — <see cref="WriteGuard.FindRiskyStatements(string)"/> is this same
/// scan projected down to the distinct risky verbs.
/// </summary>
/// <param name="Text">The statement as written (trimmed), comments and formatting intact.</param>
/// <param name="Verb">Leading keyword, uppercased (<c>SELECT</c>, <c>WITH</c>, <c>DELETE</c>, …).</param>
/// <param name="RiskyVerbs">Risky verbs found in this statement, first-seen order. Empty = reads only.</param>
/// <param name="GuardIsDialectAware">
/// False when the guard could not actually read this engine's grammar
/// (<see cref="ISqlDialect.HasDialectAwareGuard"/>). The statement is then risky whatever the scan found,
/// because "found nothing" did not mean "there is nothing" — see <see cref="IsRisky"/>.
/// </param>
public sealed record StatementRisk(
    string Text, string Verb, IReadOnlyList<string> RiskyVerbs, bool GuardIsDialectAware = true)
{
    /// <summary>Why a read is being confirmed, appended to its <see cref="Label"/> so the prompt can say
    /// what it is unsure of instead of implying the statement writes.</summary>
    public const string UnparsedDialectNote = "dialect not parsed";

    /// <summary>True when this statement writes data or alters schema — or when the guard cannot read
    /// this dialect and therefore cannot say that it doesn't.</summary>
    public bool IsRisky => RiskyVerbs.Count > 0 || !GuardIsDialectAware;

    /// <summary>How to label the statement: the risky verbs when the scan found some, else its leading
    /// keyword — qualified with <see cref="UnparsedDialectNote"/> when it is only being confirmed
    /// because the guard doesn't understand the engine.</summary>
    public string Label => RiskyVerbs.Count > 0
        ? string.Join(" + ", RiskyVerbs)
        : GuardIsDialectAware ? Verb : $"{Verb} — {UnparsedDialectNote}";
}

/// <summary>
/// Classifies a (possibly multi-statement) SQL batch for statements that write data or alter schema,
/// so a caller can require confirmation before running them against a guarded connection. Pure and
/// lexer-based (reuses <see cref="PgParsing"/>); errs toward caution — a WITH/EXPLAIN preamble is
/// scanned through so a data-modifying CTE or <c>EXPLAIN ANALYZE DELETE …</c> is not missed.
/// <para>
/// The engine decides how much of that is trustworthy. <see cref="ISqlDialect.RiskyVerbs"/> supplies the
/// verbs, and <see cref="ISqlDialect.HasDialectAwareGuard"/> says whether the lexer under this scan
/// actually reads the dialect. When it does not, the guard fails safe: every statement comes back risky,
/// labelled with why, so a confirmation can be honest about being conservative rather than accuse a
/// SELECT of writing (§1.2 — the guard is never narrower for any dialect).
/// </para>
/// <para>
/// The dialect-less overloads are the Postgres-bound entry points and behave exactly as they always have.
/// </para>
/// </summary>
public static class WriteGuard
{
    // A statement whose first meaningful keyword is one of these may still hide a risky verb further
    // in (a data-modifying CTE, or EXPLAIN ANALYZE which actually executes its inner statement).
    private static readonly HashSet<string> Preambles = new(StringComparer.OrdinalIgnoreCase)
    {
        "WITH", "EXPLAIN",
    };

    /// <summary>Distinct risky verbs found in the batch (e.g. ["DELETE", "DROP"]), in first-seen order.
    /// Empty when nothing writes data or alters schema.</summary>
    public static IReadOnlyList<string> FindRiskyStatements(string sql)
        => FindRiskyStatements(PostgresDialect.Instance, sql);

    /// <summary>
    /// Distinct risky verbs the scan <em>found</em>, in first-seen order. Note the difference from
    /// <see cref="HasRisk(ISqlDialect, string)"/> under a dialect the guard cannot read: there is no verb
    /// to name for a statement that is only risky because nothing could be ruled out, so this list can be
    /// empty for a batch that must still be confirmed. Ask <see cref="HasRisk(ISqlDialect, string)"/> or
    /// <see cref="StatementRisk.IsRisky"/> for the verdict; ask this only to name what was found.
    /// </summary>
    public static IReadOnlyList<string> FindRiskyStatements(ISqlDialect dialect, string sql)
    {
        var found = new List<string>();
        foreach (var statement in Describe(dialect, sql))
            foreach (var verb in statement.RiskyVerbs)
                Add(found, verb);
        return found;
    }

    /// <summary>Every statement in the batch, in execution order, each tagged with the risky verbs it
    /// carries — so a confirmation can show what is about to run and which parts of it write. Reads are
    /// included (with an empty <see cref="StatementRisk.RiskyVerbs"/>): the batch runs them too.</summary>
    public static IReadOnlyList<StatementRisk> Describe(string sql)
        => Describe(PostgresDialect.Instance, sql);

    /// <inheritdoc cref="Describe(string)"/>
    public static IReadOnlyList<StatementRisk> Describe(ISqlDialect dialect, string sql)
    {
        var described = dialect.DescribeStatements(sql);
        if (dialect.HasDialectAwareGuard) return described;

        // The safety net (§1.2). A dialect that admits its scanner cannot read the engine has every
        // statement re-tagged risky, whatever the scan claimed to find: "found nothing" did not mean
        // "there is nothing". Both shipped dialects read their own engine, so this is the path a
        // future provider takes before its scanner exists — not a path either takes today.
        var failSafe = new List<StatementRisk>(described.Count);
        foreach (var s in described) failSafe.Add(s with { GuardIsDialectAware = false });
        return failSafe;
    }

    /// <summary>
    /// The PostgreSQL scan, reached through <see cref="PostgresDialect.DescribeStatements"/>. Its
    /// behaviour is deliberately untouched by the arrival of a second dialect — every pre-existing
    /// WriteGuard test asserts exactly this.
    /// </summary>
    internal static IReadOnlyList<StatementRisk> DescribeWithPostgresLexer(
        string sql, IReadOnlySet<string> risky)
    {
        var described = new List<StatementRisk>();
        if (string.IsNullOrWhiteSpace(sql)) return described;

        foreach (var span in StatementSplitter.Split(sql))
        {
            var keywords = OnChannelKeywords(span.Text);
            if (keywords.Count == 0) continue;

            var first = keywords[0];
            var found = new List<string>();
            if (risky.Contains(first))
            {
                Add(found, first); // a risky lead verb settles it — no need to scan the interior
            }
            else
            {
                // Only scan the interior of preamble statements; a plain SELECT that merely mentions an
                // identifier called "update" must not trip the guard.
                if (Preambles.Contains(first))
                    foreach (var kw in keywords)
                        if (risky.Contains(kw)) { Add(found, kw); break; }

                // `SELECT … INTO tbl` (and `WITH … SELECT … INTO`) creates a table — a write that no
                // leading verb reveals. Detect a top-level INTO for select-shaped statements only, so a
                // subquery's `INTO` (PL/pgSQL, not top-level SQL) or nested selects don't false-positive.
                if ((first.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || Preambles.Contains(first))
                    && HasTopLevelInto(span.Text))
                    Add(found, "SELECT INTO");
            }

            described.Add(new StatementRisk(span.Text.Trim(), first.ToUpperInvariant(), found));
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
    public static bool HasRisk(string sql) => HasRisk(PostgresDialect.Instance, sql);

    /// <summary>True when at least one statement in the batch must be confirmed — a found write, or any
    /// statement at all when the guard cannot read <paramref name="dialect"/>.</summary>
    public static bool HasRisk(ISqlDialect dialect, string sql)
    {
        foreach (var statement in Describe(dialect, sql))
            if (statement.IsRisky) return true;
        return false;
    }

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
