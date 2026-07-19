using System;
using System.Globalization;

namespace Squirrel.App.Formatting;

/// <summary>
/// Renders result-grid cell values to strings. Dates use a fixed day-first pattern
/// (<c>dd.MM.yyyy HH:mm:ss</c>) rather than the OS culture's own pattern — .NET's culture data for
/// day-first locales adds spaces/trailing dots that aren't wanted here. Non-date values keep their
/// default (current-culture) rendering.
/// </summary>
public static class CellFormat
{
    public const string DateTimePattern = "dd.MM.yyyy HH:mm:ss";
    public const string DatePattern = "dd.MM.yyyy";
    public const string TimePattern = "HH:mm:ss";

    /// <summary>Shown for a NULL cell, and the token a user types to set a cell to NULL. Distinct from
    /// an empty string (which renders blank and, for text columns, saves as empty).</summary>
    public const string NullToken = "(null)";

    public static string Display(object? value) => value switch
    {
        null => NullToken,
        DateTime dt => dt.ToString(DateTimePattern, CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString(DateTimePattern, CultureInfo.InvariantCulture),
        DateOnly d => d.ToString(DatePattern, CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString(TimePattern, CultureInfo.InvariantCulture),
        byte[] bytes => FormatBytea(bytes),               // bytea → \x hex (truncated)
        Array arr => FormatArray(arr),                     // text[]/int[]/… → {a, b, c}
        _ => value.ToString() ?? "",
    };

    /// <summary>A Postgres array value as <c>[a, b, c]</c>, elements formatted via <see cref="Display"/>
    /// (so nested arrays, dates and nulls render consistently).</summary>
    private static string FormatArray(Array arr)
    {
        var parts = new string[arr.Length];
        for (var i = 0; i < arr.Length; i++) parts[i] = Display(arr.GetValue(i));
        return "[" + string.Join(", ", parts) + "]";
    }

    /// <summary>A byte[] (bytea) as a <c>\x</c> hex string, capped at 16 bytes with a length note.</summary>
    private static string FormatBytea(byte[] bytes)
    {
        const int cap = 16;
        var shown = Math.Min(bytes.Length, cap);
        var hex = Convert.ToHexString(bytes, 0, shown).ToLowerInvariant();
        return bytes.Length > cap ? $"\\x{hex}… ({bytes.Length} bytes)" : $"\\x{hex}";
    }

    /// <summary>Whether an edited cell string means "set NULL" (the <see cref="NullToken"/>, trimmed).</summary>
    public static bool IsNullToken(string? s) => string.Equals(s?.Trim(), NullToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse an edited cell string back to <paramref name="target"/> — the display pattern
    /// first, then a lenient current-culture parse. Returns false if neither matches.</summary>
    public static bool TryParseDate(string s, Type target, out object? value)
    {
        value = null;
        if (target == typeof(DateTime))
        {
            if (DateTime.TryParseExact(s, DateTimePattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
                || DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out v)) { value = v; return true; }
        }
        else if (target == typeof(DateTimeOffset))
        {
            if (DateTimeOffset.TryParseExact(s, DateTimePattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
                || DateTimeOffset.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out v)) { value = v; return true; }
        }
        else if (target == typeof(DateOnly))
        {
            if (DateOnly.TryParseExact(s, DatePattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
                || DateOnly.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out v)) { value = v; return true; }
        }
        else if (target == typeof(TimeOnly))
        {
            if (TimeOnly.TryParseExact(s, TimePattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
                || TimeOnly.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out v)) { value = v; return true; }
        }
        return false;
    }
}
