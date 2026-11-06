using System;
using System.Globalization;
using Bearing.Sql;

namespace Bearing.Results;

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
public static class SqlValue
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
        // A UTC DateTime is a timestamptz (Npgsql's mapping, §5.5) and is an instant, so its literal carries
        // the offset: without one the server reads the text in the *session's* zone, and since #163 that is
        // this machine's — Copy as SQL of 15:00 UTC would insert 13:00 UTC from Zagreb, and an FK lookup on a
        // timestamptz key would match nothing. Unspecified (timestamp without time zone) has no zone to state.
        // SqlClient never produces Kind = Utc, so a T-SQL literal is unaffected.
        DateTime { Kind: DateTimeKind.Utc } utc
            => QuoteFor(style, utc.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture) + "+00:00"),
        DateTime dt => QuoteFor(style, dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => QuoteFor(style, dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFzzz", CultureInfo.InvariantCulture)),
        DateOnly d => QuoteFor(style, d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        TimeOnly t => QuoteFor(style, t.ToString("HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture)),
        // MySQL's TIME is a signed duration up to 838 hours with no day part, so `c`'s `1.02:03:04` would be
        // misread; it takes total hours instead.
        TimeSpan ts when style == SqlLiteralStyle.MySql => QuoteFor(style, MySqlTime(ts)),
        TimeSpan ts => QuoteFor(style, ts.ToString("c", CultureInfo.InvariantCulture)),
        // Ahead of the Array arm deliberately — a byte[] *is* an Array, and falling through would render a
        // bytea / varbinary value as the array literal `'{1,2,3}'`. T-SQL's binary constant is bare:
        // quoting `0x01` would turn it into the four-character string "0x01".
        byte[] bytes => style is SqlLiteralStyle.TSql or SqlLiteralStyle.MySql
            ? "0x" + Convert.ToHexString(bytes)
            : @"'\x" + Convert.ToHexString(bytes).ToLowerInvariant() + "'",
        Guid g => QuoteFor(style, g.ToString()),
        // Arrays and everything else are quoted and left to the server to cast from its text form. Note
        // this is the *full* value: unlike the grid's display, a literal must never be truncated. The array
        // text form is Postgres's own; SQL Server has no array type, so nothing it returns reaches here.
        Array arr => QuoteFor(style, PostgresArray(arr)),
        _ => QuoteFor(style, value.ToString()!),
    };

    /// <summary>A quoted literal in <paramref name="style"/>: MySQL also escapes the backslash, which it reads as
    /// an escape character under the default <c>sql_mode</c>. (Under <c>NO_BACKSLASH_ESCAPES</c> the doubled
    /// backslash reads as two — a wrong value, never a broken-out literal, which is the side to err on.)</summary>
    private static string QuoteFor(SqlLiteralStyle style, string s)
        => Quote(style == SqlLiteralStyle.MySql ? s.Replace("\\", "\\\\") : s);

    /// <summary>A duration as MySQL's <c>[-]H:MM:SS[.ffffff]</c>, hours unbounded by a day.</summary>
    private static string MySqlTime(TimeSpan ts)
    {
        var sign = ts < TimeSpan.Zero ? "-" : "";
        var abs = ts.Duration();
        var text = $"{sign}{(long)abs.TotalHours}:{abs.Minutes:00}:{abs.Seconds:00}";
        var micros = abs.Ticks % TimeSpan.TicksPerSecond / 10;
        return micros == 0 ? text : $"{text}.{micros:000000}";
    }

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
