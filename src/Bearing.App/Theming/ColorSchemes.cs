using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Bearing.Core.Workspace;

namespace Bearing.App.Theming;

/// <summary>
/// The light/dark switch. Two halves, and both have to move together:
/// <list type="bullet">
/// <item>Avalonia's own <see cref="Application.RequestedThemeVariant"/>, which flips every Fluent control
/// (text boxes, combo boxes, the DataGrid's own chrome) — anything Bearing did not draw itself.</item>
/// <item>Bearing's tokens, which are <b>not</b> theme dictionaries. Each token in <c>Tokens.axaml</c> is one
/// <see cref="SolidColorBrush"/> instance whose <see cref="SolidColorBrush.Color"/> is rewritten in place from
/// <c>Tokens.Light.axaml</c> (or back from <c>Tokens.axaml</c>). Theme dictionaries would hand out a second
/// brush instance per variant, and the ~150 brushes captured in C# through <c>Tokens.Res</c> would keep
/// painting the old one; mutating the one instance is what lets them follow.</item>
/// </list>
/// What still needs a nudge after a switch is anything that copied a colour out of a token —
/// <c>Tokens.Tint</c>, <see cref="ThemeBrush.AtAlpha"/> — which is why <see cref="Repainted"/> exists and
/// <see cref="Generation"/> keys the alpha caches.
/// </summary>
public static class ColorSchemes
{
    private static readonly Uri DarkSource = new("avares://Bearing.App/Themes/Tokens.axaml");
    private static readonly Uri LightSource = new("avares://Bearing.App/Themes/Tokens.Light.axaml");

    /// <summary>The rail's brand mark: duo-tone on dark, ink (the brand's on-light form) on light.</summary>
    internal const string RailMarkKey = "BearingMark.Rail";

    private static IReadOnlyDictionary<string, Color>? _dark;
    private static IReadOnlyDictionary<string, Color>? _light;

    /// <summary>The dark palette as shipped, read from a fresh load of <c>Tokens.axaml</c> — never from the
    /// live brushes, which may be painted light at the time.</summary>
    internal static IReadOnlyDictionary<string, Color> Dark => _dark ??= Read(DarkSource);

    /// <summary>The light palette (<c>Tokens.Light.axaml</c>).</summary>
    internal static IReadOnlyDictionary<string, Color> Light => _light ??= Read(LightSource);

    /// <summary>Bumped on every repaint that changed the palette, so caches of a colour <i>copied</i> out of a
    /// token (<see cref="ThemeBrush.AtAlphaCached"/>) know they are stale.</summary>
    public static int Generation { get; private set; }

    /// <summary>Raised after the tokens were repainted to the other palette — by a settings change, a toggle,
    /// or the OS flipping while the scheme is <see cref="ColorScheme.System"/>. For surfaces that copied a
    /// colour and have to rebuild (the results grid) or carry their own theme (TextMate).</summary>
    public static event Action<Application>? Repainted;

    /// <summary>
    /// One-time wiring for an application: the rail mark alias, and repainting when the actual variant moves
    /// under a <see cref="ColorScheme.System"/> setting. Called from <c>App.Initialize</c>, so headless tests
    /// get it too (§4.5).
    /// </summary>
    public static void Install(Application app)
    {
        app.Resources[RailMarkKey] = app.FindResource("BearingMark.Duo");
        app.ActualThemeVariantChanged += (_, _) => Repaint(app);
    }

    /// <summary>The Avalonia variant a scheme asks for; <see cref="ThemeVariant.Default"/> follows the OS.</summary>
    public static ThemeVariant VariantFor(ColorScheme scheme) => scheme switch
    {
        ColorScheme.Light => ThemeVariant.Light,
        ColorScheme.System => ThemeVariant.Default,
        _ => ThemeVariant.Dark,
    };

    /// <summary>Whether the application is currently showing the light palette.</summary>
    public static bool IsLight(Application? app) => app?.ActualThemeVariant == ThemeVariant.Light;

    /// <summary>The explicit scheme a toggle lands on: the opposite of what is on screen now.</summary>
    public static ColorScheme Toggled(Application? app) => IsLight(app) ? ColorScheme.Dark : ColorScheme.Light;

    /// <summary>Switch the application to <paramref name="scheme"/>. Cheap and idempotent — re-applying the
    /// scheme already on screen changes nothing and raises nothing.</summary>
    public static void Apply(Application app, ColorScheme scheme)
    {
        // Setting the variant raises ActualThemeVariantChanged when it moves, which repaints; the explicit call
        // covers the cases where it does not move but the tokens are still on the other palette (first apply).
        app.RequestedThemeVariant = VariantFor(scheme);
        Repaint(app);
    }

    /// <summary>
    /// Paint the token brushes for the application's actual variant. Returns false (and raises nothing) when
    /// they already show it.
    /// </summary>
    internal static bool Repaint(Application app)
    {
        var light = IsLight(app);
        var palette = light ? Light : Dark;
        if (app.Resources.TryGetResource("Bg.Editor", null, out var probe)
            && probe is ISolidColorBrush current
            && palette.TryGetValue("Bg.Editor", out var wanted)
            && current.Color == wanted)
        {
            return false;
        }

        foreach (var (key, colour) in palette)
        {
            if (app.Resources.TryGetResource(key, null, out var value) && value is SolidColorBrush brush)
            {
                brush.Color = colour;
            }
        }
        app.Resources[RailMarkKey] = app.FindResource(light ? "BearingMark.Ink" : "BearingMark.Duo");

        Generation++;
        Repainted?.Invoke(app);
        return true;
    }

    /// <summary>Every colour token in a token file — a brush's colour, or a bare <see cref="Color"/>.</summary>
    internal static IReadOnlyDictionary<string, Color> Read(Uri source)
    {
        var dictionary = (ResourceDictionary)AvaloniaXamlLoader.Load(source);
        var colours = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var key in dictionary.Keys)
        {
            if (key is not string name || !dictionary.TryGetResource(name, null, out var value))
            {
                continue;
            }
            switch (value)
            {
                case ISolidColorBrush brush:
                    colours[name] = brush.Color;
                    break;
                case Color colour:
                    colours[name] = colour;
                    break;
            }
        }
        return colours;
    }
}
