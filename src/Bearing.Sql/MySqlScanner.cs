
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
    public static IReadOnlyList<MySqlToken> Tokenize(string sql)
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
                i = SkipQuoted(sql, i, sql[i], backslashEscapes: true);
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
                    i = SkipQuoted(sql, i, sql[i], backslashEscapes: sql[i] != '`');
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
    /// Split <paramref name="sql"/> into statements: at the delimiter in force (outside a <c>BEGIN … END</c>
    /// body and outside parentheses when it is <c>;</c>; anywhere when a <c>DELIMITER</c> directive changed
    /// it, which is the directive's whole purpose), and at every <c>DELIMITER</c> line, which belongs to no
    /// statement. Empty statements are dropped.
    /// </summary>
    public static IReadOnlyList<MySqlStatement> Split(string sql)
    {
        var statements = new List<MySqlStatement>();
        if (string.IsNullOrWhiteSpace(sql))
        {
            return statements;
        }

        var tokens = Tokenize(sql);
        var current = new List<MySqlToken>();
        var from = 0;
        var block = 0;

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
                block = 0;
                continue;
            }

            if (t.Kind == MySqlTokenKind.Delimiter)
            {
                var custom = t.Text != DefaultDelimiter;
                if (custom || (t.Depth == 0 && block == 0))
                {
                    Flush(t.Start);
                    from = t.Start + t.Length;
                    block = 0;
                    continue;
                }

                // A `;` inside a body is part of the statement, as punctuation.
                current.Add(t with { Kind = MySqlTokenKind.Punctuation });
                continue;
            }

            if (t.IsWord && t.Depth == 0)
            {
                if (OpensBlock(tokens, index))
                {
                    block++;
                }
                else if (ClosesBlock(tokens, index) && block > 0)
                {
                    block--;
                }
            }

            current.Add(t);
        }
        Flush(sql.Length);
        return statements;
    }

    /// <summary>
    /// <c>CASE</c> always opens (its <c>END</c> / <c>END CASE</c> closes it). <c>BEGIN</c> opens a block
    /// unless it starts a transaction: <c>BEGIN;</c>, <c>BEGIN WORK</c>, a <c>BEGIN</c> at the very end, and
    /// <c>XA BEGIN</c>. MariaDB's anonymous <c>BEGIN NOT ATOMIC</c> is a block.
    /// </summary>
    private static bool OpensBlock(IReadOnlyList<MySqlToken> tokens, int index)
    {
        var word = tokens[index].Text;
        if (word.Equals("CASE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
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
        return !(next.IsWord && next.Text.Equals("WORK", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An <c>END</c> that closes a <c>BEGIN</c> or a <c>CASE</c> — not the <c>END</c> of
    /// <c>END IF</c> / <c>END LOOP</c> / <c>END WHILE</c> / <c>END REPEAT</c> / <c>END FOR</c>, whose openers
    /// are not counted either.</summary>
    private static bool ClosesBlock(IReadOnlyList<MySqlToken> tokens, int index)
    {
        if (!tokens[index].Text.Equals("END", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var next = index + 1 < tokens.Count && tokens[index + 1].IsWord ? tokens[index + 1].Text : "";
        return !NonBlockEnds.Contains(next);
    }

    private static readonly HashSet<string> NonBlockEnds = new(StringComparer.OrdinalIgnoreCase)
    {
        "IF", "LOOP", "WHILE", "REPEAT", "FOR",
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
