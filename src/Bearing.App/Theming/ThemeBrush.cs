using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Bearing.App.Theming;

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
    /// <para>
    /// Immutable, so the result is safe to cache in a static and to hand to a visual built on any thread.
    /// A mutable <see cref="SolidColorBrush"/> is an <see cref="AvaloniaObject"/> and takes the dispatcher
    /// of whichever thread constructed it; a static cache filled on one thread then threw
    /// <c>VerifyAccess</c> out of the compositor when a visual on another used it.
    /// </para>
    /// </summary>
    public static IImmutableBrush AtAlpha(string tokenKey, byte alpha, Color fallback)
    {
        var c = (Application.Current?.FindResource(tokenKey) as ISolidColorBrush)?.Color ?? fallback;
        return new ImmutableSolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    /// <summary>
    /// <see cref="AtAlpha"/>, cached per <see cref="Application"/> in <paramref name="cache"/>.
    /// <para>
    /// Keyed on the application rather than cached outright, because the answer depends on one: with no
    /// application the token cannot resolve and the fallback is the right answer, and a plain static cache
    /// let whichever call came first decide for everyone. In a test run that meant the value depended on
    /// test order — a shell test filling it from the real token, or a unit test filling it from the
    /// fallback, and the other one then reading the wrong one.
    /// </para>
    /// </summary>
    public static IImmutableBrush AtAlphaCached(
        ref (Application? Owner, IImmutableBrush Brush)? cache, string tokenKey, byte alpha, Color fallback)
    {
        var app = Application.Current;
        if (cache is { } hit && ReferenceEquals(hit.Owner, app)) return hit.Brush;
        var brush = AtAlpha(tokenKey, alpha, fallback);
        cache = (app, brush);
        return brush;
    }
}
