using System;
using System.Globalization;

namespace Bearing.App.Formatting;

/// <summary>
/// Renders result-grid cell values to strings. Dates use a fixed ISO-8601 shape
/// (<c>yyyy-MM-dd HH:mm:ss</c>) rather than the OS culture's own pattern: the grid, the clipboard and the
/// cell inspector all render through here, so the same text a user copies is the text they see, and it is
/// unambiguous in both directions (a day-first display made <c>03.04.2026</c> re-parse differently
/// depending on the machine's culture). A space separates date and time instead of <c>T</c> — far easier
/// to scan in a grid, and still RFC 3339 ("T" may be replaced by a space by mutual agreement).
/// <para>
/// <b>Numbers are invariant for the same reason</b>, and it is not merely cosmetic: this text is what the
/// clipboard and every export format carry (<c>TableFormats.Text</c>), and it is also what the inline
/// editor hands back to <c>ResultEditModel.Coerce</c>, which parses invariantly. Rendered in a
/// comma-decimal culture, <c>9.5m</c> displayed as "9,5" re-parsed invariantly is <b>95</b> — a silent
/// tenfold write. The numeric arms below mirror <c>SqlValue.Literal</c> exactly, so Copy as ▸ CSV and
/// Copy as ▸ SQL can never disagree about a number.
/// </para>
/// </summary>
public static class CellFormat
{
    // ".FFFFFF" prints fractional seconds only when there are any — and, because the '.' is immediately
    // followed by F specifiers, .NET omits the separator too for a whole second. So a timestamp reads
    // "2026-08-11 14:03:22" unless it genuinely carries sub-second precision. Postgres stores microseconds,
    // and truncating them in the display would mean copying a value that isn't the one in the row.
    public const string DateTimePattern = "yyyy-MM-dd HH:mm:ss.FFFFFF";
    public const string DatePattern = "yyyy-MM-dd";
    public const string TimePattern = "HH:mm:ss.FFFFFF";

    /// <summary>A <c>timestamptz</c> keeps its offset ("2026-08-11 14:03:22+02:00"). Dropping it — which the
    /// old shared date/time pattern did — silently lost the zone on every copy.</summary>
    public const string DateTimeOffsetPattern = "yyyy-MM-dd HH:mm:ss.FFFFFFzzz";

    /// <summary>Shown for a NULL cell, and the token a user types to set a cell to NULL. Distinct from
    /// an empty string (which renders blank and, for text columns, saves as empty).</summary>
    public const string NullToken = "(null)";

    public static string Display(object? value) => value switch
    {
        null => NullToken,
        DateTime dt => dt.ToString(DateTimePattern, CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString(DateTimeOffsetPattern, CultureInfo.InvariantCulture),
        DateOnly d => d.ToString(DatePattern, CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString(TimePattern, CultureInfo.InvariantCulture),
        byte[] bytes => FormatBytea(bytes),               // bytea → \x hex (truncated)
        Array arr => FormatArray(arr),                     // text[]/int[]/… → {a, b, c}
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float fl => fl.ToString("R", CultureInfo.InvariantCulture),
        byte or sbyte or short or ushort or int or uint or long or ulong
            => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        // Anything else that formats per-culture (BigInteger for an oversized numeric, …). Everything the
        // grid actually shows is matched above; this is the arm that stops a new type reintroducing the bug.
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>A Postgres array value as <c>[a, b, c]</c>, elements formatted via <see cref="Display"/>
    /// (so nested arrays, dates and nulls render consistently).</summary>
    private static string FormatArray(Array arr)
    {
        // foreach flattens any rank in row-major order; the old arr.Length + single-index GetValue(i)
        // threw on a multi-dimensional Postgres array (int[][], text[][], …) which needs rank-N indices.
        var parts = new string[arr.Length];
        var i = 0;
        foreach (var item in arr) parts[i++] = Display(item);
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

    // Accepted exact forms per target type, display pattern first. Listed rather than relying on one
    // pattern because ParseExact's optional-fraction handling shouldn't be the only thing standing between
    // a typed date and the lenient fallback: a user may type "2026-08-11 14:03" (no seconds), paste an
    // RFC 3339 "T" form back in, or omit the offset on a timestamptz column.
    private static readonly string[] DateTimeFormats =
    [
        DateTimePattern, "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm", DatePattern,
    ];

    private static readonly string[] DateTimeOffsetFormats =
    [
        DateTimeOffsetPattern, "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd HH:mm:sszzz", DateTimePattern, "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", DatePattern,
    ];

    private static readonly string[] DateFormats = [DatePattern];

    private static readonly string[] TimeFormats = [TimePattern, "HH:mm:ss", "HH:mm"];

    /// <summary>Parse an edited cell string back to <paramref name="target"/> — the ISO display forms
    /// first, then a lenient current-culture parse. Returns false if neither matches.</summary>
    public static bool TryParseDate(string s, Type target, out object? value)
    {
        value = null;
        if (target == typeof(DateTime))
        {
            if (DateTime.TryParseExact(s, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
                || DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out v)) { value = v; return true; }
        }
        else if (target == typeof(DateTimeOffset))
        {
            if (DateTimeOffset.TryParseExact(s, DateTimeOffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
                || DateTimeOffset.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out v)) { value = v; return true; }
        }
        else if (target == typeof(DateOnly))
        {
            if (DateOnly.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
                || DateOnly.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out v)) { value = v; return true; }
        }
        else if (target == typeof(TimeOnly))
        {
            if (TimeOnly.TryParseExact(s, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
                || TimeOnly.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out v)) { value = v; return true; }
        }
        return false;
    }

    /// <summary>Whether <paramref name="target"/> is one of the numeric types <see cref="Display"/> renders
    /// invariantly — and therefore one <see cref="TryParseNumber"/> owns rather than <c>Convert.ChangeType</c>.
    /// </summary>
    public static bool IsNumeric(Type target)
        => target == typeof(decimal) || target == typeof(double) || target == typeof(float)
        || target == typeof(byte) || target == typeof(sbyte)
        || target == typeof(short) || target == typeof(ushort)
        || target == typeof(int) || target == typeof(uint)
        || target == typeof(long) || target == typeof(ulong);

    /// <summary>
    /// Parse an edited cell string back to a numeric <paramref name="target"/>: invariant, and with group
    /// separators <b>disallowed</b>. Both halves matter, and the second is the subtle one —
    /// <c>Convert.ChangeType("9,5", typeof(decimal), InvariantCulture)</c> does <i>not</i> throw. It reads
    /// the comma as a thousands separator and returns <b>95</b>, so a user typing their own locale's decimal
    /// form silently wrote a tenfold value. Refusing it here sends the raw string to the server instead,
    /// which rejects it with an error the user can see.
    /// </summary>
    public static bool TryParseNumber(string s, Type target, out object? value)
    {
        // NumberStyles.Float = sign + decimal point + exponent; NumberStyles.Integer = sign only. Neither
        // includes AllowThousands, which is the whole point.
        const NumberStyles Real = NumberStyles.Float;
        const NumberStyles Int = NumberStyles.Integer;
        var inv = CultureInfo.InvariantCulture;

        value = null;
        if (target == typeof(decimal)) { if (decimal.TryParse(s, Real, inv, out var v)) { value = v; return true; } }
        else if (target == typeof(double)) { if (double.TryParse(s, Real, inv, out var v)) { value = v; return true; } }
        else if (target == typeof(float)) { if (float.TryParse(s, Real, inv, out var v)) { value = v; return true; } }
        else if (target == typeof(byte)) { if (byte.TryParse(s, Int, inv, out var v)) { value = v; return true; } }
        else if (target == typeof(sbyte)) { if (sbyte.TryParse(s, Int, inv, out var v)) { value = v; return true; } }
        else if (target == typeof(short)) { if (short.TryParse(s, Int, inv, out var v)) { value = v; return true; } }
        else if (target == typeof(ushort)) { if (ushort.TryParse(s, Int, inv, out var v)) { value = v; return true; } }
        else if (target == typeof(int)) { if (int.TryParse(s, Int, inv, out var v)) { value = v; return true; } }
        else if (target == typeof(uint)) { if (uint.TryParse(s, Int, inv, out var v)) { value = v; return true; } }
        else if (target == typeof(long)) { if (long.TryParse(s, Int, inv, out var v)) { value = v; return true; } }
        else if (target == typeof(ulong)) { if (ulong.TryParse(s, Int, inv, out var v)) { value = v; return true; } }
        return false;
    }
}
