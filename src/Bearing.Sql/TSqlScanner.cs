using System.Text;

namespace Bearing.Sql;

/// <summary>One lexical token of a T-SQL batch, with where it sits in the source.</summary>
/// <param name="Text">The token as written. A delimited identifier keeps its delimiters.</param>
/// <param name="Start">Character offset of the first character.</param>
/// <param name="Length">Character length, delimiters included.</param>
/// <param name="Depth">Parenthesis nesting at this token; 0 is top level. An opening paren carries the
/// depth it opens into, a closing paren the depth it closes.</param>
/// <param name="Kind">What the scanner made of it.</param>
public sealed record TSqlToken(string Text, int Start, int Length, int Depth, TSqlTokenKind Kind)
{
    /// <summary>True when this token could be a clause keyword — a bare word, not a delimited name, a
    /// string, a variable or punctuation. The write guard and the paging rules only ever match on these.</summary>
    public bool IsWord => Kind == TSqlTokenKind.Word;
}

public enum TSqlTokenKind
{
    /// <summary>A bare identifier or keyword: <c>select</c>, <c>Orders</c>, <c>dbo</c>.</summary>
    Word,
    /// <summary>A delimited identifier — <c>[Order Details]</c> or <c>"Order Details"</c>. Never a keyword,
    /// which is the whole reason it is a separate kind (see <see cref="TSqlScanner"/>).</summary>
    QuotedName,
    /// <summary>A character literal, <c>N</c> prefix included: <c>'a''b'</c>, <c>N'x'</c>.</summary>
    Text,
    /// <summary>A local or global variable: <c>@id</c>, <c>@@version</c>.</summary>
    Variable,
    Number,
    Punctuation,
}

/// <summary>One statement of a batch, as the scanner split it.</summary>
/// <param name="Text">The statement as written, trimmed. Comments and formatting intact.</param>
/// <param name="Tokens">Its tokens, in order, comments and whitespace already dropped.</param>
public sealed record TSqlStatement(string Text, IReadOnlyList<TSqlToken> Tokens);

/// <summary>
/// A hand-rolled T-SQL lexer and batch splitter. Small, deliberately, and not a parser: it answers "what
/// tokens are here, which are bare words, and where does each statement end?" — enough for the write guard
/// and the paging rules, and nothing more.
/// <para>
/// <b>Why not the ANTLR grammar.</b> This project vendors a <em>PostgreSQL</em> grammar, and using it on
/// T-SQL is how a table called <c>[Order Details]</c> came to read as a query with a top-level
/// <c>ORDER BY</c>: the PG lexer has no delimited-identifier concept, so it emitted the words inside the
/// brackets as ordinary tokens. Every rule that asks a <em>positive</em> question of the token stream — "is
/// this a write?", "may I append a page clause?" — is wrong in a way that reaches the server when the
/// lexer cannot read the dialect. A T-SQL ANTLR grammar is the Phase 2 answer; this is what makes Phase 1
/// honest without one.
/// </para>
/// <para>
/// <b>What it handles</b>, because each one silently breaks a keyword scan otherwise: <c>[delimited]</c> and
/// <c>"delimited"</c> names (with <c>]]</c> / <c>""</c> escapes), <c>'strings'</c> with <c>''</c> escapes and
/// the <c>N</c> prefix, <c>@variables</c> and <c>@@globals</c>, <c>#temp</c> / <c>##global</c> table names,
/// <c>--</c> line comments, <c>/* */</c> block comments (<b>nestable</b> in T-SQL, unlike most dialects),
/// the <c>GO</c> batch separator, and <c>;</c>.
/// </para>
/// </summary>
public static class TSqlScanner
{
    /// <summary>Every token in <paramref name="sql"/>, comments and whitespace dropped.</summary>
    public static IReadOnlyList<TSqlToken> Tokenize(string sql)
    {
        var tokens = new List<TSqlToken>();
        var depth = 0;
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            // -- line comment
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }

            // /* block comment */ — nestable, which is a real T-SQL rule: an unbalanced inner /* inside an
            // outer comment leaves the rest of the batch commented out, and a non-nesting scanner would
            // resume lexing keywords that are not there.
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var nesting = 1;
                i += 2;
                while (i < sql.Length && nesting > 0)
                {
                    if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { nesting++; i += 2; }
                    else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { nesting--; i += 2; }
                    else i++;
                }
                continue;
            }

            // 'string' / N'string' — '' is an escaped quote, not a terminator.
            if (c == '\'' || ((c == 'N' || c == 'n') && i + 1 < sql.Length && sql[i + 1] == '\''))
            {
                var start = i;
                if (c != '\'') i++;                       // step over the N prefix
                i++;                                      // step over the opening quote
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                tokens.Add(new TSqlToken(sql[start..i], start, i - start, depth, TSqlTokenKind.Text));
                continue;
            }

            // [delimited name] — ]] is an escaped bracket.
            if (c == '[')
            {
                var start = i;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == ']')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == ']') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                tokens.Add(new TSqlToken(sql[start..i], start, i - start, depth, TSqlTokenKind.QuotedName));
                continue;
            }

            // "delimited name" — QUOTED_IDENTIFIER is ON for SqlClient, so this is a name, not a string.
            if (c == '"')
            {
                var start = i;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '"')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '"') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                tokens.Add(new TSqlToken(sql[start..i], start, i - start, depth, TSqlTokenKind.QuotedName));
                continue;
            }

            // @local, @@GLOBAL
            if (c == '@')
            {
                var start = i;
                i++;
                if (i < sql.Length && sql[i] == '@') i++;
                while (i < sql.Length && IsNameChar(sql[i])) i++;
                tokens.Add(new TSqlToken(sql[start..i], start, i - start, depth, TSqlTokenKind.Variable));
                continue;
            }

            if (c == '(')
            {
                depth++;
                tokens.Add(new TSqlToken("(", i, 1, depth, TSqlTokenKind.Punctuation));
                i++;
                continue;
            }

            if (c == ')')
            {
                tokens.Add(new TSqlToken(")", i, 1, depth, TSqlTokenKind.Punctuation));
                if (depth > 0) depth--;
                i++;
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.')) i++;
                tokens.Add(new TSqlToken(sql[start..i], start, i - start, depth, TSqlTokenKind.Number));
                continue;
            }

            // A bare word. #temp and ##global are ordinary names that happen to start with #.
            if (char.IsLetter(c) || c == '_' || c == '#')
            {
                var start = i;
                while (i < sql.Length && IsNameChar(sql[i])) i++;
                tokens.Add(new TSqlToken(sql[start..i], start, i - start, depth, TSqlTokenKind.Word));
                continue;
            }

            tokens.Add(new TSqlToken(sql[i].ToString(), i, 1, depth, TSqlTokenKind.Punctuation));
            i++;
        }

        return tokens;
    }

    /// <summary>
    /// Split <paramref name="sql"/> into statements on top-level <c>;</c> and on a <c>GO</c> that stands
    /// alone on its line (the batch separator — a client-side directive, not a T-SQL statement, and the one
    /// separator a semicolon-only splitter misses entirely).
    /// <para>
    /// Empty statements are dropped, so a trailing semicolon or a stray <c>GO</c> does not manufacture one.
    /// </para>
    /// </summary>
    public static IReadOnlyList<TSqlStatement> Split(string sql)
    {
        var statements = new List<TSqlStatement>();
        if (string.IsNullOrWhiteSpace(sql)) return statements;

        var tokens = Tokenize(sql);
        var current = new List<TSqlToken>();
        var from = 0;

        void Flush(int end)
        {
            var text = end > from ? sql[from..end].Trim() : "";
            if (current.Count > 0 && text.Length > 0)
                statements.Add(new TSqlStatement(text, current.ToList()));
            current.Clear();
        }

        foreach (var t in tokens)
        {
            if (t.Kind == TSqlTokenKind.Punctuation && t.Text == ";" && t.Depth == 0)
            {
                Flush(t.Start);
                from = t.Start + 1;
                continue;
            }

            if (t.IsWord && t.Depth == 0
                && t.Text.Equals("GO", StringComparison.OrdinalIgnoreCase)
                && StandsAlone(sql, t))
            {
                Flush(t.Start);
                from = t.Start + t.Length;
                continue;
            }

            current.Add(t);
        }
        Flush(sql.Length);
        return statements;
    }

    /// <summary>True when <paramref name="token"/> is the only thing on its line — what makes a <c>GO</c>
    /// the batch separator rather than a column called <c>go</c>. A trailing repeat count (<c>GO 5</c>) is
    /// deliberately not supported; it would still split, which is the safe direction.</summary>
    private static bool StandsAlone(string sql, TSqlToken token)
    {
        for (var i = token.Start - 1; i >= 0; i--)
        {
            if (sql[i] == '\n') break;
            if (!char.IsWhiteSpace(sql[i])) return false;
        }
        for (var i = token.Start + token.Length; i < sql.Length; i++)
        {
            if (sql[i] == '\n') break;
            if (!char.IsWhiteSpace(sql[i])) return false;
        }
        return true;
    }

    private static bool IsNameChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$';

    /// <summary>
    /// Where a <c>TOP (n)</c> clause belongs in <paramref name="statement"/>, or <c>null</c> when this
    /// statement must not take one. T-SQL's order is
    /// <c>SELECT [ALL | DISTINCT] [TOP (n)] select_list</c>, so the offset is after the leading
    /// <c>SELECT</c> and after an <c>ALL</c>/<c>DISTINCT</c> if one follows it.
    /// <para>
    /// Null for anything that does not lead with a bare <c>SELECT</c> — a CTE's outer select cannot be
    /// located this cheaply, and guessing is what this scanner exists to stop.
    /// </para>
    /// </summary>
    public static int? TopInsertionPoint(IReadOnlyList<TSqlToken> tokens)
    {
        if (tokens.Count == 0) return null;

        var first = tokens[0];
        if (!first.IsWord || !first.Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase)) return null;

        var after = first.Start + first.Length;
        if (tokens.Count > 1 && tokens[1].IsWord
            && (tokens[1].Text.Equals("DISTINCT", StringComparison.OrdinalIgnoreCase)
                || tokens[1].Text.Equals("ALL", StringComparison.OrdinalIgnoreCase)))
            after = tokens[1].Start + tokens[1].Length;

        return after;
    }

    /// <summary>The uppercased text of every bare word at parenthesis depth 0 — the clause keywords, with
    /// delimited names, strings and variables excluded by construction.</summary>
    public static IReadOnlyList<string> TopLevelWords(IReadOnlyList<TSqlToken> tokens)
    {
        var words = new List<string>();
        foreach (var t in tokens)
            if (t.IsWord && t.Depth == 0) words.Add(t.Text.ToUpperInvariant());
        return words;
    }

    /// <summary>The uppercased text of every bare word at any depth — for scanning the interior of a
    /// preamble statement (a CTE) that may hide a write further in.</summary>
    public static IReadOnlyList<string> AllWords(IReadOnlyList<TSqlToken> tokens)
    {
        var words = new List<string>();
        foreach (var t in tokens)
            if (t.IsWord) words.Add(t.Text.ToUpperInvariant());
        return words;
    }

    /// <summary>Re-render <paramref name="sql"/> with <paramref name="clause"/> spliced in at
    /// <paramref name="at"/>, surrounded by single spaces. Pure string work; the caller decided it is safe.</summary>
    public static string Insert(string sql, int at, string clause)
    {
        var sb = new StringBuilder(sql.Length + clause.Length + 2);
        sb.Append(sql, 0, at).Append(' ').Append(clause).Append(sql, at, sql.Length - at);
        return sb.ToString();
    }
}
