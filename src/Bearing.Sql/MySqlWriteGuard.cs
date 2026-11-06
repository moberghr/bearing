namespace Bearing.Sql;

/// <summary>
/// The write guard for MySQL and MariaDB, over <see cref="MySqlScanner"/> — the counterpart of
/// <see cref="WriteGuard"/>'s Postgres scan and <see cref="TSqlWriteGuard"/>, with the same contract and the
/// same conservative stance (§1.2): it classifies on bare words only, so a <c>`delete`</c> column, a string
/// containing <c>'drop table'</c> or a variable called <c>@update</c> cannot trip it, and anything it cannot
/// recognise as a read is confirmed rather than waved through.
/// <para>
/// Unlike T-SQL, MySQL requires a delimiter between statements, so a statement's lead word does describe it
/// — <c>select 1 drop table t</c> is a syntax error here, not a SELECT followed by a DROP. The scan of a
/// statement's interior is therefore reserved for the leads that can genuinely hide a write: a CTE, and the
/// <c>EXPLAIN</c> / <c>DESCRIBE</c> family, which in MySQL 8 (<c>EXPLAIN ANALYZE</c>) and MariaDB
/// (<c>ANALYZE</c>, which is not a read start here at all) can execute what they describe.
/// </para>
/// </summary>
public static class MySqlWriteGuard
{
    /// <summary>Leads whose interior is scanned for a risky verb.</summary>
    private static readonly HashSet<string> Preambles = new(StringComparer.OrdinalIgnoreCase)
    {
        "WITH", "EXPLAIN", "DESCRIBE", "DESC",
    };

    /// <summary>
    /// Leads of a plain read. Anything else that is not a known risky verb is still reported risky — the
    /// conservative default: <c>SET</c>, <c>PREPARE</c>, <c>HANDLER</c>, <c>LOCK</c>, <c>ANALYZE</c>, or a
    /// statement nobody here thought of, all confirm rather than slip through.
    /// <para>
    /// <c>TABLE t</c> and a standalone <c>VALUES ROW(…)</c> are MySQL 8's; <c>HELP</c> reads the server's
    /// help tables; <c>USE</c> changes the default database and writes nothing.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ReadStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "SHOW", "DESCRIBE", "DESC", "EXPLAIN", "TABLE", "VALUES", "HELP", "USE",
    };

    /// <summary>Every statement in the script, in execution order, tagged with the risky verbs it carries.
    /// Mirrors <see cref="WriteGuard.Describe(string)"/>'s contract.</summary>
    public static IReadOnlyList<StatementRisk> Describe(string sql, ISqlDialect dialect)
    {
        var riskyVerbs = dialect.RiskyVerbs;
        var described = new List<StatementRisk>();
        if (string.IsNullOrWhiteSpace(sql))
        {
            return described;
        }

        foreach (var statement in MySqlScanner.Split(sql))
        {
            var words = MySqlScanner.AllWords(statement.Tokens);
            if (words.Count == 0)
            {
                continue;
            }

            var first = words[0];
            var risky = new List<string>();
            var guessed = false;

            if (riskyVerbs.Contains(first))
            {
                Add(risky, first);
            }
            else
            {
                if (Preambles.Contains(first))
                {
                    foreach (var w in words)
                    {
                        if (riskyVerbs.Contains(w) && !IsLockingUpdate(words, w))
                        {
                            Add(risky, w);
                            break;
                        }
                    }
                }

                // SELECT … INTO OUTFILE / DUMPFILE writes a file on the *server's* disk — a write no lead verb
                // reveals. `INTO @var` (a variable) is not one, and is left alone.
                var topLevel = MySqlScanner.TopLevelWords(statement.Tokens);
                for (var i = 0; i + 1 < topLevel.Count; i++)
                {
                    if (topLevel[i] == "INTO" && topLevel[i + 1] is "OUTFILE" or "DUMPFILE")
                    {
                        Add(risky, "SELECT INTO " + topLevel[i + 1]);
                        break;
                    }
                }

                if (risky.Count == 0 && !ReadStarts.Contains(first))
                {
                    Add(risky, first);
                    guessed = true;
                }
            }

            described.Add(new StatementRisk(
                statement.Text, first, risky,
                TransactionControl: dialect.TransactionControl(ControlWords(statement.Tokens)),
                FromConservativeDefault: guessed));
        }
        return described;
    }

    /// <summary>
    /// <c>UPDATE</c> in a CTE's or an EXPLAIN's interior is a write — unless it is the locking clause
    /// <c>FOR UPDATE</c>, which takes a row lock and writes nothing. Without this, every
    /// <c>with … select … for update</c> would be confirmed as an UPDATE.
    /// </summary>
    private static bool IsLockingUpdate(IReadOnlyList<string> words, string verb)
    {
        if (verb != "UPDATE")
        {
            return false;
        }
        var index = -1;
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i] == "UPDATE")
            {
                index = i;
                break;
            }
        }
        // `ON DUPLICATE KEY UPDATE` only follows an INSERT, which is risky in its own right.
        return index > 0 && words[index - 1] == "FOR";
    }

    /// <summary>
    /// The statement's words as <see cref="ISqlDialect.TransactionControl"/> reads them — bare words, plus
    /// system variables with their <c>@@</c> and scope prefix removed, so <c>set @@session.autocommit = 1</c>
    /// is read as the <c>SET AUTOCOMMIT</c> it is.
    /// </summary>
    private static IReadOnlyList<string> ControlWords(IReadOnlyList<MySqlToken> tokens)
    {
        var words = new List<string>();
        foreach (var t in tokens)
        {
            if (t.IsWord)
            {
                words.Add(t.Text.ToUpperInvariant());
            }
            else if (t.Kind == MySqlTokenKind.Variable && t.Text.StartsWith("@@", StringComparison.Ordinal))
            {
                var name = t.Text[2..].ToUpperInvariant();
                var dot = name.LastIndexOf('.');
                words.Add(dot >= 0 ? name[(dot + 1)..] : name);
            }
        }
        return words;
    }

    private static void Add(List<string> found, string verb)
    {
        if (!found.Contains(verb))
        {
            found.Add(verb);
        }
    }
}
