using System.Text;

namespace Bearing.Sql;

/// <summary>
/// The two lexical questions about MySQL literals asked outside the completion engine — is the caret inside
/// one, and what does a statement look like with its literal values taken out (#22, §1.3). The counterpart
/// of <see cref="TSqlLiterals"/>, answered from <see cref="MySqlScanner"/> for the same reasons: they are
/// lexical, one runs per keystroke and the other on the write path of every logged statement.
/// <para>
/// MySQL needs its own answer rather than Postgres' because of the backslash escape: the PG lexer reads
/// <c>'it\'s'</c> as a string that closes at the second quote, so the caret after it looks "inside a
/// string" to completion and the redactor leaves <c>s'</c> and everything after it in the log.
/// </para>
/// </summary>
public static class MySqlLiterals
{
    /// <summary>Whether <paramref name="offset"/> sits inside a character literal. The opening delimiter's
    /// own position does not count, matching <see cref="SqlStringLiterals.Contains"/>.</summary>
    public static bool Contains(string? sql, int offset)
    {
        if (string.IsNullOrEmpty(sql) || offset <= 0 || offset > sql.Length)
        {
            return false;
        }

        foreach (var token in MySqlScanner.Tokenize(sql))
        {
            if (token.Kind != MySqlTokenKind.Text)
            {
                continue;
            }
            // The same bounds the Postgres and T-SQL answers use, so the three dialects agree on where a
            // literal begins and ends for completion.
            if (offset > token.Start && offset <= token.Start + token.Length)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// <paramref name="sql"/> with every literal value replaced by <c>?</c>. A quoted literal keeps its
    /// shell and prefix (<c>N'Ada'</c> → <c>N'?'</c>, <c>X'DEAD'</c> → <c>X'?'</c>); a number — hex and bit
    /// forms included (<c>0xDEADBEEF</c>, <c>0b101</c>) — is replaced whole. Identifiers are never touched.
    /// <para>
    /// A statement holding a backslash is read twice: under the default quoting and under
    /// <c>NO_BACKSLASH_ESCAPES</c> (<see cref="MySqlQuoting"/>), which disagree on where its literals end. Every
    /// span either reading calls a literal is taken out, overlapping spans merged — the log cannot know the
    /// server's <c>sql_mode</c>, and a value left in it is the failure that matters, so the shape gives way
    /// (<c>'it\'s' and b = 2</c> → <c>'?</c>: the second reading leaves a quote open to the end).
    /// </para>
    /// </summary>
    public static string RedactLiterals(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return sql ?? "";
        }

        var spans = LiteralSpans(sql);
        var sb = new StringBuilder(sql.Length);
        var copied = 0;

        foreach (var (start, end, placeholder) in spans)
        {
            sb.Append(sql, copied, start - copied);
            sb.Append(placeholder);
            copied = end;
        }

        sb.Append(sql, copied, sql.Length - copied);
        return sb.ToString();
    }

    /// <summary>The literal spans to replace, in order and non-overlapping, each with its placeholder.</summary>
    private static List<(int Start, int End, string Placeholder)> LiteralSpans(string sql)
    {
        var literals = new List<MySqlToken>();
        Collect(MySqlScanner.Tokenize(sql), literals);
        if (sql.Contains('\\'))
        {
            Collect(MySqlScanner.Tokenize(sql, new MySqlQuoting(false, false)), literals);
            literals.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        }

        var spans = new List<(int Start, int End, string Placeholder)>();
        var i = 0;
        while (i < literals.Count)
        {
            var first = literals[i];
            var end = first.Start + first.Length;
            // Sorted by start, longest first: `first` is the widest token at its start, so the span grew
            // only if a later-starting token overran it.
            for (i++; i < literals.Count && literals[i].Start < end; i++)
            {
                end = Math.Max(end, literals[i].Start + literals[i].Length);
            }

            spans.Add((first.Start, end,
                end == first.Start + first.Length ? Placeholder(first) : Opened(first)));
        }
        return spans;
    }

    private static void Collect(IReadOnlyList<MySqlToken> tokens, List<MySqlToken> into)
    {
        foreach (var token in tokens)
        {
            if (token.Kind is MySqlTokenKind.Text or MySqlTokenKind.Number)
            {
                into.Add(token);
            }
        }
    }

    /// <summary>A literal whose span grew into its neighbours under the other reading: its prefix and an
    /// open shell, with nothing claimed about where it ends.</summary>
    private static string Opened(MySqlToken token)
    {
        if (token.Kind != MySqlTokenKind.Text)
        {
            return "?";
        }
        var open = token.Text.IndexOfAny(['\'', '"']);
        return open < 0 ? "?" : token.Text[..(open + 1)] + "?";
    }

    private static string Placeholder(MySqlToken token)
    {
        if (token.Kind != MySqlTokenKind.Text)
        {
            return "?";
        }

        var text = token.Text;
        var open = text.IndexOfAny(['\'', '"']);
        if (open < 0)
        {
            return "?";
        }

        var prefix = text[..(open + 1)];
        return IsClosed(text, open) ? prefix + "?" + text[open] : prefix + "?";
    }

    /// <summary>True when the literal's last character is its own closing quote — and not an escaped one,
    /// which an unterminated <c>'abc\'</c> ends with.</summary>
    private static bool IsClosed(string text, int open)
    {
        if (text.Length < open + 2 || text[^1] != text[open])
        {
            return false;
        }

        // Count the backslashes immediately before the final quote: an odd number escapes it.
        var backslashes = 0;
        for (var i = text.Length - 2; i > open && text[i] == '\\'; i--)
        {
            backslashes++;
        }
        return backslashes % 2 == 0;
    }
}
