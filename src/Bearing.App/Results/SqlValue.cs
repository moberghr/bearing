using System;
using System.Globalization;

namespace Bearing.App.Results;

/// <summary>
/// How an engine spells the literal forms the two do not share. Everything else — single-quoting, ISO
/// dates, invariant numbers — is identical, which is why this is a two-member enum rather than a second
/// renderer. Resolved per connection through <c>ProviderTraits</c>.
/// </summary>
public enum SqlLiteralStyle
{
    /// <summary>PostgreSQL: the <c>true</c>/<c>false</c> keywords, and bytea as a quoted <c>'\x…'</c>.</summary>
    Postgres,

    /// <summary>T-SQL: no boolean literal at all (<c>bit</c> takes <c>1</c>/<c>0</c>), and binary as the
    /// bare <c>0x…</c> constant — quoting that would make it a string.</summary>
    TSql,
}

/// <summary>
/// Renders a typed value as a SQL literal. Used wherever SQL is produced as *text* rather than
/// parameterized: the inline-edit preview, the foreign-key lookup, and Copy as ▸ SQL. Values come from the
/// database (or from a coerced cell), never straight from untrusted input — the write path itself stays
/// parameterized (§5.4).
/// <para>
/// Dates are the reason this is one function and not three: <c>value.ToString()</c> renders them in the
/// current culture, so a literal built on a Croatian machine came out <c>'11.08.2026 14:03:22'</c> and its
/// meaning then depended on the server's <c>DateStyle</c>. Every date form here is written ISO
/// (unquoted-parse-safe in Postgres regardless of DateStyle), matching what the grid displays.
/// </para>
/// <para>
/// The style-less overload renders PostgreSQL, which is what every caller wanted when there was one
/// engine. A caller holding a connection passes that connection's <see cref="SqlLiteralStyle"/>: booleans
/// and binary are spelled differently, and on the foreign-key-lookup path this literal is SQL that runs.
/// </para>
/// </summary>
internal static class SqlValue
{
    /// <summary>A value as a PostgreSQL literal; <c>null</c> becomes the keyword <c>null</c>.</summary>
    public static string Literal(object? value) => Literal(SqlLiteralStyle.Postgres, value);

    /// <summary>A value as a SQL literal in <paramref name="style"/>; <c>null</c> becomes the keyword
    /// <c>null</c>.</summary>
    public static string Literal(SqlLiteralStyle style, object? value) => value switch
    {
        null => "null",
        // T-SQL has no boolean literal: a `bit` compares against 1/0, and `flag = true` is a syntax error.
        bool b => style == SqlLiteralStyle.TSql ? (b ? "1" : "0") : b ? "true" : "false",
        byte or sbyte or short or ushort or int or uint or long or ulong
            => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        DateTime dt => Quote(dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => Quote(dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFzzz", CultureInfo.InvariantCulture)),
        DateOnly d => Quote(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        TimeOnly t => Quote(t.ToString("HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture)),
        TimeSpan ts => Quote(ts.ToString("c", CultureInfo.InvariantCulture)),
        // Ahead of the Array arm deliberately — a byte[] *is* an Array, and falling through would render a
        // bytea / varbinary value as the array literal `'{1,2,3}'`. T-SQL's binary constant is bare:
        // quoting `0x01` would turn it into the four-character string "0x01".
        byte[] bytes => style == SqlLiteralStyle.TSql
            ? "0x" + Convert.ToHexString(bytes)
            : @"'\x" + Convert.ToHexString(bytes).ToLowerInvariant() + "'",
        Guid g => Quote(g.ToString()),
        // Arrays and everything else are quoted and left to the server to cast from its text form. Note
        // this is the *full* value: unlike the grid's display, a literal must never be truncated. The array
        // text form is Postgres's own; SQL Server has no array type, so nothing it returns reaches here.
        Array arr => Quote(PostgresArray(arr)),
        _ => Quote(value.ToString()!),
    };

    /// <summary>Single-quote a string literal, doubling embedded quotes.</summary>
    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>An array as Postgres's own text form (<c>{a,b,c}</c>), so the quoted literal casts back to an
    /// array type. Elements are rendered without the outer SQL quoting; embedded quotes/backslashes are
    /// escaped per the array-literal rules.</summary>
    private static string PostgresArray(Array arr)
    {
        var parts = new string[arr.Length];
        var i = 0;
        foreach (var item in arr)
            parts[i++] = item switch
            {
                null => "NULL",
                Array nested => PostgresArray(nested),
                bool b => b ? "t" : "f",
                _ => "\"" + Element(item).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
            };
        return "{" + string.Join(",", parts) + "}";
    }

    /// <summary>An array element's inner text: the literal form minus its SQL quoting.</summary>
    private static string Element(object item)
    {
        var literal = Literal(item);
        return literal.Length >= 2 && literal[0] == '\''
            ? literal[1..^1].Replace("''", "'")
            : literal;
    }
}
