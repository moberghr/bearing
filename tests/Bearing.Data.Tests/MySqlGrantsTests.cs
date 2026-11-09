using Bearing.Data.MySql;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The <c>SHOW GRANTS</c> lines that decide <c>SeesAllSessions</c> on MySQL and MariaDB (§9.13a). Shapes are the
/// two engines' own output; the live half is <c>MySqlLiveTests.PROCESS_held_through_an_active_role…</c>.
/// </summary>
public class MySqlGrantsTests
{
    [Theory]
    [InlineData("GRANT PROCESS ON *.* TO `u`@`%`")]
    [InlineData("GRANT SELECT, PROCESS ON *.* TO `u`@`%`")]
    [InlineData("GRANT ALL PRIVILEGES ON *.* TO `root`@`%` WITH GRANT OPTION")]
    [InlineData("GRANT PROCESS ON *.* TO `probe_r`")]                         // MariaDB: the active role's line
    public void A_global_PROCESS_or_ALL_grant_reveals_every_session(string grant)
        => Assert.True(MySqlServerActivity.GrantsProcess(grant));

    [Theory]
    [InlineData("GRANT SUPER ON *.* TO `u`@`%`")]                             // measured: not enough on either engine
    [InlineData("GRANT USAGE ON *.* TO `u`@`%`")]
    [InlineData("GRANT ALL PRIVILEGES ON `shop`.* TO `u`@`%`")]               // not global
    [InlineData("GRANT `process`@`%` TO `u`@`%`")]                             // a role named process, not the privilege
    [InlineData("GRANT SHOW_ROUTINE,PROCESS_ADMIN ON *.* TO `u`@`%`")]         // whole entries, not substrings
    public void Anything_else_does_not(string grant)
        => Assert.False(MySqlServerActivity.GrantsProcess(grant));
}
