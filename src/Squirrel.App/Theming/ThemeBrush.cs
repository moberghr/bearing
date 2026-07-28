using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Squirrel.App.Theming;

/// <summary>
/// Resolves a theme token brush (from <c>Themes/Tokens.axaml</c>) for code that draws outside XAML —
/// margins, converters, custom render passes. Prefer <c>{DynamicResource}</c> in XAML; use this only
/// where a brush must be built in C#. Keeps color literals out of code so a theme swap stays one-file.
/// </summary>
public static class ThemeBrush
{
    /// <summary>
    /// The token brush at <paramref name="alpha"/>, so a call site can reuse an opaque token at a
    /// custom translucency. Falls back to <paramref name="fallback"/> if the token can't be resolved.
    /// </summary>
    public static IBrush AtAlpha(string tokenKey, byte alpha, Color fallback)
    {
        var c = (Application.Current?.FindResource(tokenKey) as ISolidColorBrush)?.Color ?? fallback;
        return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }
}
