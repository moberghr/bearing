using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Bearing.App.Theming;

namespace Bearing.App.Converters;

/// <summary>
/// bool → the type-ahead match decoration, drawn the way the results grid draws a selected cell: a translucent
/// {Accent.Brand} fill with a solid {Accent.Brand} edge (<c>ConverterParameter=Border</c>). False gives
/// transparent for both, so the row keeps its size whether or not it matches.
/// </summary>
public sealed class MatchHighlightConverter : IValueConverter
{
    public static readonly MatchHighlightConverter Instance = new();

    // Resolved from the theme token, falling back to the token's literal value.
    private static readonly Color Fallback = Color.FromRgb(0x35, 0xD0, 0xBE);

    private static IBrush? _fill;
    private static IBrush? _edge;

    /// <summary>The same 0x2A wash the grid's selection ring fills with, so a match reads as "picked out",
    /// not as a second selection colour.</summary>
    private static IBrush Fill => _fill ??= ThemeBrush.AtAlpha("Accent.Brand", 0x2A, Fallback);

    private static IBrush Edge => _edge ??= ThemeBrush.AtAlpha("Accent.Brand", 0xFF, Fallback);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true
            ? Brushes.Transparent
            : string.Equals(parameter as string, "Border", StringComparison.OrdinalIgnoreCase) ? Edge : Fill;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
