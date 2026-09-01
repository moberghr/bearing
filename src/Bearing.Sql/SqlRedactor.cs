using System;
using System.Collections.Generic;
using System.Text;
using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// Replaces the literal values in a SQL statement with placeholders, leaving its shape intact (#22).
/// <para>
/// The query log holds the SQL you ran, verbatim — which means it holds whatever was in the WHERE clause: an
/// email address, a national id, a customer name. Retention prunes it eventually; until then it is a
/// plaintext file of production identifiers sitting in the user's data directory. Redaction is what makes the
/// log a record of <i>what you did</i> rather than of the data you did it to.
/// </para>
/// <para>
/// Lexer-based, reusing <see cref="PgParsing"/> as <see cref="WriteGuard"/> does, because a regex cannot tell
/// a quote inside a dollar-quoted body from one that ends a string, and getting that wrong either leaves the
/// value in or eats the rest of the statement. Whitespace, comments and formatting survive untouched: the
/// point is a statement still recognisable as the one you ran.
/// </para>
/// <para>
/// It is <b>not</b> anonymisation. Identifiers are left alone — a table called <c>patient_ssn</c> still says
/// so, and a redacted statement can still be recognisable in a small database. What it removes is the values,
/// which is the part that is personal data rather than schema.
/// </para>
/// </summary>
public static class SqlRedactor
{
    /// <summary>Stands in for a removed string, blob or dollar-quoted body.</summary>
    public const string StringPlaceholder = "'?'";

    /// <summary>Stands in for a removed number. Not <c>0</c> — a placeholder has to be visibly a placeholder,
    /// or a redacted statement reads as one that really did ask for zero.</summary>
    public const string NumberPlaceholder = "?";

    /// <summary>
    /// String-ish literal tokens: every quoting form the grammar knows, unterminated variants included. An
    /// unterminated string is exactly the case a naive scan mishandles, and it still holds a value.
    /// </summary>
    private static readonly HashSet<int> StringLiterals =
    [
        PostgreSQLLexer.StringConstant, PostgreSQLLexer.UnterminatedStringConstant,
        PostgreSQLLexer.EscapeStringConstant, PostgreSQLLexer.UnterminatedEscapeStringConstant,
        PostgreSQLLexer.UnicodeEscapeStringConstant, PostgreSQLLexer.UnterminatedUnicodeEscapeStringConstant,
        PostgreSQLLexer.BinaryStringConstant, PostgreSQLLexer.UnterminatedBinaryStringConstant,
        PostgreSQLLexer.HexadecimalStringConstant, PostgreSQLLexer.UnterminatedHexadecimalStringConstant,
    ];

    private static readonly HashSet<int> NumberLiterals =
    [
        PostgreSQLLexer.Integral, PostgreSQLLexer.Numeric,
    ];

    /// <summary>
    /// The SQL with its literals replaced. Returns the input unchanged when there is nothing to redact, so a
    /// caller can store the result unconditionally.
    /// </summary>
    public static string Redact(string? sql)
    {
        if (string.IsNullOrEmpty(sql)) return sql ?? "";

        var tokens = PgParsing.LexAll(sql);
        var sb = new StringBuilder(sql.Length);
        var copied = 0;

        // A dollar-quoted body arrives as three tokens (open tag, text, close tag) and has to collapse to one
        // placeholder — replacing the text alone would leave $tag$'?'$tag$, which is not what it looks like.
        var inDollar = false;

        foreach (var token in tokens)
        {
            if (token.Type == TokenConstants.EOF) break;

            if (token.Type == PostgreSQLLexer.BeginDollarStringConstant)
            {
                Emit(token, StringPlaceholder);
                inDollar = true;
                continue;
            }
            if (inDollar)
            {
                // Swallow the body and the closing tag: the placeholder already stands for the whole thing.
                Skip(token);
                if (token.Type == PostgreSQLLexer.EndDollarStringConstant) inDollar = false;
                continue;
            }

            if (StringLiterals.Contains(token.Type)) Emit(token, StringPlaceholder);
            else if (NumberLiterals.Contains(token.Type)) Emit(token, NumberPlaceholder);
        }

        sb.Append(sql[copied..]);
        return sb.ToString();

        // Copy everything up to the token, then the placeholder instead of the token itself.
        void Emit(IToken token, string placeholder)
        {
            sb.Append(sql[copied..token.StartIndex]).Append(placeholder);
            copied = token.StopIndex + 1;
        }

        void Skip(IToken token)
        {
            sb.Append(sql[copied..token.StartIndex]);
            copied = token.StopIndex + 1;
        }
    }
}
