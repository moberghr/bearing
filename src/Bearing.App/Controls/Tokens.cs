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

    /// <summary>
    /// A numeric token by key — the type scale and the metrics derived from it (#52), for visuals built in
    /// C#. Named Metric rather than Size because <c>using static Tokens</c> would otherwise shadow Avalonia's
    /// own <c>Size</c> struct at every call site that pulls this in. XAML uses
    /// <c>{DynamicResource Font.Body}</c> and follows a change on its own; a code-built <c>TextBlock</c> reads
    /// the value once, which is why the settings window rebuilds the results view after a change rather than
    /// expecting it to update itself.
    /// </summary>
    public static double Metric(string key) => Theming.FontScale.Get(key, 12);

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

    /// <summary>The app's monospace stack (<c>MonoFontFamily</c> in <c>App.axaml</c>) for code-built text
    /// visuals — SQL previews, the cell inspector's JSON.</summary>
    public static FontFamily MonoFont
        => Application.Current?.FindResource("MonoFontFamily") as FontFamily ?? FontFamily.Default;
}
