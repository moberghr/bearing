using Avalonia;
using Avalonia.Media;
using Avalonia.Controls;

namespace Bearing.App.Controls;

/// <summary>
/// Resolves the design token brushes declared in <c>Themes/Tokens.axaml</c> (see also the dark variant in
/// <c>App.axaml</c>) for code-built visuals. Six controls each carried their own private copy of this lookup
/// — <c>ResultView.Res</c>, <c>MainWindow.ThemeBrush</c>, <c>SettingsWindow.Brush</c> and friends — so a
/// missing key behaved differently depending on which class asked. This is the one lookup.
/// <para>
/// Intended to be pulled in with <c>using static Bearing.App.Controls.Tokens;</c> so call sites read
/// <c>Res("Text.Dim")</c>. Prefer <c>{DynamicResource}</c> in XAML; this is for visuals built in C#.
/// </para>
/// <para>
/// The one deliberate exception is <see cref="Theming.ThemeBrush.AtAlpha"/>, which takes an explicit fallback
/// <see cref="Color"/> — converters and custom margins can run without a resolvable token and need a real
/// colour rather than transparency.
/// </para>
/// </summary>
public static class Tokens
{
    /// <summary>A token brush by key, or transparent when the key is missing (never throws — a missing
    /// token must not take the window down at build time).</summary>
    public static IBrush Res(string key)
        => (Application.Current?.FindResource(key) as IBrush) ?? Brushes.Transparent;

    /// <summary>A token color re-emitted at a given alpha (for faint row / selection / badge tints).</summary>
    public static IBrush Tint(string key, byte alpha)
    {
        var c = (Res(key) as ISolidColorBrush)?.Color ?? Colors.Transparent;
        return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    /// <summary>Foreign-key jump icons, the results back-arrow, and JSON keys — the app's "link" color.</summary>
    public static IBrush LinkBrush => Res("Syntax.Func");

    /// <summary>The dimmed marker a NULL cell renders in.</summary>
    public static IBrush NullBrush => Res("Text.Faint");

    /// <summary>The 1px rule used for every region / cell / header separator.</summary>
    public static IBrush SeparatorBrush => Res("Border");
}
