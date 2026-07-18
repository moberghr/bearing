using Avalonia.Media;
using Squirrel.App.Theming;
using Xunit;

namespace Squirrel.App.Tests;

public class ConnectionColorsTests
{
    [Theory]
    [InlineData("#E46876", 0xE4, 0x68, 0x76)] // production
    [InlineData("#E6C384", 0xE6, 0xC3, 0x84)] // staging
    [InlineData("#7AA89F", 0x7A, 0xA8, 0x9F)] // local
    public void Resolve_parses_valid_hex(string hex, byte r, byte g, byte b)
    {
        var c = ConnectionColors.Resolve(hex);
        Assert.Equal(Color.FromRgb(r, g, b), c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-color")]
    public void Resolve_falls_back_to_neutral(string? hex)
        => Assert.Equal(ConnectionColors.Neutral, ConnectionColors.Resolve(hex));
}
