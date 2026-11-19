namespace Bearing.Data.DynamoDb;

/// <summary>
/// The table (and index) a PartiQL statement names: <c>from "T"</c>, <c>from T</c> and <c>from "T"."Idx"</c> give
/// table <c>T</c> and, for the last, index <c>Idx</c>; <c>insert into</c>, <c>update</c> and <c>delete from</c> give
/// the table. Names are returned unquoted, with <c>""</c> collapsed to <c>"</c>.
/// </summary>
internal sealed record PartiQlTarget(string Table, string? Index);

/// <summary>
/// The three questions the executor asks of a script: where does each statement end, what verb leads it, and
/// which table does it name. <c>Bearing.Data</c> may not reference <c>Bearing.Sql</c> (§2.2), so this is the
/// small lexical copy of what <c>Bearing.Sql</c>'s PartiQL scan does — and it must never split <em>coarser</em>
/// than that scan does: a <c>;</c> the write guard sees as a statement boundary has to be one here too, or a
/// write could ride behind a read the guard had already cleared. It splits on <c>;</c> outside <c>'…'</c>
/// (<c>''</c> escapes), <c>"…"</c> (<c>""</c> escapes), <c>--</c> to the end of the line and <c>/* … */</c>
/// (not nested — a nested reading would only make a boundary vanish, which is the coarser mistake).
/// <para>
/// Each statement keeps its text exactly as typed (trimmed at both ends, comments inside it included): it is
/// sent to <c>ExecuteStatement</c> as written. A segment that holds nothing but whitespace and comments is
/// dropped — there is nothing to send. An unterminated string, quoted name or comment runs to the end of the
/// text, so the rest of the script stays one statement rather than being guessed at.
/// </para>
/// </summary>
internal static class PartiQlBatch
{
    /// <summary>The statements of <paramref name="script"/>, in order, trimmed, with empty ones dropped.</summary>
    public static IReadOnlyList<string> Split(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var statements = new List<string>();
        var segmentStart = 0;
        var hasCode = false;

        foreach (var token in Lex(script))
        {
            if (token.Kind == TokenKind.Semicolon)
            {
                Close(token.Start);
                segmentStart = token.End;
                continue;
            }

            hasCode = true;
        }

        Close(script.Length);

        return statements;

        void Close(int end)
        {
            if (hasCode)
            {
                var text = script[segmentStart..end].Trim();
                if (text.Length > 0) statements.Add(text);
            }

            hasCode = false;
        }
    }

    /// <summary>The statement's first keyword, upper-cased (<c>SELECT</c>, <c>INSERT</c>, …); empty when it does
    /// not start with a word. Leading comments are skipped.</summary>
    public static string LeadVerb(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var first = Lex(statement).FirstOrDefault(t => t.Kind != TokenKind.Semicolon);

        return first is { Kind: TokenKind.Word }
            ? first.Text.ToUpperInvariant()
            : "";
    }

    /// <summary>True when <paramref name="statement"/> is <c>SELECT *</c> — every attribute of every item, the
    /// table's key attributes included, so those are columns even before an item has shown them. A projection
    /// returns only what it names.</summary>
    public static bool SelectsAll(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var tokens = Lex(statement)
            .Where(t => t.Kind != TokenKind.Semicolon)
            .Take(2)
            .ToList();

        return tokens.Count == 2
            && IsWord(tokens[0], "SELECT")
            && tokens[1] is { Kind: TokenKind.Other, Text: "*" };
    }

    /// <summary>
    /// True when <paramref name="statement"/> has a top-level <c>WHERE</c> after its <c>FROM</c> — a filter, or a key
    /// condition, either of which can make DynamoDB evaluate more items than it returns. False when there is no
    /// <c>FROM</c> or no <c>WHERE</c> at depth zero (a <c>WHERE</c> inside parentheses is not the statement's).
    /// </summary>
    public static bool HasWhere(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var tokens = Lex(statement)
            .Where(t => t.Kind != TokenKind.Semicolon)
            .ToList();

        var from = IndexOfFrom(tokens);
        if (from < 0) return false;

        var depth = 0;
        for (var i = from; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (token.Kind == TokenKind.Other && token.Text == "(") depth++;
            else if (token.Kind == TokenKind.Other && token.Text == ")") depth--;
            else if (depth == 0 && IsWord(token, "WHERE")) return true;
        }

        return false;
    }

    /// <summary>
    /// The attribute names a <c>SELECT</c> projects, in the order written, when every projected item is one plain
    /// or quoted name (<c>SELECT pk, "Order Date" FROM t</c>). Null for <c>SELECT *</c>, for a statement that is
    /// not a <c>SELECT</c>, and for any item this does not read as one name — a path (<c>a.b</c>, <c>a[0]</c>), a
    /// function, an alias: null is "could not tell", and the layout then goes by what the items hold.
    /// </summary>
    public static IReadOnlyList<string>? Projection(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var tokens = Lex(statement)
            .Where(t => t.Kind != TokenKind.Semicolon)
            .ToList();

        if (tokens.Count < 2 || !IsWord(tokens[0], "SELECT")) return null;

        var from = IndexOfFrom(tokens);
        if (from < 0) return null;

        var names = new List<string>();
        var item = new List<Token>();

        // tokens[from - 1] is FROM itself; the projection is everything between SELECT and it.
        for (var i = 1; i <= from - 1; i++)
        {
            var atEnd = i == from - 1;
            var token = tokens[i];

            if (!atEnd && !(token.Kind == TokenKind.Other && token.Text == ","))
            {
                item.Add(token);
                continue;
            }

            if (item is not [{ Kind: TokenKind.Word or TokenKind.QuotedName, Text.Length: > 0 } only]) return null;

            names.Add(only.Text);
            item.Clear();
        }

        return names.Count == 0 ? null : names;
    }

    /// <summary>
    /// The table and index <paramref name="statement"/> names, or null when the text is not a shape this reads
    /// (no <c>FROM</c>, no name after it, an unknown lead verb). Null is "could not tell", never "none".
    /// </summary>
    public static PartiQlTarget? Target(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var tokens = Lex(statement)
            .Where(t => t.Kind != TokenKind.Semicolon)
            .ToList();

        if (tokens.Count == 0 || tokens[0].Kind != TokenKind.Word) return null;

        var at = tokens[0].Text.ToUpperInvariant() switch
        {
            "SELECT" or "DELETE" => IndexOfFrom(tokens),
            "INSERT" => IndexAfterKeyword(tokens, "INTO"),
            "UPDATE" => 1,
            _ => -1,
        };

        return at < 0 ? null : ReadName(tokens, at);
    }

    private static int IndexOfFrom(List<Token> tokens)
    {
        var depth = 0;

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (token.Kind == TokenKind.Other && token.Text == "(") depth++;
            else if (token.Kind == TokenKind.Other && token.Text == ")") depth--;
            else if (depth == 0 && IsWord(token, "FROM")) return i + 1;
        }

        return -1;
    }

    private static int IndexAfterKeyword(List<Token> tokens, string keyword)
        => tokens.Count > 1 && IsWord(tokens[1], keyword) ? 2 : -1;

    private static PartiQlTarget? ReadName(List<Token> tokens, int at)
    {
        var table = NameAt(tokens, at);
        if (table is null) return null;

        var hasIndex = at + 2 < tokens.Count
            && tokens[at + 1] is { Kind: TokenKind.Other, Text: "." };
        var index = hasIndex ? NameAt(tokens, at + 2) : null;

        return new PartiQlTarget(table, index);
    }

    private static string? NameAt(List<Token> tokens, int at)
    {
        if (at >= tokens.Count) return null;

        var token = tokens[at];

        return token.Kind is TokenKind.Word or TokenKind.QuotedName && token.Text.Length > 0
            ? token.Text
            : null;
    }

    private static bool IsWord(Token token, string word)
        => token.Kind == TokenKind.Word && string.Equals(token.Text, word, StringComparison.OrdinalIgnoreCase);

    // ---- the lexer ---------------------------------------------------------------------------------

    private enum TokenKind
    {
        Word,
        QuotedName,
        Literal,
        Semicolon,
        Other,
    }

    /// <summary>One token of code. <see cref="Text"/> is the word, or the quoted name unquoted; whitespace and
    /// comments never become tokens.</summary>
    private sealed record Token(TokenKind Kind, int Start, int End, string Text);

    private static IEnumerable<Token> Lex(string text)
    {
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && Peek(text, i + 1) == '-')
            {
                var newline = text.IndexOf('\n', i);
                i = newline < 0 ? text.Length : newline + 1;
            }
            else if (c == '/' && Peek(text, i + 1) == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
            }
            else if (c == '\'' || c == '"')
            {
                var start = i;
                var name = Quoted(text, ref i, c);

                yield return new Token(c == '"' ? TokenKind.QuotedName : TokenKind.Literal, start, i, name);
            }
            else if (c == ';')
            {
                yield return new Token(TokenKind.Semicolon, i, i + 1, ";");
                i++;
            }
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                var start = i;

                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;

                yield return new Token(TokenKind.Word, start, i, text[start..i]);
            }
            else
            {
                yield return new Token(TokenKind.Other, i, i + 1, c.ToString());
                i++;
            }
        }
    }

    /// <summary>Reads a quoted run starting at <paramref name="i"/> (on the opening quote), leaving
    /// <paramref name="i"/> after the closing one — or at the end when it never closes.</summary>
    private static string Quoted(string text, ref int i, char quote)
    {
        var body = new System.Text.StringBuilder();

        i++;

        while (i < text.Length)
        {
            if (text[i] != quote)
            {
                body.Append(text[i]);
                i++;
            }
            else if (Peek(text, i + 1) == quote)
            {
                body.Append(quote);
                i += 2;
            }
            else
            {
                i++;

                return body.ToString();
            }
        }

        return body.ToString();
    }

    private static char Peek(string text, int at) => at < text.Length ? text[at] : '\0';
}
