using System.Globalization;
using Avalonia.Media;
using Bearing.App.Converters;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// <see cref="HexBrushConverter"/>'s two modes. Without a parameter it produces an opaque environment
/// badge; with an opacity parameter it produces a *wash* — the chip fill and hairline that replaced the
/// server pill's environment dot, which read as a connection-state light (issue #45).
/// </summary>
public class EnvironmentWashTests
{
    // Brushes.Transparent (the empty wash) is an ImmutableSolidColorBrush, so assert on the interface.
    private static ISolidColorBrush Convert(string? hex, object? parameter = null)
        => Assert.IsAssignableFrom<ISolidColorBrush>(
            HexBrushConverter.Instance.Convert(hex, typeof(IBrush), parameter, CultureInfo.InvariantCulture));

    [Fact]
    public void Badge_mode_keeps_the_environment_hex_opaque()
    {
        var brush = Convert("#E5484D");
        Assert.Equal(Color.FromArgb(0xFF, 0xE5, 0x48, 0x4D), brush.Color);
    }

    [Fact]
    public void Wash_mode_keeps_the_hue_and_applies_the_requested_opacity()
    {
        var brush = Convert("#E5484D", "0.13");
        Assert.Equal(0xE5, brush.Color.R);
        Assert.Equal(0x48, brush.Color.G);
        Assert.Equal(0x4D, brush.Color.B);
        Assert.Equal(33, brush.Color.A); // 0.13 × 255, rounded
    }

    [Fact]
    public void Wash_mode_accepts_a_double_parameter_too()
        => Assert.Equal(128, Convert("#E5484D", 0.5).Color.A);

    /// <summary>A wash has no neutral form: a grey tint on the tab would read as its own environment.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-color")]
    public void Wash_mode_renders_nothing_for_an_untagged_connection(string? hex)
        => Assert.Equal(0, Convert(hex, "0.13").Color.A);

    /// <summary>The badge, by contrast, still shows a translucent neutral so surfaces that always want a
    /// mark (the schema-tree server node, the history row) keep their shape when untagged.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-color")]
    public void Badge_mode_falls_back_to_a_translucent_neutral(string? hex)
    {
        var brush = Convert(hex);
        Assert.Equal(0x55, brush.Color.A);
        Assert.NotEqual(0u, (uint)(brush.Color.R + brush.Color.G + brush.Color.B));
    }

    /// <summary>Out-of-range opacities are ignored rather than clamped — a bad parameter must not silently
    /// turn a wash into an opaque fill behind the connection name.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("-0.2")]
    [InlineData("wash")]
    public void An_unusable_opacity_parameter_falls_back_to_badge_mode(string parameter)
        => Assert.Equal(0xFF, Convert("#E5484D", parameter).Color.A);
}
