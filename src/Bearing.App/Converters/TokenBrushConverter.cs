using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Converters;

/// <summary>
/// Turns a design-token key ("Error.Red", "Tree.Folder") into that token's brush, for view models that pick a
/// colour by meaning. The brush handed back is the live token instance, so a bound glyph follows a light/dark
/// switch without the binding re-evaluating (see <c>Theming.ColorSchemes</c>) — which a hex string through
/// <see cref="HexBrushConverter"/> could not do. Unknown or blank keys give transparent, as <c>Res</c> does.
/// </summary>
public sealed class TokenBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string key && !string.IsNullOrWhiteSpace(key) ? Res(key) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
