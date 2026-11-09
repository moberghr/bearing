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
    /// <summary>Leads whose interior is scanned for a risky verb. <c>ANALYZE</c> is MariaDB's statement form
    /// too — <c>analyze delete from t</c> runs the DELETE — so the write it carries is named, not only guessed.
    /// <c>ANALYZE TABLE</c> names nothing inside and stays the conservative default.</summary>
    private static readonly HashSet<string> Preambles = new(StringComparer.OrdinalIgnoreCase)
    {
        "WITH", "EXPLAIN", "DESCRIBE", "DESC", "ANALYZE",
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

    /// <summary>
    /// Every statement in the script, in execution order, tagged with the risky verbs it carries.
    /// Mirrors <see cref="WriteGuard.Describe(string)"/>'s contract.
    /// <para>
    /// Read once as the default <c>sql_mode</c> reads it — and, when the batch holds a backslash, again as a
    /// <c>NO_BACKSLASH_ESCAPES</c> or <c>ANSI_QUOTES</c> server would (<see cref="MySqlQuoting"/>). On such a
    /// server <c>select 'a\'; drop table t; -- '</c> runs the DROP, and which mode the server is in is not
    /// something this layer knows. Any statement only another reading sees is appended, so the guard, the
    /// manual-commit check and an exposed connection's allow-list all judge what the server might run —
    /// widening only (§1.2): a batch every reading splits alike is described exactly as before. Appended
    /// statements come after the default reading's, so "execution order" holds for that reading only.
    /// </para>
    /// </summary>
    public static IReadOnlyList<StatementRisk> Describe(string sql, ISqlDialect dialect)
    {
        var described = new List<StatementRisk>();
        if (string.IsNullOrWhiteSpace(sql))
        {
            return described;
        }

        Describe(sql, dialect, MySqlQuoting.Default, described);
        if (!sql.Contains('\\'))
        {
            return described;
        }

        // Keyed by the verdict as well as the text: one statement can split alike under every reading and still
        // read differently — `select 'a\' union select 1 into outfile '/tmp/x' #'` is a plain SELECT by default
        // and writes a file under NO_BACKSLASH_ESCAPES, with no delimiter to move.
        var seen = new HashSet<string>(described.Select(Verdict), StringComparer.Ordinal);
        foreach (var quoting in MySqlQuoting.Alternates)
        {
            var other = new List<StatementRisk>();
            Describe(sql, dialect, quoting, other);
            foreach (var statement in other)
            {
                if (seen.Add(Verdict(statement)))
                {
                    described.Add(statement);
                }
            }
        }
        return described;
    }

    private static string Verdict(StatementRisk s)
        => string.Join('\u0001', s.Text, s.Verb, string.Join(',', s.RiskyVerbs), s.TransactionControl ?? "",
            s.FromConservativeDefault, s.CommitsImplicitly);

    private static void Describe(string sql, ISqlDialect dialect, MySqlQuoting quoting, List<StatementRisk> described)
    {
        var riskyVerbs = dialect.RiskyVerbs;
        foreach (var statement in MySqlScanner.Split(sql, quoting))
        {
            var words = MySqlScanner.AllWords(statement.Tokens);
            if (words.Count == 0)
            {
                continue;
            }

            // A labelled compound (`lbl: begin not atomic …`) is described by the word after its label.
            var lead = IsLabelled(statement.Tokens) && words.Count > 1 ? 1 : 0;
            var first = words[lead];
            var risky = new List<string>();
            var guessed = false;

            if (riskyVerbs.Contains(first))
            {
                Add(risky, first);
                // A risky lead names itself, but a `;` the splitter kept means more statements run with it:
                // `update t set t.case = 1; delete from x` must not be confirmed as the UPDATE alone.
                if (HasAbsorbedDelimiter(statement.Tokens))
                {
                    foreach (var verb in InteriorWrites(statement.Tokens, riskyVerbs))
                    {
                        Add(risky, verb);
                    }
                }
            }
            else
            {
                // A `;` the splitter kept inside the statement means it read a body there. When the lead is not
                // one that owns a body (a column called `begin` opens one too: `select begin from t; delete
                // from t`), the statements after it run all the same, so the interior is scanned as for a
                // compound — widening only (§1.2).
                if (Preambles.Contains(first) || IsCompound(words, lead) || HasAbsorbedDelimiter(statement.Tokens))
                {
                    foreach (var verb in InteriorWrites(statement.Tokens, riskyVerbs))
                    {
                        Add(risky, verb);
                    }
                }

                // SELECT … INTO OUTFILE / DUMPFILE writes a file on the *server's* disk — a write no lead verb
                // reveals. `INTO @var` (a variable) is not one, and is left alone. At any depth: MySQL runs a
                // parenthesised `(select … into dumpfile '…')`, and OUTFILE / DUMPFILE are reserved, so the
                // pair is never a name.
                for (var i = 0; i + 1 < words.Count; i++)
                {
                    if (words[i] == "INTO" && words[i + 1] is "OUTFILE" or "DUMPFILE")
                    {
                        Add(risky, "SELECT INTO " + words[i + 1]);
                        break;
                    }
                }

                if (risky.Count == 0 && !ReadStarts.Contains(first))
                {
                    Add(risky, first);
                    guessed = true;
                }
            }

            var controlWords = ControlWords(statement.Tokens);
            var control = dialect.TransactionControl(controlWords);
            var commitsImplicitly = dialect.CommitsImplicitly(controlWords);
            if (control is null && (IsCompound(words, lead) || HasAbsorbedDelimiter(statement.Tokens)))
            {
                (control, commitsImplicitly) = InteriorControl(statement.Tokens, dialect);
            }
            described.Add(new StatementRisk(
                statement.Text, first, risky,
                TransactionControl: control,
                FromConservativeDefault: guessed,
                CommitsImplicitly: commitsImplicitly));
        }
    }

    /// <summary>
    /// Every risky verb in a statement's interior, in order — each occurrence judged on its own (a body or a
    /// merged span can hold several writes, and the confirmation must name each one): <c>UPDATE</c>
    /// preceded by <c>FOR</c> is the locking clause <c>FOR UPDATE</c>, which takes a row lock and writes
    /// nothing, and is skipped; the scan carries on past it, so a real UPDATE later in the same statement
    /// (<c>with c as (select … for update) update t2 …</c>) is still found. <c>ON DUPLICATE KEY UPDATE</c>
    /// only follows an INSERT, which is named in its own right, and is skipped too. <c>DO</c> is skipped — see below — and so is
    /// a call of one of <see cref="FunctionNamedVerbs"/>.
    /// </summary>
    private static IEnumerable<string> InteriorWrites(IReadOnlyList<MySqlToken> tokens, IReadOnlySet<string> riskyVerbs)
    {
        string? previous = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].IsWord)
            {
                continue;
            }
            var word = tokens[i].Text.ToUpperInvariant();
            var before = previous;
            previous = word;

            if (!riskyVerbs.Contains(word))
            {
                continue;
            }
            // `FOR UPDATE` locks; `ON DUPLICATE KEY UPDATE` belongs to the INSERT already named.
            if (word == "UPDATE" && before is "FOR" or "KEY")
            {
                continue;
            }
            // `WHILE … DO` and `FOR … DO` use the word as a keyword. A body's own `DO expr` is not lost: the
            // compound is still confirmed by the conservative default when nothing else is named.
            if (word == "DO")
            {
                continue;
            }
            if (FunctionNamedVerbs.Contains(word) && IsOpenParen(tokens, i + 1))
            {
                continue;
            }
            yield return word;
        }
    }

    /// <summary>
    /// Verbs that are also built-in function names: <c>replace(str, from, to)</c>, <c>insert(str, pos, len,
    /// new)</c>, <c>truncate(x, d)</c>. Followed directly by <c>(</c> the word is the call — the statement
    /// forms (<c>INSERT [IGNORE] [INTO] t</c>, <c>REPLACE [INTO] t</c>, <c>TRUNCATE [TABLE] t</c>) never put
    /// a parenthesis right after the keyword — so a CTE that tidies a column with <c>replace()</c> is not
    /// named a write, refused on an exposed connection, or opened as a manual-commit transaction.
    /// </summary>
    private static readonly HashSet<string> FunctionNamedVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "REPLACE", "TRUNCATE",
    };

    private static bool IsOpenParen(IReadOnlyList<MySqlToken> tokens, int index)
        => index < tokens.Count && tokens[index].Kind == MySqlTokenKind.Punctuation && tokens[index].Text == "(";

    /// <summary>A <c>;</c> inside the statement: the splitter kept a delimiter as punctuation because it
    /// believed it was inside a <c>BEGIN … END</c> or <c>CASE … END</c>.</summary>
    private static bool HasAbsorbedDelimiter(IReadOnlyList<MySqlToken> tokens)
    {
        foreach (var t in tokens)
        {
            if (t.Kind == MySqlTokenKind.Punctuation && t.Text == ";")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary><c>label:</c> — a word, then a colon, then the statement it labels.</summary>
    private static bool IsLabelled(IReadOnlyList<MySqlToken> tokens)
        => tokens.Count > 2 && tokens[0].IsWord
           && tokens[1].Kind == MySqlTokenKind.Punctuation && tokens[1].Text == ":";

    /// <summary>Leads of a compound statement outside a stored program — MariaDB runs these directly.</summary>
    private static readonly HashSet<string> CompoundLeads = new(StringComparer.OrdinalIgnoreCase)
    {
        "IF", "WHILE", "LOOP", "REPEAT", "FOR", "CASE",
    };

    /// <summary>
    /// A compound statement whose body holds statements of its own: MariaDB's <c>BEGIN NOT ATOMIC … END</c>,
    /// a top-level <c>IF</c> / <c>WHILE</c> / <c>LOOP</c> / <c>REPEAT</c> / <c>FOR</c> / <c>CASE</c>, or a
    /// labelled one (<c>lbl: begin not atomic …</c>). The lead says nothing about what runs inside, so the
    /// interior is scanned — otherwise a <c>DELETE</c> in the body would be confirmed only as a guess, and a
    /// guess does not open a manual-commit transaction (<see cref="StatementRisk.NamesAWrite"/>). A plain
    /// <c>BEGIN</c> is transaction control, not a block.
    /// </summary>
    private static bool IsCompound(IReadOnlyList<string> words, int lead)
    {
        if (lead >= words.Count)
        {
            return false;
        }
        if (CompoundLeads.Contains(words[lead]))
        {
            return true;
        }
        return words[lead] == "BEGIN" && lead + 1 < words.Count && words[lead + 1] == "NOT";
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

    /// <summary>
    /// Transaction control inside a compound's body. MariaDB runs <c>if … then create table …; end if</c> or
    /// <c>begin not atomic commit; end</c> at the top level, and the statement the lead word describes is not
    /// the one that ends the transaction — so on a manual-commit tab the body's own statements are read
    /// (each one's first words, after a <c>;</c> kept in the body or a body opener). A bare <c>BEGIN</c> is
    /// skipped: inside a body it opens a block, never a transaction.
    /// </summary>
    private static (string? Name, bool Implicit) InteriorControl(IReadOnlyList<MySqlToken> tokens, ISqlDialect dialect)
    {
        for (var i = 1; i < tokens.Count; i++)
        {
            if (!tokens[i].IsWord || !StartsInteriorStatement(tokens[i - 1]))
            {
                continue;
            }
            var end = i;
            while (end < tokens.Count && !(tokens[end].Kind == MySqlTokenKind.Punctuation && tokens[end].Text == ";"))
            {
                end++;
            }
            var words = ControlWords(tokens.Skip(i).Take(end - i).ToList());
            if (words.Count == 0 || words[0] == "BEGIN")
            {
                continue;
            }
            if (dialect.TransactionControl(words) is { } name)
            {
                return (name, dialect.CommitsImplicitly(words));
            }
        }
        return (null, false);
    }

    private static bool StartsInteriorStatement(MySqlToken previous)
        => (previous.Kind == MySqlTokenKind.Punctuation && previous.Text == ";")
           || (previous.IsWord && MySqlScanner.BodyOpeners.Contains(previous.Text));

    private static void Add(List<string> found, string verb)
    {
        if (!found.Contains(verb))
        {
            found.Add(verb);
        }
    }
}
