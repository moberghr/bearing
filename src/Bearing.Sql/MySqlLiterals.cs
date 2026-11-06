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
    /// </summary>
    public static string RedactLiterals(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return sql ?? "";
        }

        var sb = new StringBuilder(sql.Length);
        var copied = 0;

        foreach (var token in MySqlScanner.Tokenize(sql))
        {
            if (token.Kind is not (MySqlTokenKind.Text or MySqlTokenKind.Number))
            {
                continue;
            }

            sb.Append(sql, copied, token.Start - copied);
            sb.Append(Placeholder(token));
            copied = token.Start + token.Length;
        }

        sb.Append(sql, copied, sql.Length - copied);
        return sb.ToString();
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
