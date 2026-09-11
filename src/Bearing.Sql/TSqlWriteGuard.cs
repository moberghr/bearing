namespace Bearing.Sql;

/// <summary>
/// The write guard for T-SQL: <see cref="WriteGuard"/>'s counterpart, built on
/// <see cref="TSqlScanner"/> rather than on the vendored PostgreSQL grammar.
/// <para>
/// It exists because the alternative was worse in both directions. Reusing the PG lexer risked
/// <em>under</em>-reporting — a batch it could not tokenize would have read as safe on a connection the
/// user marked guarded, silently disarming §1.2 — and the fail-safe answer to that (treat everything as
/// risky) made a guarded SQL Server connection confirm on every <c>SELECT</c>, which is correct but
/// unusable.
/// </para>
/// <para>
/// <b>Conservative by construction.</b> It classifies on bare words only, so a table called
/// <c>[delete]</c>, a string containing <c>'drop table'</c> or a variable named <c>@update</c> cannot trip
/// it; and anything it cannot classify counts as risky, never as safe. It is still not a parser — the
/// vendored T-SQL grammar exists now but is not used here, deliberately: a guard that needs a successful
/// parse has a failure mode where it cannot answer, and the answer §1.2 wants in that case is "risky",
/// which a token scan gives without the round trip (see <see cref="TSqlScanner"/>). So it errs the way the
/// Postgres guard does: a preamble is scanned through, and a shape it does not recognise is confirmed
/// rather than waved past.
/// </para>
/// </summary>
public static class TSqlWriteGuard
{
    /// <summary>
    /// A statement whose first word is one of these may still hide a risky verb further in — a
    /// data-modifying CTE is the case that matters (<c>with x as (…) delete from …</c>), and T-SQL allows
    /// <c>INSERT</c>/<c>UPDATE</c>/<c>DELETE</c>/<c>MERGE</c> against a CTE.
    /// </summary>
    private static readonly HashSet<string> Preambles =
        new(StringComparer.OrdinalIgnoreCase) { "WITH" };

    /// <summary>
    /// Words that lead a plain read. A statement starting with anything <em>else</em> that is not a known
    /// risky verb is reported risky anyway — that is the conservative half: <c>SET</c>, <c>DECLARE</c>,
    /// <c>IF</c>, <c>WHILE</c>, <c>BEGIN</c>, a bare <c>EXEC</c>-less procedure call, or a T-SQL construct
    /// nobody here thought of, all confirm rather than slip through.
    /// </summary>
    private static readonly HashSet<string> ReadStarts =
        new(StringComparer.OrdinalIgnoreCase) { "SELECT", "WITH", "PRINT", "USE", "SHOWPLAN_ALL" };

    /// <summary>Every statement in the batch, in execution order, tagged with the risky verbs it carries.
    /// Mirrors <see cref="WriteGuard.Describe(string)"/>'s contract exactly, so the two are interchangeable
    /// behind <see cref="ISqlDialect"/>.</summary>
    public static IReadOnlyList<StatementRisk> Describe(string sql, IReadOnlySet<string> riskyVerbs)
    {
        var described = new List<StatementRisk>();
        if (string.IsNullOrWhiteSpace(sql)) return described;

        foreach (var statement in TSqlScanner.Split(sql))
        {
            var words = TSqlScanner.AllWords(statement.Tokens);
            if (words.Count == 0) continue;

            var first = words[0];
            var risky = new List<string>();

            if (riskyVerbs.Contains(first))
            {
                Add(risky, first);      // a risky lead verb settles it
            }
            else
            {
                // A preamble's interior can hide the write (a data-modifying CTE).
                if (Preambles.Contains(first))
                    foreach (var w in words)
                        if (riskyVerbs.Contains(w)) { Add(risky, w); break; }

                // SELECT … INTO creates a table — a write no leading verb reveals. Top level only, so a
                // subquery's INTO or a PL-style INTO cannot false-positive.
                if (ReadStarts.Contains(first) && TSqlScanner.TopLevelWords(statement.Tokens).Contains("INTO"))
                    Add(risky, "SELECT INTO");

                // The conservative default: a statement this guard does not recognise as a read is treated
                // as a write. EXEC of a procedure that writes, a DECLARE/SET batch, an IF block — none can
                // be read without a parser, and on a guarded connection the wrong answer must be "confirm",
                // never "run it".
                if (risky.Count == 0 && !ReadStarts.Contains(first))
                    Add(risky, first);
            }

            described.Add(new StatementRisk(statement.Text, first, risky));
        }
        return described;
    }

    private static void Add(List<string> found, string verb)
    {
        var upper = verb.ToUpperInvariant();
        if (!found.Contains(upper)) found.Add(upper);
    }
}
