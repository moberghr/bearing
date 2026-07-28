using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Squirrel.App.Theming;

namespace Squirrel.App.Converters;

/// <summary>bool → a translucent orange highlight brush for type-ahead matches (false = transparent).</summary>
public sealed class MatchHighlightConverter : IValueConverter
{
    public static readonly MatchHighlightConverter Instance = new();

    // {Accent.Orange} at 0x55 alpha, resolved from the theme token (falls back to the token's literal value).
    private static IBrush? _match;
    private static IBrush Match => _match ??= ThemeBrush.AtAlpha("Accent.Orange", 0x55, Color.FromRgb(0xFF, 0x9E, 0x3B));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Match : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
