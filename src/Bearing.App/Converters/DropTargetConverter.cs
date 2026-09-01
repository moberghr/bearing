using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Bearing.App.Theming;

namespace Bearing.App.Converters;

/// <summary>
/// bool → the drag-and-drop "this is where it lands" fill for the row under the pointer (false =
/// transparent). Deliberately faint: a hint that follows the pointer, not a selection.
/// </summary>
public sealed class DropTargetConverter : IValueConverter
{
    public static readonly DropTargetConverter Instance = new();

    // {Accent.Brand}, resolved from the theme token (falls back to the token's literal value).
    private static readonly Color Fallback = Color.FromRgb(0x35, 0xD0, 0xBE);
    private static (Avalonia.Application? Owner, Avalonia.Media.IImmutableBrush Brush)? _fill;

    private static IBrush Fill => ThemeBrush.AtAlphaCached(ref _fill, "Accent.Brand", 0x26, Fallback);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Fill : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
