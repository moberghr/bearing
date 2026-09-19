using System.Text;

namespace Bearing.Sql;

/// <summary>
/// The two lexical questions about T-SQL literals that are asked outside the completion engine: is the
/// caret inside one, and what does this statement look like with its literal values taken out. The T-SQL
/// counterparts of <see cref="SqlStringLiterals"/> and <see cref="SqlRedactor"/>.
/// <para>
/// Both were PostgreSQL-lexer-bound on every dialect until this existed, and both were <em>measurably</em>
/// wrong on T-SQL rather than merely approximate:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Completion died on an apostrophe inside a delimited name.</b> The PG lexer has no
///     <c>[bracketed]</c> identifier, so the <c>'</c> in <c>[O'Donnell]</c> opened a string constant that
///     never closed — and since the in-a-literal check is the <em>first</em> thing the engine does, every
///     caret after that point returned nothing at all. Names like that are ordinary in real schemas.
///   </item>
///   <item>
///     <b>The query log under-redacted binary literals.</b> With <c>QueryLogRedactLiterals</c> on,
///     <c>where Blob = 0xDEADBEEF</c> was stored as <c>where Blob = ?xDEADBEEF</c>: the PG lexer read the
///     <c>0</c> as a number and <c>xDEADBEEF</c> as an identifier, so the bytes survived in a log the
///     setting promised to strip (§1.3).
///   </item>
/// </list>
/// <para>
/// Both are answered from <see cref="TSqlScanner"/> rather than the vendored T-SQL grammar. Deliberately:
/// these are lexical questions, the scanner already classifies <c>[O'Donnell]</c> as a name and
/// <c>0xDEADBEEF</c> as one numeric token, and neither caller can afford a parse — one runs per keystroke,
/// the other on the write path of every logged statement.
/// </para>
/// </summary>
public static class TSqlLiterals
{
    /// <summary>
    /// Whether <paramref name="offset"/> sits inside a character literal.
    /// <para>
    /// The opening delimiter itself does not count, matching <see cref="SqlStringLiterals.Contains"/>: at
    /// the position where the literal begins the caret is not inside anything yet, so completion still
    /// works there. Everything after it counts, up to and including one past the closing quote's content.
    /// </para>
    /// </summary>
    public static bool Contains(string? sql, int offset)
    {
        if (string.IsNullOrEmpty(sql)) return false;
        if (offset <= 0 || offset > sql.Length) return false;

        foreach (var token in TSqlScanner.Tokenize(sql))
        {
            if (token.Kind != TSqlTokenKind.Text) continue;
            if (offset > token.Start && offset <= token.Start + token.Length) return true;
        }
        return false;
    }

    /// <summary>
    /// <paramref name="sql"/> with every literal <em>value</em> replaced by <c>?</c>, so the shape of a
    /// statement can be kept without keeping the data in it (#22, §1.3).
    /// <para>
    /// A character literal keeps its delimiters and its <c>N</c> prefix — <c>N'Ada'</c> becomes
    /// <c>N'?'</c> — because the quoting is shape, not data, and a redacted statement should still read as
    /// SQL. A numeric or binary literal is replaced whole: <c>42</c> and <c>0xDEADBEEF</c> both become
    /// <c>?</c>, the latter being the case the Postgres redactor let through.
    /// </para>
    /// <para>
    /// Identifiers are never touched, delimited or not. A table called <c>[Salary]</c> is structure, and a
    /// log with its table names removed would not be worth keeping.
    /// </para>
    /// </summary>
    public static string RedactLiterals(string? sql)
    {
        if (string.IsNullOrEmpty(sql)) return sql ?? "";

        var sb = new StringBuilder(sql.Length);
        var copied = 0;

        foreach (var token in TSqlScanner.Tokenize(sql))
        {
            if (token.Kind is not (TSqlTokenKind.Text or TSqlTokenKind.Number)) continue;

            sb.Append(sql, copied, token.Start - copied);
            sb.Append(Placeholder(sql, token));
            copied = token.Start + token.Length;
        }

        sb.Append(sql, copied, sql.Length - copied);
        return sb.ToString();
    }

    /// <summary>What one literal is replaced by: a quoted literal keeps its shell, anything else goes
    /// entirely. An unterminated quote — which a buffer being typed into has most of the time — keeps the
    /// opening delimiter and nothing else, rather than inventing a closing one.</summary>
    private static string Placeholder(string sql, TSqlToken token)
    {
        if (token.Kind != TSqlTokenKind.Text) return "?";

        var text = sql.Substring(token.Start, token.Length);
        var open = text.IndexOf('\'');
        if (open < 0) return "?";                                  // not shaped like a quoted literal

        var prefix = text[..(open + 1)];                           // "'" or "N'"
        var closed = text.Length > open + 1 && text[^1] == '\'';
        return closed ? prefix + "?" + "'" : prefix + "?";
    }
}
