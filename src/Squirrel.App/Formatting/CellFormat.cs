using System;
using System.Globalization;
using Avalonia.Data.Converters;

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
        _ => value.ToString() ?? "",
    };

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

/// <summary>Formats a grid cell value for display (dates in the fixed day-first pattern).</summary>
public sealed class CellDisplayConverter : IValueConverter
{
    public static readonly CellDisplayConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => CellFormat.Display(value);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
