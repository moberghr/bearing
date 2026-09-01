using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Bearing.App.Theming;

namespace Bearing.App.Converters;

/// <summary>
/// Turns a hex color string ("#E53935") into a brush for environment badges. Null/blank/invalid
/// falls back to a translucent neutral so an untagged connection still renders a subtle chip.
/// <para>
/// A <c>ConverterParameter</c> of <c>0..1</c> requests a <em>wash</em> at that opacity instead — a
/// low-saturation fill or hairline (the server pill's environment chip) rather than a badge. A wash has
/// no neutral form: an untagged connection gets nothing rather than a grey tint that would read as its
/// own environment.
/// </para>
/// </summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();

    // Translucent {Text.Faint} — same slate hue as ConnectionColors.Neutral / ConnectionBrush's
    // default (the badge stays translucent by design; only the hue is unified). Resolved from the token.
    private static (Avalonia.Application? Owner, Avalonia.Media.IImmutableBrush Brush)? _neutral;
    private static IBrush Neutral =>
        ThemeBrush.AtAlphaCached(ref _neutral, "Text.Faint", 0x55, Color.FromRgb(0x4E, 0x58, 0x65));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var opacity = ParseOpacity(parameter);
        if (value is string hex && !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var color))
        {
            return opacity is { } a
                ? new SolidColorBrush(Color.FromArgb((byte)Math.Round(a * 255), color.R, color.G, color.B))
                : new SolidColorBrush(color);
        }
        return opacity is null ? Neutral : Brushes.Transparent;
    }

    /// <summary>Reads the wash opacity from a converter parameter; null when none was supplied.</summary>
    private static double? ParseOpacity(object? parameter)
        => parameter switch
        {
            double d when d is > 0 and <= 1 => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                          && d is > 0 and <= 1 => d,
            _ => null,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
