using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Bearing.App.Theming;

namespace Bearing.App.Converters;

/// <summary>
/// Turns a hex color string ("#E53935") into a brush for environment badges. Null/blank/invalid
/// falls back to a translucent neutral so an untagged connection still renders a subtle chip.
/// </summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();

    // Translucent {Text.Faint} — same slate hue as ConnectionColors.Neutral / ConnectionBrush's
    // default (the badge stays translucent by design; only the hue is unified). Resolved from the token.
    private static IBrush? _neutral;
    private static IBrush Neutral => _neutral ??= ThemeBrush.AtAlpha("Text.Faint", 0x55, Color.FromRgb(0x4E, 0x58, 0x65));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var color))
            return new SolidColorBrush(color);
        return Neutral;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
