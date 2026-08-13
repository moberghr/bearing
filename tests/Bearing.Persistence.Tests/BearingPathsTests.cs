using Bearing.Persistence;
using Xunit;

using static Bearing.Persistence.BearingPaths;

namespace Bearing.Persistence.Tests;

/// <summary>
/// Covers the per-platform root selection. These exercise <see cref="BearingPaths.ResolveRoot"/> rather
/// than the live properties so every platform's mapping is verified from any host — the Windows and macOS
/// branches are otherwise unreachable on the Linux dev/CI box.
/// </summary>
public class BearingPathsTests
{
    private const string Home = "/home/tester";
    private const string Roaming = @"C:\Users\tester\AppData\Roaming";
    private const string Local = @"C:\Users\tester\AppData\Local";

    private static string Resolve(PathKind kind, PathPlatform platform, string? xdg = null)
        => ResolveRoot(kind, xdg, platform, Home, Roaming, Local);

    [Fact]
    public void Linux_uses_xdg_default_locations()
    {
        Assert.Equal(Path.Combine(Home, ".config"), Resolve(PathKind.Config, PathPlatform.Linux));
        Assert.Equal(Path.Combine(Home, ".local", "share"), Resolve(PathKind.Data, PathPlatform.Linux));
    }

    [Fact]
    public void Windows_splits_roaming_config_from_local_data()
    {
        // The query log and secrets must not follow the user to other machines.
        Assert.Equal(Roaming, Resolve(PathKind.Config, PathPlatform.Windows));
        Assert.Equal(Local, Resolve(PathKind.Data, PathPlatform.Windows));
    }

    [Fact]
    public void MacOS_puts_both_under_application_support()
    {
        var expected = Path.Combine(Home, "Library", "Application Support");
        Assert.Equal(expected, Resolve(PathKind.Config, PathPlatform.MacOS));
        Assert.Equal(expected, Resolve(PathKind.Data, PathPlatform.MacOS));
    }

    // Named by string rather than the enum: xUnit needs public test signatures, and PathPlatform is internal.
    [Theory]
    [InlineData("Linux")]
    [InlineData("Windows")]
    [InlineData("MacOS")]
    public void Xdg_override_wins_on_every_platform(string platformName)
    {
        var platform = Enum.Parse<PathPlatform>(platformName);
        Assert.Equal("/redirected", Resolve(PathKind.Config, platform, "/redirected"));
        Assert.Equal("/redirected", Resolve(PathKind.Data, platform, "/redirected"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_xdg_override_falls_back_to_the_platform_default(string? xdg)
    {
        // An empty/whitespace env var must not resolve state to the process working directory.
        Assert.Equal(Path.Combine(Home, ".config"), Resolve(PathKind.Config, PathPlatform.Linux, xdg));
        Assert.Equal(Local, Resolve(PathKind.Data, PathPlatform.Windows, xdg));
    }

    [Fact]
    public void Config_and_data_dirs_are_namespaced_by_the_app_dir_name()
    {
        // The live properties create the directories; assert they land under the profile-aware name.
        Assert.Equal(BearingPaths.AppDirName, Path.GetFileName(BearingPaths.ConfigDir));
        Assert.Equal(BearingPaths.AppDirName, Path.GetFileName(BearingPaths.DataDir));
        Assert.True(Directory.Exists(BearingPaths.ConfigDir));
        Assert.True(Directory.Exists(BearingPaths.DataDir));
    }
}
