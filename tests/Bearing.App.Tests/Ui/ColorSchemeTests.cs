using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Bearing.App.Controls;
using Bearing.App.Converters;
using Bearing.App.Theming;
using Bearing.Core.Workspace;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The light/dark switch. The tokens are repainted in place rather than swapped for theme dictionaries, so a
/// brush captured in C# through <c>Tokens.Res</c> before the switch must show the new colour after it — that is
/// the property everything else rests on. A UI collection test because the tokens live in the real
/// application's resources.
/// </summary>
[Collection(UiTestCollection.Name)]
public class ColorSchemeTests
{
    private readonly UiTestSession _ui;

    public ColorSchemeTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task Every_colour_token_has_a_light_twin() => _ui.Run(() =>
    {
        // A token missing from the light file would stay dark on a light screen — dark text on dark fill is
        // exactly the failure nobody sees until it ships.
        var dark = ColorSchemes.Dark.Keys.OrderBy(k => k).ToList();
        var light = ColorSchemes.Light.Keys.OrderBy(k => k).ToList();

        Assert.Contains("Bg.Editor", dark);
        Assert.Equal(dark, light);
    });

    [Fact]
    public Task A_brush_captured_before_the_switch_shows_the_light_colour_after_it() => _ui.Run(() =>
    {
        var app = Application.Current!;
        var captured = (ISolidColorBrush)Tokens.Res("Bg.Editor");
        Assert.Equal(ColorSchemes.Dark["Bg.Editor"], captured.Color);

        ColorSchemes.Apply(app, ColorScheme.Light);

        Assert.Equal(ThemeVariant.Light, app.ActualThemeVariant);
        Assert.Same(captured, Tokens.Res("Bg.Editor"));
        Assert.Equal(ColorSchemes.Light["Bg.Editor"], captured.Color);
        Assert.Equal(ColorSchemes.Light["Text.Primary"], ((ISolidColorBrush)Tokens.Res("Text.Primary")).Color);
    });

    [Fact]
    public Task Switching_back_restores_the_shipped_dark_palette() => _ui.Run(() =>
    {
        var app = Application.Current!;
        ColorSchemes.Apply(app, ColorScheme.Light);
        ColorSchemes.Apply(app, ColorScheme.Dark);

        Assert.Equal(ThemeVariant.Dark, app.ActualThemeVariant);
        foreach (var (key, colour) in ColorSchemes.Dark)
        {
            Assert.Equal(colour, ((ISolidColorBrush)Tokens.Res(key)).Color);
        }
    });

    [Fact]
    public Task Reapplying_the_scheme_on_screen_raises_nothing() => _ui.Run(() =>
    {
        // Every settings change re-applies the scheme; one that did not touch it must not rebuild the results.
        var app = Application.Current!;
        ColorSchemes.Apply(app, ColorScheme.Dark);
        var raised = 0;
        void Count(Application _) => raised++;
        ColorSchemes.Repainted += Count;
        try
        {
            var generation = ColorSchemes.Generation;
            ColorSchemes.Apply(app, ColorScheme.Dark);
            Assert.Equal(0, raised);
            Assert.Equal(generation, ColorSchemes.Generation);

            ColorSchemes.Apply(app, ColorScheme.Light);
            Assert.Equal(1, raised);
            Assert.Equal(generation + 1, ColorSchemes.Generation);
        }
        finally
        {
            ColorSchemes.Repainted -= Count;
        }
    });

    [Fact]
    public Task Alpha_copies_of_a_token_are_retired_by_a_switch() => _ui.Run(() =>
    {
        // AtAlphaCached holds a copy of the colour, not the token brush, so it has to notice the switch.
        var app = Application.Current!;
        ColorSchemes.Apply(app, ColorScheme.Dark);
        (Application? Owner, int Generation, IImmutableBrush Brush)? cache = null;

        var before = (ISolidColorBrush)ThemeBrush.AtAlphaCached(ref cache, "Text.Faint", 0x55, Colors.Black);
        ColorSchemes.Apply(app, ColorScheme.Light);
        var after = (ISolidColorBrush)ThemeBrush.AtAlphaCached(ref cache, "Text.Faint", 0x55, Colors.Black);

        var light = ColorSchemes.Light["Text.Faint"];
        Assert.NotEqual(before.Color, after.Color);
        Assert.Equal(Color.FromArgb(0x55, light.R, light.G, light.B), after.Color);
    });

    [Fact]
    public Task The_rail_mark_takes_the_brands_on_light_form() => _ui.Run(() =>
    {
        var app = Application.Current!;
        ColorSchemes.Apply(app, ColorScheme.Light);
        Assert.Same(app.FindResource("BearingMark.Ink"), app.FindResource("BearingMark.Rail"));

        ColorSchemes.Apply(app, ColorScheme.Dark);
        Assert.Same(app.FindResource("BearingMark.Duo"), app.FindResource("BearingMark.Rail"));
    });

    [Fact]
    public Task The_toggle_lands_on_the_opposite_of_what_is_showing() => _ui.Run(() =>
    {
        var app = Application.Current!;
        ColorSchemes.Apply(app, ColorScheme.Dark);
        Assert.Equal(ColorScheme.Light, ColorSchemes.Toggled(app));

        ColorSchemes.Apply(app, ColorScheme.Light);
        Assert.Equal(ColorScheme.Dark, ColorSchemes.Toggled(app));
    });

    [Fact]
    public Task A_token_bound_glyph_follows_the_switch() => _ui.Run(() =>
    {
        // Tree icons and history rows pick a colour by token key; the converter must hand back the live token
        // so the bound stroke changes without the binding re-evaluating.
        var app = Application.Current!;
        ColorSchemes.Apply(app, ColorScheme.Dark);
        var brush = (ISolidColorBrush)new TokenBrushConverter()
            .Convert("Tree.Folder", typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture);

        ColorSchemes.Apply(app, ColorScheme.Light);

        Assert.Equal(ColorSchemes.Light["Tree.Folder"], brush.Color);
    });
}
