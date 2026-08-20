using Bearing.App;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The version string shown in the status bar and the About dialog. It is also what the release feed compares
/// against, so the shape matters: Velopack only accepts a 3-part semver2, and the SDK appends a
/// <c>+&lt;git-sha&gt;</c> that must not leak into the displayed name.
/// </summary>
public class AppVersionTests
{
    [Fact]
    public void The_displayed_version_carries_no_build_metadata()
    {
        Assert.DoesNotContain("+", AppVersion.Display);
        Assert.NotEqual("unknown", AppVersion.Display);
    }

    [Fact]
    public void The_chrome_label_is_the_displayed_version_with_a_v()
    {
        Assert.Equal($"v{AppVersion.Display}", AppVersion.Label);
    }

    [Fact]
    public void Metadata_is_stripped_at_the_first_plus_and_prerelease_tags_survive()
    {
        Assert.Equal("0.2.1", AppVersion.Strip("0.2.1+93e7a69171107c77a9beb991f8a0014ebfee9ba6"));
        Assert.Equal("0.2.1", AppVersion.Strip("0.2.1"));
        // A pre-release tag is part of the version, not metadata — it must not be trimmed with it.
        Assert.Equal("0.3.0-beta.1", AppVersion.Strip("0.3.0-beta.1+abc123"));
    }
}
