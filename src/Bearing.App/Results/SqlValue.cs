using System;
using System.Globalization;

namespace Bearing.App.Results;

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
/// </summary>
internal static class SqlValue
{
    /// <summary>A value as a SQL literal; <c>null</c> becomes the keyword <c>null</c>.</summary>
    public static string Literal(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
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
        byte[] bytes => @"'\x" + Convert.ToHexString(bytes).ToLowerInvariant() + "'",
        Guid g => Quote(g.ToString()),
        // Arrays and everything else are quoted and left to Postgres to cast from its text form. Note this
        // is the *full* value: unlike the grid's display, a literal must never be truncated.
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
