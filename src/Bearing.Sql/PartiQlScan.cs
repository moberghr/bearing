using System.Text.RegularExpressions;
using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// The lexical half of DynamoDB's full-scan question (<see cref="ISqlDialect.ScanTargets"/>): for each
/// <c>SELECT</c> in a batch, the table (and index) it reads and the attributes its <c>WHERE</c> pins with
/// <c>=</c> or <c>IN</c>. Pure, and deliberately only what the text says — whether a pinned attribute is the
/// partition key is the caller's question, because only the caller holds the table's keys (§2.2).
/// <para>
/// DynamoDB runs a <c>SELECT</c> as a Query rather than a Scan only when its <c>WHERE</c> has an equality or
/// <c>IN</c> condition on the partition key; <c>pk = 1 OR other = 2</c> scans
/// (https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.select.html). So an
/// attribute counts as pinned when an <c>=</c>/<c>IN</c> on it is a top-level <c>AND</c> conjunct, or when it
/// is pinned in <em>every</em> branch of a top-level <c>OR</c>; parenthesised groups of <c>AND</c>/<c>OR</c>
/// are flattened into that reading. Anything else — <c>&lt;</c>, <c>&lt;&gt;</c>, <c>BETWEEN</c>,
/// <c>BEGINS_WITH</c>, a <c>NOT</c> — pins nothing.
/// </para>
/// <para>
/// <b>Every doubt resolves toward "not pinned" or "not readable"</b>, never toward "pinned": a missed pin costs
/// one confirmation the user did not need, a false one lets a scan run unasked. A statement this reduction
/// cannot follow (no <c>FROM</c>, a subquery, unbalanced brackets, an alias, a join, anything unexpected) comes
/// back with <see cref="ScanTarget.Readable"/> false, and the caller says it could not check — never that the
/// statement scans.
/// </para>
/// <para>
/// Read with the vendored PostgreSQL lexer, which PartiQL shares its lexical forms with. Where the two part
/// ways (<see cref="ReadsAsPartiQl"/>) the whole batch is one unreadable target: the lexer's statements are not
/// the ones DynamoDB would be sent.
/// </para>
/// </summary>
public static partial class PartiQlScan
{
    /// <summary>
    /// One <see cref="ScanTarget"/> per <c>SELECT</c> in <paramref name="sql"/>, in order. Statements led by any
    /// other word are not returned — they are the write guard's (<see cref="PartiQlDialect"/>), not a read.
    /// Empty when there is no <c>SELECT</c>.
    /// </summary>
    public static IReadOnlyList<ScanTarget> Targets(string sql)
    {
        var targets = new List<ScanTarget>();
        if (string.IsNullOrWhiteSpace(sql))
        {
            return targets;
        }

        if (!ReadsAsPartiQl(sql))
        {
            targets.Add(Unreadable(sql.Trim(), null, null));

            return targets;
        }

        foreach (var span in StatementSplitter.SplitWithPostgresLexer(sql))
        {
            var tokens = CodeTokens(span.Text);
            if (tokens.Count == 0 || !IsWord(tokens[0], "SELECT"))
            {
                continue;
            }

            targets.Add(Reduce(span.Text.Trim(), tokens));
        }

        return targets;
    }

    /// <summary>
    /// Whether the vendored PostgreSQL lexer reads <paramref name="sql"/> the way DynamoDB would — false when the
    /// text holds a form that is Postgres' alone and changes where a string, a comment or a statement ends:
    /// <list type="bullet">
    ///   <item>a dollar-quoted body (<c>$$ … $$</c>) or an <c>E'…'</c> string, whose extent PartiQL does not
    ///     share (<c>$$; delete …; $$</c> is one string to the lexer and a statement to anything splitting on
    ///     <c>;</c>);</item>
    ///   <item>a nested block comment — the lexer nests <c>/* /* */ … */</c>, PartiQL's comment ends at the
    ///     first <c>*/</c>, so text the lexer calls comment can be a statement;</item>
    ///   <item>an unterminated string, quoted name or comment, which the lexer runs to the end of the text;</item>
    ///   <item>a backtick (PartiQL's Ion literal, an operator character to the lexer) and a backslash (a psql
    ///     meta-command to the lexer, which swallows the rest of the line);</item>
    ///   <item>an <c>@set</c> line, which the Postgres-bound guard blanks as Bearing's directive while this
    ///     dialect sends it as typed (PartiQL has no script variables).</item>
    /// </list>
    /// Used by both the scan and the write guard, so they decline the same text.
    /// </summary>
    internal static bool ReadsAsPartiQl(string sql)
    {
        if (ScriptDirectives.FindPostgres(sql).Count > 0)
        {
            return false;
        }

        foreach (var token in PgParsing.LexAll(sql))
        {
            if (token.Type == TokenConstants.EOF)
            {
                continue;
            }

            if (NotPartiQlTokens.Contains(token.Type))
            {
                return false;
            }

            var text = token.Text ?? "";
            var nestedComment = token.Type == PostgreSQLLexer.BlockComment
                && text.IndexOf("/*", 2, StringComparison.Ordinal) >= 0;
            var backtick = token.Type == PostgreSQLLexer.Operator
                && text.Contains('`');
            var metaCommand = token.Type == PostgreSQLLexer.SEMI && text != ";";
            if (nestedComment || backtick || metaCommand)
            {
                return false;
            }
        }

        return true;
    }

    private static readonly HashSet<int> NotPartiQlTokens =
    [
        PostgreSQLLexer.BeginDollarStringConstant, PostgreSQLLexer.DollarText,
        PostgreSQLLexer.EndDollarStringConstant,
        PostgreSQLLexer.EscapeStringConstant, PostgreSQLLexer.UnterminatedEscapeStringConstant,
        PostgreSQLLexer.InvalidEscapeStringConstant, PostgreSQLLexer.InvalidUnterminatedEscapeStringConstant,
        PostgreSQLLexer.UnterminatedBlockComment,
        PostgreSQLLexer.UnterminatedStringConstant,
        PostgreSQLLexer.UnterminatedQuotedIdentifier, PostgreSQLLexer.InvalidQuotedIdentifier,
        PostgreSQLLexer.InvalidUnterminatedQuotedIdentifier,
        PostgreSQLLexer.UnterminatedUnicodeQuotedIdentifier, PostgreSQLLexer.InvalidUnicodeQuotedIdentifier,
        PostgreSQLLexer.InvalidUnterminatedUnicodeQuotedIdentifier,
        PostgreSQLLexer.UnterminatedUnicodeEscapeStringConstant,
        PostgreSQLLexer.UnterminatedBinaryStringConstant, PostgreSQLLexer.InvalidBinaryStringConstant,
        PostgreSQLLexer.InvalidUnterminatedBinaryStringConstant,
        PostgreSQLLexer.UnterminatedHexadecimalStringConstant, PostgreSQLLexer.InvalidHexadecimalStringConstant,
        PostgreSQLLexer.InvalidUnterminatedHexadecimalStringConstant,
    ];

    /// <summary>Words that structure a statement and so are never an attribute or table name when bare.</summary>
    private static readonly HashSet<string> Structural = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "BETWEEN", "IS", "NULL", "MISSING", "TRUE",
        "FALSE", "ORDER", "BY", "ASC", "DESC", "EXISTS",
    };

    /// <summary>
    /// SELECT … FROM target [WHERE condition] [ORDER BY …], or unreadable. The table and index are kept on an
    /// unreadable target when they were read before the reduction gave up (an alias after them, say), so a
    /// caller can still name them.
    /// </summary>
    private static ScanTarget Reduce(string statement, List<Tok> tokens)
    {
        while (tokens.Count > 0 && tokens[^1].Text == ";")
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        if (!Balanced(tokens))
        {
            return Unreadable(statement, null, null);
        }

        // A second SELECT is a subquery; DynamoDB has none to offer, and this reduction does not follow one.
        for (var i = 1; i < tokens.Count; i++)
        {
            if (IsWord(tokens[i], "SELECT"))
            {
                return Unreadable(statement, null, null);
            }
        }

        var from = TopLevelIndexOf(tokens, 1, tokens.Count, "FROM");
        if (from < 0 || from + 1 >= tokens.Count || !IsName(tokens[from + 1]))
        {
            return Unreadable(statement, null, null);
        }

        var table = NameOf(tokens[from + 1]);
        string? index = null;
        var at = from + 2;
        if (at + 1 < tokens.Count && tokens[at].Text == "." && IsName(tokens[at + 1]))
        {
            index = NameOf(tokens[at + 1]);
            at += 2;
        }

        if (at == tokens.Count || IsOrderBy(tokens, at))
        {
            return new ScanTarget(statement, table, index, [], Readable: true);
        }

        if (!IsWord(tokens[at], "WHERE"))
        {
            return Unreadable(statement, table, index);
        }

        var end = tokens.Count;
        for (var i = at + 1; i < tokens.Count; i++)
        {
            if (IsOrderBy(tokens, i) && DepthAt(tokens, at + 1, i) == 0)
            {
                end = i;
                break;
            }
        }

        var pinned = Pins(tokens, at + 1, end);

        return pinned is null
            ? Unreadable(statement, table, index)
            : new ScanTarget(statement, table, index, pinned.Order(StringComparer.Ordinal).ToList(), Readable: true);
    }

    /// <summary>
    /// The attributes a condition pins: the intersection over its top-level <c>OR</c> branches. Null when the
    /// condition cannot be read (an empty operand, a dangling <c>AND</c>/<c>OR</c>).
    /// </summary>
    private static HashSet<string>? Pins(List<Tok> tokens, int from, int to)
    {
        HashSet<string>? pinned = null;
        foreach (var (start, end) in SplitTopLevel(tokens, from, to, "OR"))
        {
            var branch = ConjunctionPins(tokens, start, end);
            if (branch is null)
            {
                return null;
            }

            if (pinned is null)
            {
                pinned = branch;
            }
            else
            {
                pinned.IntersectWith(branch);
            }
        }

        return pinned;
    }

    /// <summary>The union over a branch's top-level <c>AND</c> conjuncts.</summary>
    private static HashSet<string>? ConjunctionPins(List<Tok> tokens, int from, int to)
    {
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (start, end) in SplitTopLevel(tokens, from, to, "AND"))
        {
            var factor = FactorPins(tokens, start, end);
            if (factor is null)
            {
                return null;
            }

            pinned.UnionWith(factor);
        }

        return pinned;
    }

    /// <summary>A <c>NOT</c> pins nothing (its operand must still read); a parenthesised group is flattened;
    /// anything else is one predicate.</summary>
    private static HashSet<string>? FactorPins(List<Tok> tokens, int from, int to)
    {
        if (from >= to)
        {
            return null;
        }

        if (IsWord(tokens[from], "NOT"))
        {
            return Pins(tokens, from + 1, to) is null ? null : [];
        }

        if (tokens[from].Text == "(" && Close(tokens, from) == to - 1)
        {
            return Pins(tokens, from + 1, to - 1);
        }

        return PredicatePins(tokens, from, to);
    }

    /// <summary>
    /// <c>name = value</c>, <c>value = name</c> and <c>name IN [v, …]</c> / <c>name IN (v, …)</c> pin the name;
    /// every other predicate is readable and pins nothing. A value is a string, a number (optionally signed) or a
    /// <c>?</c> parameter — never another attribute, since <c>a = b</c> is no key condition.
    /// </summary>
    private static HashSet<string> PredicatePins(List<Tok> tokens, int from, int to)
    {
        if (IsName(tokens[from]) && from + 1 < to && tokens[from + 1].Text == "="
            && ValueLength(tokens, from + 2, to) == to - from - 2)
        {
            return [NameOf(tokens[from])];
        }

        var left = ValueLength(tokens, from, to);
        if (left > 0 && from + left + 2 == to && tokens[from + left].Text == "=" && IsName(tokens[to - 1]))
        {
            return [NameOf(tokens[to - 1])];
        }

        if (to - from >= 4 && IsName(tokens[from]) && IsWord(tokens[from + 1], "IN")
            && tokens[from + 2].Text is "(" or "[" && Close(tokens, from + 2) == to - 1
            && IsValueList(tokens, from + 3, to - 1))
        {
            return [NameOf(tokens[from])];
        }

        return [];
    }

    private static bool IsValueList(List<Tok> tokens, int from, int to)
    {
        if (from >= to)
        {
            return false;
        }

        var at = from;
        while (true)
        {
            var length = ValueLength(tokens, at, to);
            if (length == 0)
            {
                return false;
            }

            at += length;
            if (at == to)
            {
                return true;
            }

            if (tokens[at].Text != "," || at + 1 == to)
            {
                return false;
            }

            at++;
        }
    }

    /// <summary>How many tokens the value at <paramref name="at"/> spans (a sign makes it two), or 0.</summary>
    private static int ValueLength(List<Tok> tokens, int at, int to)
    {
        if (at >= to)
        {
            return 0;
        }

        if (IsScalar(tokens[at]))
        {
            return 1;
        }

        var signed = tokens[at].Text is "-" or "+" && at + 1 < to
            && tokens[at + 1].Type is PostgreSQLLexer.Integral or PostgreSQLLexer.Numeric;

        return signed ? 2 : 0;
    }

    private static bool IsScalar(Tok token)
        => token.Type is PostgreSQLLexer.StringConstant or PostgreSQLLexer.Integral or PostgreSQLLexer.Numeric
           || token.Text == "?";

    /// <summary>
    /// The pieces of [<paramref name="from"/>, <paramref name="to"/>) between top-level occurrences of
    /// <paramref name="word"/>. An <c>AND</c> that closes a <c>BETWEEN … AND …</c> is the predicate's, not a
    /// conjunction.
    /// </summary>
    private static List<(int Start, int End)> SplitTopLevel(List<Tok> tokens, int from, int to, string word)
    {
        var pieces = new List<(int, int)>();
        var start = from;
        var depth = 0;
        var openBetween = false;
        for (var i = from; i < to; i++)
        {
            depth += DepthChange(tokens[i]);
            if (depth != 0)
            {
                continue;
            }

            if (IsWord(tokens[i], "BETWEEN"))
            {
                openBetween = true;
            }
            else if (IsWord(tokens[i], word))
            {
                if (word == "AND" && openBetween)
                {
                    openBetween = false;
                    continue;
                }

                pieces.Add((start, i));
                start = i + 1;
            }
        }

        pieces.Add((start, to));

        return pieces;
    }

    private static int TopLevelIndexOf(List<Tok> tokens, int from, int to, string word)
    {
        var depth = 0;
        for (var i = from; i < to; i++)
        {
            depth += DepthChange(tokens[i]);
            if (depth == 0 && IsWord(tokens[i], word))
            {
                return i;
            }
        }

        return -1;
    }

    private static int DepthAt(List<Tok> tokens, int from, int to)
    {
        var depth = 0;
        for (var i = from; i < to; i++)
        {
            depth += DepthChange(tokens[i]);
        }

        return depth;
    }

    private static bool IsOrderBy(List<Tok> tokens, int at)
        => at + 1 < tokens.Count && IsWord(tokens[at], "ORDER") && IsWord(tokens[at + 1], "BY");

    /// <summary>The index of the bracket closing the one at <paramref name="open"/>.</summary>
    private static int Close(List<Tok> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            depth += DepthChange(tokens[i]);
            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Every bracket closed by its own kind, in order — PartiQL's lists, tuples and calls alike.</summary>
    private static bool Balanced(List<Tok> tokens)
    {
        var open = new Stack<string>();
        foreach (var token in tokens)
        {
            switch (token.Text)
            {
                case "(" or "[" or "{":
                    open.Push(token.Text);
                    break;
                case ")" or "]" or "}":
                    var expected = token.Text switch { ")" => "(", "]" => "[", _ => "{" };
                    if (open.Count == 0 || open.Pop() != expected)
                    {
                        return false;
                    }

                    break;
            }
        }

        return open.Count == 0;
    }

    private static int DepthChange(Tok token) => token.Text switch
    {
        "(" or "[" or "{" => 1,
        ")" or "]" or "}" => -1,
        _ => 0,
    };

    private static bool IsWord(Tok token, string word) => token.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>A double-quoted name, or a bare word that is not one of the statement's own keywords.</summary>
    private static bool IsName(Tok token)
        => token.Type == PostgreSQLLexer.QuotedIdentifier
           || (BareWord().IsMatch(token.Text) && !Structural.Contains(token.Text));

    /// <summary>As written: a bare name keeps its case, a quoted one is unquoted with <c>""</c> → <c>"</c>.</summary>
    private static string NameOf(Tok token)
        => token.Type == PostgreSQLLexer.QuotedIdentifier ? PgIdentifier.Unquote(token.Text) : token.Text;

    private static ScanTarget Unreadable(string statement, string? table, string? index)
        => new(statement, table, index, [], Readable: false);

    /// <summary>
    /// The statement's code tokens. An operator token carrying a <c>?</c> is cut around it, because the lexer
    /// reads <c>pk=?</c> as the one operator <c>=?</c>, and PartiQL reads a comparison with a parameter.
    /// </summary>
    private static List<Tok> CodeTokens(string statement)
    {
        var tokens = new List<Tok>();
        foreach (var token in PgParsing.LexAll(statement))
        {
            if (token.Type == TokenConstants.EOF || token.Channel != TokenConstants.DefaultChannel)
            {
                continue;
            }

            var text = token.Text ?? "";
            if (token.Type == PostgreSQLLexer.Operator
                && text.Length > 1 && text.Contains('?'))
            {
                foreach (var piece in QuestionMarks().Split(text))
                {
                    if (piece.Length > 0)
                    {
                        tokens.Add(new Tok(piece, PostgreSQLLexer.Operator));
                    }
                }

                continue;
            }

            tokens.Add(new Tok(text, token.Type));
        }

        return tokens;
    }

    private readonly record struct Tok(string Text, int Type);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$]*$")]
    private static partial Regex BareWord();

    [GeneratedRegex(@"(\?)")]
    private static partial Regex QuestionMarks();
}
