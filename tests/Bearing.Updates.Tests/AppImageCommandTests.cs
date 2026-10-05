using Bearing.Updates;
using Xunit;

namespace Bearing.Updates.Tests;

/// <summary>
/// Which launches of the AppImage are the command and which are the window. The forwarding itself is a
/// process start; the decision is the half that can send someone's <c>bearing query …</c> to a window that
/// ignores it, which is the bug this exists for.
/// </summary>
public class AppImageCommandTests
{
    private const string Image = "/home/u/Applications/BearingSql.AppImage";
    private static readonly string[] AppArguments = ["--demo"];

    private static bool Forward(string? appImage, params string[] args)
        => AppImageCommand.ShouldForward(args, appImage, AppArguments);

    [Fact]
    public void A_command_inside_an_appimage_goes_to_the_cli()
    {
        Assert.True(Forward(Image, "query", "prod", "select 1"));
        Assert.True(Forward(Image, "--version"));
        Assert.True(Forward(Image, "--help"));
    }

    /// <summary>The launcher and the update restart start the image with nothing, and that is the window.</summary>
    [Fact]
    public void No_arguments_is_the_window()
    {
        Assert.False(Forward(Image));
    }

    [Fact]
    public void The_windows_own_switch_stays_the_window()
    {
        Assert.False(Forward(Image, "--demo"));
        Assert.False(Forward(Image, "--DEMO"));
    }

    /// <summary>A window ignoring the rest would be the bug again; the command says what it does not take.</summary>
    [Fact]
    public void The_windows_switch_mixed_with_anything_else_goes_to_the_cli()
    {
        Assert.True(Forward(Image, "--demo", "connections"));
    }

    /// <summary>Outside an image the command is beside the app under its own name, and is called by it.</summary>
    [Fact]
    public void Nothing_is_forwarded_outside_an_appimage()
    {
        Assert.False(Forward(null, "query", "prod"));
        Assert.False(Forward("", "query", "prod"));
    }
}
