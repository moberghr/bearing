using System;
using System.Threading.Tasks;
using Bearing.Updates;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The real update service against a build that was never installed — which is every dev run, every test
/// run, and every copy from build/release.sh's archive. The composition root constructs this during startup,
/// so anything that throws here is a crash on launch for everyone working from source.
/// </summary>
public class VelopackUpdateServiceTests
{
    [Fact]
    public void Constructing_it_on_an_uninstalled_build_does_not_throw()
    {
        var service = new VelopackUpdateService();

        Assert.False(service.IsSupported);
    }

    [Fact]
    public async Task Applying_an_update_that_did_not_come_from_this_service_is_rejected()
    {
        // Guards the opaque handle IUpdateService passes around: a foreign handle must be refused loudly
        // rather than quietly installing whatever "latest" happens to be.
        var service = new VelopackUpdateService();
        var foreign = new Core.Updates.UpdateCheck("9.9.9", Handle: "not-an-update");

        Assert.Throws<ArgumentException>(() => service.ApplyOnExit(foreign));
        Assert.Throws<ArgumentException>(() => service.ApplyAndRestart(foreign));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DownloadAsync(foreign));
    }

    [Fact]
    public void The_feed_token_is_read_from_the_environment_and_nowhere_else()
    {
        // §1.1: no token is compiled in and none is written to disk. Absent variable means "no token",
        // which is what makes the private-repo feed fail quietly rather than ship a credential.
        var original = Environment.GetEnvironmentVariable("BEARING_UPDATE_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("BEARING_UPDATE_TOKEN", "  ");
            Assert.Null(UpdateFeed.AccessToken);

            Environment.SetEnvironmentVariable("BEARING_UPDATE_TOKEN", " gho_example ");
            Assert.Equal("gho_example", UpdateFeed.AccessToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BEARING_UPDATE_TOKEN", original);
        }
    }
}
