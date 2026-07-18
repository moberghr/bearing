using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Squirrel.App.Converters;

/// <summary>bool → a translucent orange highlight brush for type-ahead matches (false = transparent).</summary>
public sealed class MatchHighlightConverter : IValueConverter
{
    public static readonly MatchHighlightConverter Instance = new();
    private static readonly IBrush Match = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0x9E, 0x3B));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Match : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
