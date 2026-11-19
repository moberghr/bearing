using System;
using Bearing.App.Workspace;
using Bearing.Core.Logging;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>The wording of a script row's trailer and its tooltip (<see cref="ScriptUseText"/>).</summary>
public class ScriptUseTextTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 15, 0, 0, TimeSpan.Zero);

    private static ScriptUse Use(DateTimeOffset at, string database = "Netgiro_Test")
        => new("/s/a.sql", at, "partner-sql", null, database);

    [Fact]
    public void A_recorded_run_shows_its_database_and_day()
    {
        Assert.Equal("Netgiro_Test · today", ScriptUseText.Trailer(Use(Now.AddHours(-3)), null, Now, Utc));
        Assert.Equal("Netgiro_Test · yesterday", ScriptUseText.Trailer(Use(Now.AddDays(-1)), null, Now, Utc));
        Assert.Equal("Netgiro_Test · 12 Nov", ScriptUseText.Trailer(Use(Now.AddDays(-7)), null, Now, Utc));
        Assert.Equal("Netgiro_Test · 12 Nov 2025", ScriptUseText.Trailer(Use(Now.AddYears(-1).AddDays(-7)), null, Now, Utc));
    }

    [Fact]
    public void A_run_with_no_database_shows_the_day_alone()
        => Assert.Equal("today", ScriptUseText.Trailer(Use(Now, database: ""), null, Now, Utc));

    [Fact]
    public void The_day_is_the_viewers_calendar_day_not_utcs()
    {
        // 23:30 UTC on the 18th is already the 19th two hours east.
        var east = TimeZoneInfo.CreateCustomTimeZone("east", TimeSpan.FromHours(2), "east", "east");
        var lateOn18th = new DateTimeOffset(2026, 11, 18, 23, 30, 0, TimeSpan.Zero);

        Assert.Equal("yesterday", ScriptUseText.Day(lateOn18th, Now, Utc));
        Assert.Equal("today", ScriptUseText.Day(lateOn18th, Now, east));
    }

    [Fact]
    public void A_recorded_run_outranks_the_edit_date()
        => Assert.Equal("Netgiro_Test · 12 Nov", ScriptUseText.Trailer(Use(Now.AddDays(-7)), Now, Now, Utc));

    [Fact]
    public void Without_a_recorded_run_the_edit_date_shows_and_the_tip_says_which_it_is()
    {
        var edited = Now.AddDays(-7);

        Assert.Equal("12 Nov", ScriptUseText.Trailer(null, edited, Now, Utc));
        Assert.Equal("No run recorded for this script. Edited 2026-11-12 15:00", ScriptUseText.Tip(null, edited, Utc));
    }

    [Fact]
    public void Nothing_known_shows_nothing()
    {
        Assert.Equal("", ScriptUseText.Trailer(null, null, Now, Utc));
        Assert.Equal("", ScriptUseText.Tip(null, null, Utc));
    }

    [Fact]
    public void The_tip_names_the_connection_database_and_time()
        => Assert.Equal("Last run on partner-sql / Netgiro_Test, 2026-11-12 15:00",
            ScriptUseText.Tip(Use(Now.AddDays(-7)), null, Utc));
}
