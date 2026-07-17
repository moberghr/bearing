using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Squirrel.App.Converters;

/// <summary>
/// Turns a hex color string ("#E53935") into a brush for environment badges. Null/blank/invalid
/// falls back to a neutral gray so an untagged connection still renders a subtle chip.
/// </summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();

    private static readonly IBrush Neutral = new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var color))
            return new SolidColorBrush(color);
        return Neutral;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
