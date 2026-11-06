using Bearing.Data.MySql;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>The handshake version strings the provider decides <c>RETURNING</c> from — the real ones the two
/// test containers report, and the replication-compatible prefix older MariaDB servers still send.</summary>
public class MySqlServerFlavorTests
{
    [Theory]
    [InlineData("11.4.13-MariaDB-ubu2404", true, 11, 4, true)]
    [InlineData("5.5.5-10.11.6-MariaDB-log", true, 10, 11, true)]
    [InlineData("10.4.32-MariaDB", true, 10, 4, false)]
    [InlineData("10.5.0-MariaDB", true, 10, 5, true)]
    [InlineData("8.4.13", false, 8, 4, false)]
    [InlineData("", false, 0, 0, false)]
    public void Parses_engine_and_version(string text, bool maria, int major, int minor, bool returning)
    {
        var flavor = MySqlServerFlavor.Parse(text);
        Assert.Equal(maria, flavor.IsMariaDb);
        Assert.Equal(major, flavor.Major);
        Assert.Equal(minor, flavor.Minor);
        Assert.Equal(returning, flavor.SupportsInsertReturning);
    }
}
