using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bearing.App.Controls;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Guards the headless harness itself (#62): that a window realizes, that a layout pass runs and measures
/// real text, and that the app's design tokens resolve. Every other UI test is written on top of those three
/// facts, so when they break it should be one obvious failure rather than a dozen confusing ones.
/// </summary>
[Collection(UiTestCollection.Name)]
public class HarnessTests
{
    private readonly UiTestSession _ui;

    public HarnessTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task Window_realizes_and_lays_out() => _ui.Run(() =>
    {
        var text = new TextBlock { Text = "measured" };
        var window = new Window { Width = 400, Height = 300, Content = text };
        window.Show();
        window.UpdateLayout();

        Assert.True(text.IsVisible);
        // Real shaping, not the headless stub: a non-empty string has to occupy width.
        Assert.True(text.Bounds.Width > 0, $"expected measured text width > 0, got {text.Bounds.Width}");
        Assert.True(text.Bounds.Height > 0);
    });

    [Fact]
    public Task Design_tokens_resolve_inside_the_test_application() => _ui.Run(() =>
    {
        // Tokens.Res falls back to transparent rather than throwing, so a missing dictionary would silently
        // turn every code-built visual invisible instead of failing. Assert a real colour came back.
        var faint = Assert.IsAssignableFrom<ISolidColorBrush>(Tokens.Res("Text.Faint"));
        Assert.NotEqual(Colors.Transparent, faint.Color);
        Assert.NotEqual(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(Tokens.Res("Text.Primary")).Color);
    });

    [Fact]
    public Task Each_test_gets_its_own_application() => _ui.Run(() =>
    {
        // PerTest isolation, asserted from the state that made it necessary: the accent this test mutates
        // must not be the accent the next one starts from. Both halves run here because the sibling test
        // below only proves the default if this one really did reset it.
        var before = ConnectionBrushColor();
        Bearing.App.App.SetConnectionAccent("#B24C63");
        Assert.NotEqual(before, ConnectionBrushColor());
    });

    [Fact]
    public Task Connection_accent_starts_neutral() => _ui.Run(() =>
        Assert.Equal(Color.Parse("#4E5865"), ConnectionBrushColor()));

    private static Color ConnectionBrushColor()
        => ((ISolidColorBrush)Application.Current!.FindResource("ConnectionBrush")!).Color;
}
