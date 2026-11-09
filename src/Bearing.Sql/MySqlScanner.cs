
namespace Bearing.Sql;

/// <summary>One lexical token of a MySQL / MariaDB script, with where it sits in the source.</summary>
/// <param name="Text">The token as written. A quoted name or literal keeps its delimiters.</param>
/// <param name="Start">Character offset of the first character.</param>
/// <param name="Length">Character length, delimiters included.</param>
/// <param name="Depth">Parenthesis nesting at this token; 0 is top level. An opening paren carries the
/// depth it opens into, a closing paren the depth it closes.</param>
/// <param name="Kind">What the scanner made of it.</param>
public sealed record MySqlToken(string Text, int Start, int Length, int Depth, MySqlTokenKind Kind)
{
    /// <summary>True when this token could be a clause keyword — a bare word, never a quoted name, a
    /// literal or a variable. The guard and the paging rules only ever match on these.</summary>
    public bool IsWord => Kind == MySqlTokenKind.Word;
}

public enum MySqlTokenKind
{
    /// <summary>A bare identifier or keyword: <c>select</c>, <c>Orders</c>.</summary>
    Word,
    /// <summary>A backticked identifier, <c>`Order Details`</c>. Never a keyword.</summary>
    QuotedName,
    /// <summary>A character literal — single- or double-quoted (double quotes delimit a string under the
    /// default <c>sql_mode</c>), with an <c>N</c>/<c>X</c>/<c>B</c> prefix when it carries one.</summary>
    Text,
    /// <summary>A user or system variable: <c>@id</c>, <c>@@session.sql_mode</c>, <c>@'quoted'</c>.</summary>
    Variable,
    Number,
    Punctuation,
    /// <summary>The statement delimiter currently in force — <c>;</c>, or whatever a
    /// <c>DELIMITER</c> directive set.</summary>
    Delimiter,
    /// <summary>A whole <c>DELIMITER x</c> line: a client directive, never sent to the server.</summary>
    Directive,
}

/// <summary>One statement of a script, as the scanner split it.</summary>
/// <param name="Text">The statement as written, trimmed. Comments and formatting intact.</param>
/// <param name="Tokens">Its tokens in order, comments and whitespace dropped, delimiters excluded.</param>
/// <param name="Start">Character offset of <c>Text</c>'s first character in the scanned buffer.</param>
public sealed record MySqlStatement(string Text, IReadOnlyList<MySqlToken> Tokens, int Start);

/// <summary>
/// A hand-rolled MySQL / MariaDB lexer and script splitter — <see cref="TSqlScanner"/>'s counterpart, small for
/// the same reasons: it answers "what tokens are here, which are bare words, and where does each statement
/// end?" on every keystroke and on the write path, where a parse can neither be afforded nor be allowed to
/// fail to answer (§1.2). The vendored grammar (<see cref="MySqlParseRules"/>) is for completion.
/// <para>
/// <b>What it handles</b>, because each one silently breaks a keyword scan otherwise:
/// </para>
/// <list type="bullet">
///   <item><c>`backticked`</c> names (with <c>``</c> escapes) — never keywords.</item>
///   <item><c>'strings'</c> and <c>"strings"</c> with both the doubled-quote and the <b>backslash</b>
///     escape. The backslash is MySQL's default (<c>NO_BACKSLASH_ESCAPES</c> off), and missing it is not a
///     cosmetic error: <c>'it\'s'</c> read without it closes the string at the second quote and lexes the
///     rest of the line as SQL.</item>
///   <item>Three comment forms: <c>#</c> to end of line, <c>-- </c> (which needs whitespace after the
///     dashes — <c>--x</c> is two minus signs), and <c>/* */</c>, which does <em>not</em> nest here.</item>
///   <item><b>Executable comments</b>, <c>/*! … */</c> and MariaDB's <c>/*M! … */</c>. The server runs their
///     contents, so they are lexed as code: a <c>/*!50000 DROP TABLE t */</c> is a DROP to the guard, as it
///     is to the server. (The version number is not compared — every version is assumed to execute it,
///     which is the cautious reading.)</item>
///   <item>The <c>DELIMITER</c> client directive, the way every MySQL script that defines a stored routine
///     is written. It is recognised at the start of a statement only — a column called <c>delimiter</c>
///     mid-statement stays a column.</item>
///   <item><c>BEGIN … END</c> bodies written <em>without</em> a <c>DELIMITER</c>: <c>create procedure p()
///     begin select 1; select 2; end</c> is one statement to the server, and splitting it at the first
///     semicolon would offer Run half a procedure. <c>END IF</c>, <c>END LOOP</c>, <c>END WHILE</c>,
///     <c>END REPEAT</c> and <c>END FOR</c> close their own constructs and are not counted.</item>
/// </list>
/// </summary>
public static class MySqlScanner
{
    private const string DefaultDelimiter = ";";

    /// <summary>Every token in <paramref name="sql"/>, comments and whitespace dropped.</summary>
    public static IReadOnlyList<MySqlToken> Tokenize(string sql) => Tokenize(sql, MySqlQuoting.Default);

    /// <summary>
    /// Every token, read under <paramref name="quoting"/> — the server's <c>sql_mode</c> decides whether a
    /// backslash escapes a quote, and this text-only layer cannot know it (see <see cref="MySqlQuoting"/>).
    /// </summary>
    internal static IReadOnlyList<MySqlToken> Tokenize(string sql, MySqlQuoting quoting)
    {
        var tokens = new List<MySqlToken>();
        var depth = 0;
        var i = 0;
        var delimiter = DefaultDelimiter;
        var inExecutableComment = false;
        var statementHasTokens = false;

        void Add(MySqlToken token)
        {
            tokens.Add(token);
            statementHasTokens = token.Kind is not (MySqlTokenKind.Delimiter or MySqlTokenKind.Directive);
        }

        while (i < sql.Length)
        {
            var c = sql[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // The delimiter is checked before anything else can claim its characters — a custom `//` must
            // not be read as the start of something, and `$$` must not be swallowed into a name.
            if (string.CompareOrdinal(sql, i, delimiter, 0, delimiter.Length) == 0)
            {
                Add(new MySqlToken(delimiter, i, delimiter.Length, depth, MySqlTokenKind.Delimiter));
                i += delimiter.Length;
                continue;
            }

            // The closing half of an executable comment: its contents were lexed as code.
            if (inExecutableComment && c == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
            {
                inExecutableComment = false;
                i += 2;
                continue;
            }

            // # line comment, and -- line comment (only when followed by whitespace or the end).
            if (c == '#' || (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-'
                             && (i + 2 >= sql.Length || char.IsWhiteSpace(sql[i + 2]) || char.IsControl(sql[i + 2]))))
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                // /*! and /*M! — executable. Skip the marker and its version number; lex the rest as code.
                var marker = i + 2;
                if (marker < sql.Length && sql[marker] == 'M' && marker + 1 < sql.Length && sql[marker + 1] == '!')
                {
                    marker++;
                }
                if (marker < sql.Length && sql[marker] == '!' && !inExecutableComment)
                {
                    i = marker + 1;
                    while (i < sql.Length && char.IsDigit(sql[i]))
                    {
                        i++;
                    }
                    inExecutableComment = true;
                    continue;
                }

                // An ordinary comment. Not nestable in MySQL: the first */ closes it.
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + 2;
                continue;
            }

            if (IsDirectiveStart(sql, i, statementHasTokens, out var lineEnd, out var newDelimiter))
            {
                var directive = sql[i..lineEnd].TrimEnd();
                Add(new MySqlToken(directive, i, directive.Length, 0, MySqlTokenKind.Directive));
                delimiter = newDelimiter;
                depth = 0;
                i = lineEnd;
                continue;
            }

            // 'string', "string", and the prefixed forms N'…', X'…', B'…'.
            if (c is '\'' or '"'
                || (c is 'N' or 'n' or 'X' or 'x' or 'B' or 'b' && i + 1 < sql.Length && sql[i + 1] == '\''
                    && !PrecededByNameChar(sql, i)))
            {
                var start = i;
                if (c is not ('\'' or '"'))
                {
                    i++;
                }
                i = SkipQuoted(sql, i, sql[i], backslashEscapes: quoting.Escapes(sql[i]));
                Add(new MySqlToken(sql[start..i], start, i - start, depth, MySqlTokenKind.Text));
                continue;
            }

            if (c == '`')
            {
                var start = i;
                i = SkipQuoted(sql, i, '`', backslashEscapes: false);
                Add(new MySqlToken(sql[start..i], start, i - start, depth, MySqlTokenKind.QuotedName));
                continue;
            }

            // @var, @@global.var, @'quoted', @`quoted`, and the host half of 'user'@'host'.
            if (c == '@')
            {
                var start = i;
                i++;
                if (i < sql.Length && sql[i] == '@')
                {
                    i++;
                }
                if (i < sql.Length && sql[i] is '\'' or '"' or '`')
                {
                    i = SkipQuoted(sql, i, sql[i], backslashEscapes: quoting.Escapes(sql[i]));
                }
                else
                {
                    while (i < sql.Length && (IsNameChar(sql[i]) || sql[i] == '.'))
                    {
                        i++;
                    }
                }
                Add(new MySqlToken(sql[start..i], start, i - start, depth, MySqlTokenKind.Variable));
                continue;
            }

            if (c == '(')
            {
                depth++;
                Add(new MySqlToken("(", i, 1, depth, MySqlTokenKind.Punctuation));
                i++;
                continue;
            }

            if (c == ')')
            {
                Add(new MySqlToken(")", i, 1, depth, MySqlTokenKind.Punctuation));
                if (depth > 0)
                {
                    depth--;
                }
                i++;
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.') && !AtDelimiter(sql, i, delimiter))
                {
                    i++;
                }
                Add(new MySqlToken(sql[start..i], start, i - start, depth, MySqlTokenKind.Number));
                continue;
            }

            if (IsNameChar(c))
            {
                var start = i;
                while (i < sql.Length && IsNameChar(sql[i]) && !AtDelimiter(sql, i, delimiter))
                {
                    i++;
                }
                Add(new MySqlToken(sql[start..i], start, i - start, depth, MySqlTokenKind.Word));
                continue;
            }

            Add(new MySqlToken(sql[i].ToString(), i, 1, depth, MySqlTokenKind.Punctuation));
            i++;
        }

        return tokens;
    }

    /// <summary>
    /// Split <paramref name="sql"/> into statements: at the delimiter in force (outside a block and outside
    /// parentheses when it is <c>;</c>; anywhere when a <c>DELIMITER</c> directive changed it, which is the
    /// directive's whole purpose), and at every <c>DELIMITER</c> line, which belongs to no statement. Empty
    /// statements are dropped.
    /// <para>
    /// A block is a <c>BEGIN … END</c> or <c>CASE … END</c>, and — in statement position only — a compound
    /// <c>IF</c> / <c>WHILE</c> / <c>LOOP</c> / <c>REPEAT</c> / <c>FOR</c>, closed by its own <c>END IF</c> /
    /// <c>END WHILE</c> / …. MariaDB runs those compounds at the top level, so <c>if @a then delete from t;
    /// end if</c> is one statement to the server; split at its first semicolon it would offer Run half a
    /// statement. Openers are kept on a stack so a closer only closes its own kind.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MySqlStatement> Split(string sql) => Split(sql, MySqlQuoting.Default);

    /// <inheritdoc cref="Split(string)"/>
    internal static IReadOnlyList<MySqlStatement> Split(string sql, MySqlQuoting quoting)
    {
        var statements = new List<MySqlStatement>();
        if (string.IsNullOrWhiteSpace(sql))
        {
            return statements;
        }

        var tokens = Tokenize(sql, quoting);
        var current = new List<MySqlToken>();
        var from = 0;
        var blocks = new List<string>();
        // The index of the last word read as the first of a statement, so `label:` can pass the position on.
        var lastStart = -1;
        // Which BEGINs this scan read as a block — a body's first word follows only those.
        var openedBlock = new bool[tokens.Count];

        void Flush(int end)
        {
            var raw = end > from ? sql[from..end] : "";
            var text = raw.Trim();
            if (current.Count > 0 && text.Length > 0)
            {
                statements.Add(new MySqlStatement(text, current.ToList(), from + (raw.Length - raw.TrimStart().Length)));
            }
            current.Clear();
        }

        for (var index = 0; index < tokens.Count; index++)
        {
            var t = tokens[index];

            if (t.Kind == MySqlTokenKind.Directive)
            {
                Flush(t.Start);
                from = t.Start + t.Length;
                blocks.Clear();
                lastStart = -1;
                continue;
            }

            if (t.Kind == MySqlTokenKind.Delimiter)
            {
                var custom = t.Text != DefaultDelimiter;
                if (custom || (t.Depth == 0 && blocks.Count == 0))
                {
                    Flush(t.Start);
                    from = t.Start + t.Length;
                    blocks.Clear();
                    lastStart = -1;
                    continue;
                }

                // A `;` inside a body is part of the statement, as punctuation.
                current.Add(t with { Kind = MySqlTokenKind.Punctuation });
                continue;
            }

            // A word after a `.` is a name, reserved or not (`t.case`, `t.end`): MySQL reads it as one.
            if (t.IsWord && t.Depth == 0 && !IsPunctuation(tokens, index - 1, "."))
            {
                var inCaseExpression = blocks.Count > 0 && blocks[^1] == CaseExpression;
                var atStart = AtStatementStart(tokens, index, current.Count == 0, lastStart, openedBlock, blocks)
                    // A CASE expression's THEN / ELSE is followed by a value, never a statement.
                    && !(inCaseExpression && IsWordAt(tokens, index - 1, "THEN", "ELSE"));
                if (atStart)
                {
                    lastStart = index;
                }

                if (t.Text.Equals("CASE", StringComparison.OrdinalIgnoreCase) && OpensBlock(tokens, index))
                {
                    // In statement position a CASE statement (closed by END CASE), anywhere else an expression.
                    blocks.Add(atStart ? BlockBegin : CaseExpression);
                }
                else if (OpensBlock(tokens, index)
                         // `case when a then begin end`: in a CASE expression, a value — a column called begin.
                         && !(inCaseExpression && IsWordAt(tokens, index - 1, "THEN", "ELSE"))
                         && (atStart || OwnsBody(tokens, lastStart, index)))
                {
                    blocks.Add(BlockBegin);
                    openedBlock[index] = true;
                }
                else if (atStart && OpensCompound(tokens, index))
                {
                    blocks.Add(t.Text.ToUpperInvariant());
                }
                else if (tokens[index].Text.Equals("END", StringComparison.OrdinalIgnoreCase))
                {
                    Close(blocks, tokens, index);
                }
            }

            current.Add(t);
        }
        Flush(sql.Length);
        return statements;
    }

    /// <summary>What a <c>BEGIN</c> or a <c>CASE</c> pushes: both are closed by a plain <c>END</c> (a CASE
    /// statement's <c>END CASE</c> too).</summary>
    private const string BlockBegin = "BEGIN";

    /// <summary>What a CASE <em>expression</em> pushes — a CASE outside statement position. Closed by a plain
    /// <c>END</c> like a block, but its <c>THEN</c> / <c>ELSE</c> lead to a value, never a body: <c>case when a
    /// then begin end</c> reads a column called begin.</summary>
    private const string CaseExpression = "CASE";

    /// <summary>
    /// <c>END IF</c> / <c>END WHILE</c> / … closes the compound on top when it is that compound, and nothing
    /// otherwise — an opener this scan did not count must not take an outer block's closer with it. A plain
    /// <c>END</c> (or <c>END CASE</c>) closes the innermost <c>BEGIN</c> / <c>CASE</c>, and any compound
    /// still open above it, which can only be one this scan counted wrongly.
    /// <para>
    /// <c>END FOR UPDATE</c> / <c>END FOR SHARE</c> with no FOR loop on top is a CASE expression's END before
    /// a locking clause (<c>… where id = case … end for update</c>): a plain END. Read as a FOR loop's closer
    /// it left the CASE open, and every statement after it ran on as part of this one.
    /// </para>
    /// </summary>
    private static void Close(List<string> blocks, IReadOnlyList<MySqlToken> tokens, int index)
    {
        var next = index + 1 < tokens.Count && tokens[index + 1].IsWord ? tokens[index + 1].Text.ToUpperInvariant() : "";
        if (CompoundKeywords.Contains(next))
        {
            if (blocks.Count > 0 && blocks[^1] == next)
            {
                blocks.RemoveAt(blocks.Count - 1);
                return;
            }
            if (!(next == "FOR" && IsLockingClause(tokens, index + 2)))
            {
                return;
            }
        }
        while (blocks.Count > 0)
        {
            var top = blocks[^1];
            blocks.RemoveAt(blocks.Count - 1);
            if (top is BlockBegin or CaseExpression)
            {
                return;
            }
        }
    }

    /// <summary>The compounds closed by <c>END</c> plus their own keyword — not counted by
    /// <see cref="OpensBlock"/>, and opened only in statement position (<see cref="OpensCompound"/>).</summary>
    private static readonly HashSet<string> CompoundKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "IF", "LOOP", "WHILE", "REPEAT", "FOR",
    };

    /// <summary>
    /// Whether the word at <paramref name="index"/> is where a statement begins: the statement's first token;
    /// the first after a <c>;</c> kept inside a body; after a label (<c>lbl:</c> in statement position); or
    /// the first of a body — after <c>THEN</c>, <c>ELSE</c>, <c>DO</c>, a block's <c>BEGIN</c> (or
    /// <c>BEGIN NOT ATOMIC</c>), <c>LOOP</c> or <c>REPEAT</c>. A <c>THEN</c>/<c>ELSE</c> may also be a CASE
    /// expression's, and <c>DO</c> the <c>DO expr</c> statement, which is why <see cref="OpensCompound"/>
    /// asks more of an <c>IF</c> or <c>REPEAT</c> than the position.
    /// </summary>
    private static bool AtStatementStart(
        IReadOnlyList<MySqlToken> tokens, int index, bool firstOfStatement, int lastStart, bool[] openedBlock,
        List<string> blocks)
    {
        if (firstOfStatement)
        {
            return true;
        }
        var previous = tokens[index - 1];
        if (previous.Kind == MySqlTokenKind.Delimiter)
        {
            return true;
        }
        if (previous.Kind == MySqlTokenKind.Punctuation && previous.Text == ":")
        {
            return index >= 2 && lastStart == index - 2 && tokens[index - 2].IsWord;
        }
        if (!previous.IsWord)
        {
            return false;
        }
        if (previous.Text.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            return openedBlock[index - 1];
        }
        // `DO` and `ATOMIC` are not reserved: `select * from do for update` names a table, and the FOR after it
        // opening a loop swallowed every statement that followed. A body follows DO only as a WHILE's or a FOR
        // loop's, or an event's (`… on schedule … do`) — never the `DO expr` statement's own — and ATOMIC only
        // as `BEGIN NOT ATOMIC`.
        if (previous.Text.Equals("DO", StringComparison.OrdinalIgnoreCase))
        {
            return lastStart != index - 1
                && (blocks.Count > 0 && blocks[^1] is "WHILE" or "FOR" || DefinesEvent(tokens, lastStart, index));
        }
        if (previous.Text.Equals("ATOMIC", StringComparison.OrdinalIgnoreCase))
        {
            return index >= 3 && IsWordAt(tokens, index - 2, "NOT", "NOT") && openedBlock[index - 3];
        }
        return BodyOpeners.Contains(previous.Text);
    }

    /// <summary>
    /// Whether a <c>BEGIN</c> that is not in statement position sits where a body can: after a stored program's
    /// header (<c>create procedure p() …</c>, <c>… for each row</c>, <c>create function … returns int
    /// deterministic</c>) or a handler's (<c>declare continue handler for sqlexception</c>). Those are the only
    /// places MySQL puts a body other than statement position, so anywhere else a <c>begin</c> is a name — <c>lock
    /// tables begin read</c>, <c>handler begin open</c>, <c>call begin()</c>, <c>with begin (n) as …</c>,
    /// <c>update t begin set …</c> — and opening a block there swallowed every statement after it.
    /// <paramref name="lead"/> is the first word of the statement the BEGIN is in (−1 when unknown).
    /// </summary>
    private static bool OwnsBody(IReadOnlyList<MySqlToken> tokens, int lead, int index)
        => Defines(tokens, lead, index, "CREATE", RoutineKinds) || Defines(tokens, lead, index, "DECLARE", HandlerKind);

    /// <summary>Whether the statement led by <paramref name="lead"/> is a <c>CREATE</c> / <c>ALTER EVENT</c>,
    /// whose <c>DO</c> is followed by the event's body.</summary>
    private static bool DefinesEvent(IReadOnlyList<MySqlToken> tokens, int lead, int index)
        => Defines(tokens, lead, index, "CREATE", EventKind) || Defines(tokens, lead, index, "ALTER", EventKind);

    /// <summary>Whether the statement whose first word, at <paramref name="lead"/>, is <paramref name="leadWord"/>
    /// names one of <paramref name="kinds"/> before <paramref name="index"/> — as a bare word at the same depth,
    /// not a qualified name's part.</summary>
    private static bool Defines(
        IReadOnlyList<MySqlToken> tokens, int lead, int index, string leadWord, IReadOnlySet<string> kinds)
    {
        if (lead < 0 || lead >= index || !IsWordAt(tokens, lead, leadWord, leadWord))
        {
            return false;
        }
        for (var i = lead + 1; i < index; i++)
        {
            var t = tokens[i];
            if (t.IsWord && t.Depth == tokens[index].Depth && !IsPunctuation(tokens, i - 1, ".") && kinds.Contains(t.Text))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly HashSet<string> HandlerKind = new(StringComparer.OrdinalIgnoreCase) { "HANDLER" };

    private static readonly HashSet<string> EventKind = new(StringComparer.OrdinalIgnoreCase) { "EVENT" };

    /// <summary>What a <c>CREATE</c> defines when its body may be a <c>BEGIN … END</c>.</summary>
    private static readonly HashSet<string> RoutineKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROCEDURE", "FUNCTION", "TRIGGER", "EVENT", "PACKAGE",
    };

    /// <summary>Words a body's first statement follows. Shared with <see cref="MySqlWriteGuard"/>'s interior
    /// read, which must start a statement exactly where this splitter does.</summary>
    internal static readonly HashSet<string> BodyOpeners = new(StringComparer.OrdinalIgnoreCase)
    {
        "THEN", "ELSE", "DO", "LOOP", "REPEAT", "ATOMIC",
    };

    /// <summary>
    /// A compound statement's opener, in statement position. <c>WHILE</c>, <c>LOOP</c> and <c>FOR</c> are
    /// reserved and cannot be anything else there. <c>REPEAT</c> followed by <c>(</c> is the
    /// <c>repeat(str, n)</c> function (<c>then repeat('-', 3)</c> in a CASE expression). <c>IF</c> is the
    /// compound only when its condition runs into a <c>THEN</c>: that is what separates <c>if (a &gt; 1) then
    /// …</c> from the <c>if(a, b, c)</c> function after a CASE expression's <c>THEN</c>, and keeps
    /// <c>if exists</c> / <c>if not exists</c> out wherever no <c>THEN</c> follows.
    /// </summary>
    private static bool OpensCompound(IReadOnlyList<MySqlToken> tokens, int index)
    {
        var word = tokens[index].Text.ToUpperInvariant();
        switch (word)
        {
            case "WHILE" or "LOOP" or "FOR":
                return true;
            case "REPEAT":
                return !IsPunctuation(tokens, index + 1, "(");
            case "IF":
                return ConditionRunsIntoThen(tokens, index);
            default:
                return false;
        }
    }

    /// <summary>The first of <c>THEN</c>, <c>WHEN</c>, <c>ELSE</c>, <c>END</c> or a delimiter at the IF's
    /// own depth is <c>THEN</c>.</summary>
    private static bool ConditionRunsIntoThen(IReadOnlyList<MySqlToken> tokens, int index)
    {
        var depth = tokens[index].Depth;
        for (var i = index + 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind is MySqlTokenKind.Delimiter or MySqlTokenKind.Directive)
            {
                return false;
            }
            if (!t.IsWord || t.Depth != depth)
            {
                continue;
            }
            if (t.Text.Equals("THEN", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (ConditionStops.Contains(t.Text))
            {
                return false;
            }
        }
        return false;
    }

    private static readonly HashSet<string> ConditionStops = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHEN", "ELSE", "END",
    };

    /// <summary>The word after <c>FOR</c> in a locking clause: <c>FOR UPDATE</c>, <c>FOR SHARE</c>.</summary>
    private static bool IsLockingClause(IReadOnlyList<MySqlToken> tokens, int index)
        => index < tokens.Count && tokens[index].IsWord
           && (tokens[index].Text.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
               || tokens[index].Text.Equals("SHARE", StringComparison.OrdinalIgnoreCase));

    private static bool IsPunctuation(IReadOnlyList<MySqlToken> tokens, int index, string text)
        => index >= 0 && index < tokens.Count && tokens[index].Kind == MySqlTokenKind.Punctuation
           && tokens[index].Text == text;

    private static bool IsWordAt(IReadOnlyList<MySqlToken> tokens, int index, string a, string b)
        => index >= 0 && index < tokens.Count && tokens[index].IsWord
           && (tokens[index].Text.Equals(a, StringComparison.OrdinalIgnoreCase)
               || tokens[index].Text.Equals(b, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>CASE</c> opens (its <c>END</c> / <c>END CASE</c> closes it) — except the <c>CASE</c> of
    /// <c>END CASE</c> itself. <c>BEGIN</c> opens a block
    /// unless it starts a transaction: <c>BEGIN;</c>, <c>BEGIN WORK</c>, a <c>BEGIN</c> at the very end, and
    /// <c>XA BEGIN</c> — or is a column (<see cref="IsColumnBegin"/>). MariaDB's anonymous
    /// <c>BEGIN NOT ATOMIC</c> is a block.
    /// </summary>
    private static bool OpensBlock(IReadOnlyList<MySqlToken> tokens, int index)
    {
        var word = tokens[index].Text;
        if (word.Equals("CASE", StringComparison.OrdinalIgnoreCase))
        {
            // The CASE of `END CASE` belongs to the END that just closed the block, and opens nothing.
            var afterEnd = index > 0 && tokens[index - 1].IsWord
                && tokens[index - 1].Text.Equals("END", StringComparison.OrdinalIgnoreCase);
            return !afterEnd;
        }
        if (!word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (index > 0 && tokens[index - 1].IsWord
            && tokens[index - 1].Text.Equals("XA", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (index + 1 >= tokens.Count)
        {
            return false;
        }
        var next = tokens[index + 1];
        if (next.Kind == MySqlTokenKind.Delimiter)
        {
            return false;
        }
        if (next.IsWord && next.Text.Equals("WORK", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return !IsColumnBegin(tokens, index);
    }

    /// <summary>
    /// <c>begin</c> is not reserved, so it is also a column name: <c>select begin, end from shifts</c>. Read as
    /// a block it would swallow every statement after it into this one. A body follows a routine header, a
    /// label, <c>THEN</c>/<c>ELSE</c>/<c>DO</c>/<c>ROW</c>/a handler — never a <c>.</c>, a comma or an
    /// operator before it, nor a word that only an expression puts there (<see cref="ColumnPrecursors"/>);
    /// and a body's first token is a statement's first word, a <c>(</c> or its <c>END</c> — never an
    /// operator, a comma, or a word that only continues an expression or a query
    /// (<see cref="ColumnFollowers"/>).
    /// </summary>
    private static bool IsColumnBegin(IReadOnlyList<MySqlToken> tokens, int index)
    {
        var next = tokens[index + 1];
        if (next.Kind == MySqlTokenKind.Punctuation && next.Text != "(")
        {
            return true;
        }
        if (next.IsWord && ColumnFollowers.Contains(next.Text))
        {
            return true;
        }
        if (index == 0)
        {
            return false;
        }
        var previous = tokens[index - 1];
        if (previous.Kind == MySqlTokenKind.Punctuation)
        {
            return previous.Text is not (")" or ":" or ";");
        }
        return previous.IsWord && ColumnPrecursors.Contains(previous.Text);
    }

    /// <summary>
    /// Words that can follow a <c>begin</c> column but never open a body: none of them starts a statement.
    /// <c>NOT</c> is absent (<c>BEGIN NOT ATOMIC</c>), and so are <c>END</c> (an empty body) and the
    /// statement leads <c>DESC</c>, <c>FOR</c>, <c>VALUES</c>, <c>TABLE</c>.
    /// </summary>
    private static readonly HashSet<string> ColumnFollowers = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "AS", "INTO",
        "THEN", "WHEN", "ELSE",
        "AND", "OR", "XOR", "IS", "IN", "LIKE", "BETWEEN", "REGEXP", "RLIKE", "SOUNDS", "DIV", "MOD", "COLLATE",
        "WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "UNION", "EXCEPT", "INTERSECT", "WINDOW",
        "JOIN", "INNER", "LEFT", "RIGHT", "CROSS", "NATURAL", "STRAIGHT_JOIN", "ON", "USING",
    };

    /// <summary>
    /// Words after which <c>begin</c> can only be an operand or a name: a select list, a predicate, a join
    /// condition, an ordering — and a column or variable being declared or altered (<c>alter table t add
    /// column begin datetime</c>, <c>declare begin int</c>), where the word after it is a type, not a
    /// statement, so the follower check cannot tell. <c>THEN</c>, <c>ELSE</c> and <c>DO</c> are absent — a body
    /// follows those.
    /// </summary>
    private static readonly HashSet<string> ColumnPrecursors = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "DISTINCT", "ALL", "BY", "WHERE", "AND", "OR", "XOR", "NOT", "SET",
        "WHEN", "HAVING", "ON", "IN", "IS", "LIKE", "BETWEEN", "REGEXP", "RLIKE", "DIV", "MOD",
        "CASE", "FROM", "JOIN", "INTO", "UPDATE", "TABLE", "RETURNING", "PARTITION",
        "ADD", "COLUMN", "MODIFY", "CHANGE", "DECLARE",
        // A stored program named begin (`create procedure begin()`), and a trigger ordered after one.
        "PROCEDURE", "FUNCTION", "TRIGGER", "EVENT", "PACKAGE", "FOLLOWS", "PRECEDES",
        // A trigger whose body is a CALL of a routine called begin, and a handler for a condition called begin.
        "CALL", "FOR",
    };

    /// <summary>
    /// A <c>DELIMITER x</c> line at the start of a statement. The new delimiter is the first
    /// whitespace-free run after the keyword, as the <c>mysql</c> client reads it.
    /// </summary>
    private static bool IsDirectiveStart(
        string sql, int i, bool statementHasTokens, out int lineEnd, out string delimiter)
    {
        lineEnd = i;
        delimiter = DefaultDelimiter;
        const string Keyword = "DELIMITER";
        if (statementHasTokens
            || i + Keyword.Length >= sql.Length
            || string.Compare(sql, i, Keyword, 0, Keyword.Length, StringComparison.OrdinalIgnoreCase) != 0
            || !char.IsWhiteSpace(sql[i + Keyword.Length])
            || sql[i + Keyword.Length] == '\n')
        {
            return false;
        }

        var end = sql.IndexOf('\n', i);
        lineEnd = end < 0 ? sql.Length : end;
        var argument = sql[(i + Keyword.Length)..lineEnd].Trim();
        var space = argument.IndexOfAny([' ', '\t']);
        if (space >= 0)
        {
            argument = argument[..space];
        }
        if (argument.Length == 0)
        {
            return false;
        }

        delimiter = argument;
        return true;
    }

    /// <summary>Index just past the closing quote of the literal opening at <paramref name="i"/>, or the
    /// end of the buffer when it never closes. A doubled delimiter is an escaped one; a backslash escapes
    /// the next character where <paramref name="backslashEscapes"/>.</summary>
    private static int SkipQuoted(string sql, int i, char quote, bool backslashEscapes)
    {
        i++;
        while (i < sql.Length)
        {
            var ch = sql[i];
            if (backslashEscapes && ch == '\\')
            {
                i += 2;
                continue;
            }
            if (ch == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }

    private static bool AtDelimiter(string sql, int i, string delimiter)
        => delimiter != DefaultDelimiter && string.CompareOrdinal(sql, i, delimiter, 0, delimiter.Length) == 0;

    private static bool PrecededByNameChar(string sql, int i) => i > 0 && IsNameChar(sql[i - 1]);

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c > '\u007f';

    /// <summary>
    /// <paramref name="sql"/> with every executable-comment marker (<c>/*!NNNNN</c>, <c>/*M!NNNNNN</c>) and
    /// its closing <c>*/</c> blanked to spaces, so a lexer that hides comments sees the code the server
    /// runs. Offsets are preserved. The vendored grammar puts <c>/*! … */</c> on a channel of its own, which
    /// is right for completion and wrong for <see cref="ExternalSqlPolicy"/>: <c>select
    /// /*!load_file('/etc/passwd')*/</c> must be read as the call it is.
    /// </summary>
    public static string RevealExecutableComments(string sql)
    {
        if (string.IsNullOrEmpty(sql) || !sql.Contains("/*", StringComparison.Ordinal))
        {
            return sql;
        }

        var chars = sql.ToCharArray();
        var i = 0;
        var inExecutable = false;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(sql, i, c, backslashEscapes: c != '`');
                continue;
            }
            if (c == '#' || (c == '-' && i + 2 < sql.Length && sql[i + 1] == '-' && char.IsWhiteSpace(sql[i + 2])))
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }
                continue;
            }
            if (inExecutable && c == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                inExecutable = false;
                i += 2;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var marker = i + 2;
                if (marker + 1 < sql.Length && sql[marker] == 'M' && sql[marker + 1] == '!')
                {
                    marker++;
                }
                if (marker < sql.Length && sql[marker] == '!' && !inExecutable)
                {
                    var end = marker + 1;
                    while (end < sql.Length && char.IsDigit(sql[end]))
                    {
                        end++;
                    }
                    for (var k = i; k < end; k++)
                    {
                        chars[k] = ' ';
                    }
                    inExecutable = true;
                    i = end;
                    continue;
                }
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + 2;
                continue;
            }
            i++;
        }
        return new string(chars);
    }

    /// <summary>The uppercased text of every bare word at parenthesis depth 0.</summary>
    public static IReadOnlyList<string> TopLevelWords(IReadOnlyList<MySqlToken> tokens)
    {
        var words = new List<string>();
        foreach (var t in tokens)
        {
            if (t.IsWord && t.Depth == 0)
            {
                words.Add(t.Text.ToUpperInvariant());
            }
        }
        return words;
    }

    /// <summary>Whether <paramref name="sql"/> holds a <c>DELIMITER</c> client directive anywhere.</summary>
    public static bool HasDirective(string sql)
    {
        if (string.IsNullOrEmpty(sql) || sql.IndexOf("delimiter", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }
        foreach (var t in Tokenize(sql))
        {
            if (t.Kind == MySqlTokenKind.Directive)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The uppercased text of every bare word at any depth.</summary>
    public static IReadOnlyList<string> AllWords(IReadOnlyList<MySqlToken> tokens)
    {
        var words = new List<string>();
        foreach (var t in tokens)
        {
            if (t.IsWord)
            {
                words.Add(t.Text.ToUpperInvariant());
            }
        }
        return words;
    }
}

/// <summary>
/// Whether a backslash escapes the next character inside a <c>'…'</c> and a <c>"…"</c> token — the one
/// lexical rule a server setting changes. The default (both) is MySQL's and MariaDB's out of the box.
/// <c>NO_BACKSLASH_ESCAPES</c> turns it off for both, and <c>ANSI_QUOTES</c> makes <c>"</c> delimit an
/// identifier, which never had the escape. Those are per-server (and per-session) facts this text-only layer
/// cannot see, and they decide where a literal <em>ends</em>: <c>select 'a\'; drop table t; -- '</c> is one
/// SELECT by default and a SELECT and a DROP under <c>NO_BACKSLASH_ESCAPES</c> (measured on MariaDB 11.4).
/// The write guard therefore reads a batch under <see cref="Alternates"/> too (§1.2).
/// </summary>
internal readonly record struct MySqlQuoting(bool SingleQuoteBackslash, bool DoubleQuoteBackslash)
{
    public static MySqlQuoting Default { get; } = new(true, true);

    /// <summary><c>NO_BACKSLASH_ESCAPES</c> (with or without <c>ANSI_QUOTES</c>), and <c>ANSI_QUOTES</c>
    /// alone — the only other two readings a server can give the same text.</summary>
    public static IReadOnlyList<MySqlQuoting> Alternates { get; } = [new(false, false), new(true, false)];

    public bool Escapes(char quote) => quote switch
    {
        '\'' => SingleQuoteBackslash,
        '"' => DoubleQuoteBackslash,
        _ => false,
    };
}
