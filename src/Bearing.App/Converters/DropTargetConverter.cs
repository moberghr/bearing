using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Bearing.App.Theming;

namespace Bearing.App.Converters;

/// <summary>
/// bool → the drag-and-drop "this is where it lands" paint (false = transparent). Two strengths, chosen with
/// <c>ConverterParameter=Border</c>: a translucent fill for the row under the pointer, and a solid line for
/// the tree's own edge when the drop would land in the scripts root — which is a real outcome, not the
/// absence of one, so it needs its own mark rather than just "no row highlighted".
/// </summary>
public sealed class DropTargetConverter : IValueConverter
{
    public static readonly DropTargetConverter Instance = new();

    // {Accent.Brand}, resolved from the theme token (falls back to the token's literal value).
    private static readonly Color Fallback = Color.FromRgb(0x35, 0xD0, 0xBE);
    private static IBrush? _fill;
    private static IBrush? _edge;

    private static IBrush Fill => _fill ??= ThemeBrush.AtAlpha("Accent.Brand", 0x38, Fallback);
    private static IBrush Edge => _edge ??= ThemeBrush.AtAlpha("Accent.Brand", 0xCC, Fallback);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true
            ? Brushes.Transparent
            : parameter is string p && p.Equals("Border", StringComparison.OrdinalIgnoreCase) ? Edge : Fill;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
