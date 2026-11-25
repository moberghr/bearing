using System;
using Bearing.App.Connections;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>The wording of the connection dialog's Test status row (§2.5): pure, so no window.</summary>
public class ConnectionTestStatusTests
{
    private static ConnectionInfo Info(string host = "db.example", int port = 1433, string user = "app")
        => new()
        {
            Id = Guid.NewGuid(), Name = "n", ProviderId = "sqlserver",
            Host = host, Port = port, Database = "orders", User = user,
        };

    [Fact]
    public void Testing_names_the_endpoint_and_counts_whole_seconds()
    {
        var status = ConnectionTestStatus.Testing(Info(), TimeSpan.FromSeconds(3.9));

        Assert.Equal("Testing db.example:1433…", status.Headline);
        Assert.Equal("app@db.example:1433/orders", status.Detail);
        Assert.Equal("3 s", status.Aside);
        Assert.Equal(ConnectionState.Connecting, status.Beacon);
    }

    [Fact]
    public void A_named_instance_is_tested_without_a_port_it_does_not_use()
    {
        var status = ConnectionTestStatus.Testing(Info(host: @"SQLPROD\SALES"), TimeSpan.Zero);

        Assert.Equal(@"Testing SQLPROD\SALES…", status.Headline);
    }

    [Fact]
    public void Success_says_what_was_reached_and_how_long_it_took()
    {
        var status = ConnectionTestStatus.Succeeded(Info(), TimeSpan.FromMilliseconds(42.7));

        Assert.Equal("Connected", status.Headline);
        Assert.Equal("app@db.example:1433/orders", status.Detail);
        Assert.Equal("42 ms", status.Aside);
        Assert.Equal(ConnectionState.Connected, status.Beacon);
    }

    [Theory]
    [InlineData(0.2, "1 ms")]      // never "0 ms": something was measured
    [InlineData(999, "999 ms")]
    [InlineData(1000, "1.0 s")]
    [InlineData(12340, "12.3 s")]
    public void Durations_switch_to_seconds_at_one_second(double ms, string expected)
        => Assert.Equal(expected, ConnectionTestStatus.Succeeded(Info(), TimeSpan.FromMilliseconds(ms)).Aside);

    [Fact]
    public void A_duration_is_not_formatted_by_the_machines_culture()
    {
        // Expose: hr-HR writes a decimal comma, which an en-US box would hide.
        CultureScope.In("hr-HR", () =>
            Assert.Equal("1.5 s", ConnectionTestStatus.Succeeded(Info(), TimeSpan.FromMilliseconds(1500)).Aside));
    }

    [Fact]
    public void A_failure_carries_the_error_and_no_duration()
    {
        var status = ConnectionTestStatus.Failed("  Login failed for user 'app'.\n");

        Assert.Equal("Could not connect", status.Headline);
        Assert.Equal("Login failed for user 'app'.", status.Detail);
        Assert.Equal("", status.Aside);
        Assert.Equal(ConnectionState.Disconnected, status.Beacon);
    }

    [Fact]
    public void A_failure_with_no_error_does_not_invent_a_cause()
        => Assert.Equal("No error was reported.", ConnectionTestStatus.Failed(null).Detail);

    [Fact]
    public void Not_tested_and_stopped_show_no_session_state()
    {
        Assert.Null(ConnectionTestStatus.NotTested.Beacon);
        // Every state carries a second line, so the row keeps one height (only an error grows it).
        Assert.NotEqual("", ConnectionTestStatus.NotTested.Detail);
        Assert.NotEqual("", ConnectionTestStatus.Stopped.Detail);
        Assert.Null(ConnectionTestStatus.Stopped.Beacon);
        Assert.Equal(ConnectionTestPhase.Stopped, ConnectionTestStatus.Stopped.Phase);
    }
}
