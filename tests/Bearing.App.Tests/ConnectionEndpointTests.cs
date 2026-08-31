using System;
using Bearing.App.Connections;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The endpoint spelling shared by the tree row, the toolbar tooltip, the credential prompt and the
/// connect-failure message (#79). Tested here because the failure it guards against is drift between those
/// four, which no single call site can catch.
/// </summary>
public class ConnectionEndpointTests
{
    private static ConnectionInfo Conn(string host = "localhost", int port = 5432,
                                       string database = "app", string user = "karlo")
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "c",
            ProviderId = "postgres",
            Host = host,
            Port = port,
            Database = database,
            User = user,
        };

    [Fact]
    public void HostPort_always_shows_the_port_even_when_it_is_the_default()
        => Assert.Equal("localhost:5432", ConnectionEndpoint.HostPort(Conn()));

    [Fact]
    public void HostPort_distinguishes_two_instances_on_one_host()
    {
        Assert.NotEqual(
            ConnectionEndpoint.HostPort(Conn(port: 5432)),
            ConnectionEndpoint.HostPort(Conn(port: 5434)));
    }

    [Fact]
    public void Address_appends_the_database()
        => Assert.Equal("db.example:5433/reporting",
            ConnectionEndpoint.Address(Conn("db.example", 5433, "reporting")));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Address_drops_the_slash_when_there_is_no_database(string database)
        => Assert.Equal("localhost:5432", ConnectionEndpoint.Address(Conn(database: database)));

    [Fact]
    public void Full_is_what_a_bug_report_would_paste()
        => Assert.Equal("karlo@localhost:5434/app", ConnectionEndpoint.Full(Conn(port: 5434)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Full_drops_the_at_sign_when_there_is_no_user(string user)
        => Assert.Equal("localhost:5432/app", ConnectionEndpoint.Full(Conn(user: user)));

    /// <summary>The failure message builds its endpoint from the same parts; this pins the two together so a
    /// change to one spelling is a visible change to the other.</summary>
    [Fact]
    public void Address_matches_the_spelling_the_connect_failure_message_uses()
    {
        var info = Conn("db.example", 5433, "reporting");
        Assert.Equal($"{info.Host}:{info.Port}/{info.Database}", ConnectionEndpoint.Address(info));
    }
}
