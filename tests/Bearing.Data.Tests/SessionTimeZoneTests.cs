using Bearing.Core.Data;
using Bearing.Data.Postgres;
using Bearing.Results;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The session time zone (#163). Left unset, a Postgres session keeps the server's own zone — UTC on RDS —
/// while pgJDBC sends the client's, so <c>timestamptz::timestamp</c> and every expression like it returned
/// numbers two hours apart in Bearing and in DBeaver against the same database. The live tests at the bottom
/// ask the <b>server</b> what zone it is computing in (§4.7): re-reading our own connection string would pass
/// with the parameter ignored.
/// </summary>
public class SessionTimeZoneTests
{
    private static ConnectionInfo Info(string? zone = null, Dictionary<string, string>? options = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "prod",
        ProviderId = PostgresProvider.ProviderId,
        Host = "db.example.com",
        Database = "app",
        User = "u",
        SessionTimeZone = zone,
        Options = options ?? new Dictionary<string, string>(),
    };

    private static string? Sent(ConnectionInfo info) => PostgresConnectionString.Build(info, "pw").Timezone;

    // ---- what the setting resolves to -----------------------------------------------------------

    [Fact]
    public void An_unset_zone_is_this_machines()
    {
        // The default, and the fix: what pgJDBC sends. Every project file on disk has no such field, so this is
        // the line that moved existing connections onto the client's zone.
        Assert.Equal(SessionTimeZonePolicy.Local, SessionTimeZonePolicy.Setting(Info()));
        Assert.Equal(SessionTimeZonePolicy.LocalIanaId(), Sent(Info()));
    }

    [Fact]
    public void The_server_setting_sends_nothing()
    {
        // Null leaves the keyword out, so the session keeps the server's zone — today's behaviour, on request.
        Assert.Null(Sent(Info(SessionTimeZonePolicy.Server)));
        Assert.Null(Sent(Info("SERVER")));
    }

    [Theory]
    [InlineData("UTC", "UTC")]
    [InlineData("Europe/Zagreb", "Europe/Zagreb")]
    // A spelling .NET cannot resolve is passed through: Postgres knows POSIX zones .NET does not, and refusing
    // them here would refuse zones the server accepts.
    [InlineData("UTC+2", "UTC+2")]
    [InlineData("  Europe/Zagreb ", "Europe/Zagreb")]
    public void An_explicit_zone_is_sent_as_written(string setting, string sent)
        => Assert.Equal(sent, Sent(Info(setting)));

    [Fact]
    public void A_windows_zone_id_is_sent_as_its_iana_name()
    {
        // The one spelling the server certainly cannot read — and the one a Windows machine's own zone list
        // offers. Postgres would refuse "Central Europe Standard Time" at startup and the connection would
        // not open at all.
        Assert.Equal("Europe/Budapest", Sent(Info("Central Europe Standard Time")));
    }

    [Fact]
    public void A_zone_is_named_by_iana_id_whichever_platform_reported_it()
    {
        // Pinned on named zones rather than the test machine's own, so it holds on every CI runner.
        Assert.Equal("Europe/Zagreb", SessionTimeZonePolicy.IanaIdOf(TimeZoneInfo.FindSystemTimeZoneById("Europe/Zagreb")));
        Assert.Equal("Europe/Budapest",
            SessionTimeZonePolicy.IanaIdOf(TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time")));
    }

    [Theory]
    // One Windows zone, several IANA zones that agree today and not historically — so the region decides.
    [InlineData("Central European Standard Time", "HR", "Europe/Zagreb")]
    [InlineData("Central European Standard Time", "PL", "Europe/Warsaw")]
    // A region the Windows zone does not cover, and none at all, fall back to its default.
    [InlineData("Central European Standard Time", "JP", "Europe/Warsaw")]
    [InlineData("Central European Standard Time", null, "Europe/Warsaw")]
    public void A_windows_zone_is_named_for_the_machines_region(string windowsId, string? region, string iana)
        => Assert.Equal(iana, SessionTimeZonePolicy.FromWindowsId(windowsId, region));

    [Fact]
    public void A_zone_with_no_name_is_not_invented()
    {
        // .NET calls a zone it read from a TZ file it could not name "Local". Sending that would fail the
        // connect; a fixed offset would be wrong half the year. Nothing is sent, and Describe says why.
        var unnamed = TimeZoneInfo.CreateCustomTimeZone("Local", TimeSpan.FromHours(2), "Local", "Local");
        Assert.Null(SessionTimeZonePolicy.IanaIdOf(unnamed));
    }

    // ---- the options bag it used to live in -----------------------------------------------------

    [Fact]
    public void A_legacy_bag_zone_is_honoured_while_the_field_is_unset()
    {
        // Before the field, a `Timezone` option was the only way to set it, and Npgsql applied it. Moving such
        // a connection onto this machine's zone on upgrade would change its results without anyone asking.
        var info = Info(options: new() { ["Timezone"] = "UTC" });
        Assert.Equal("UTC", SessionTimeZonePolicy.Setting(info));
        Assert.Equal("UTC", Sent(info));
    }

    [Fact]
    public void The_field_outranks_the_bag()
    {
        // And the bag is reserved, so it is not applied a second time over what the field resolved to.
        var info = Info("Europe/Zagreb", new() { ["timezone"] = "UTC" });
        Assert.Equal("Europe/Zagreb", Sent(info));
    }

    // ---- what the user is told ------------------------------------------------------------------

    [Fact]
    public void Every_setting_names_the_zone_and_where_it_came_from()
    {
        Assert.Equal("the server's own zone", SessionTimeZonePolicy.Describe(SessionTimeZonePolicy.Server));
        Assert.Equal("UTC", SessionTimeZonePolicy.Describe("UTC"));
        if (SessionTimeZonePolicy.LocalIanaId() is { } local)
            Assert.Equal($"{local} (this machine)", SessionTimeZonePolicy.Describe(SessionTimeZonePolicy.Local));
    }

    [Fact]
    public void The_hint_says_what_a_typed_zone_is_sent_as_and_whether_it_is_known()
    {
        Assert.Contains("is sent as Europe/Budapest", SessionTimeZonePolicy.Advice("Central Europe Standard Time"));
        Assert.Contains("doesn't recognise", SessionTimeZonePolicy.Advice("Mars/Olympus_Mons"));
        Assert.DoesNotContain("recognise", SessionTimeZonePolicy.Advice("Europe/Zagreb"));
        // It separates the two settings people confuse, which is what #163 was.
        Assert.Contains("separate setting", SessionTimeZonePolicy.Advice(SessionTimeZonePolicy.Local));
    }

    [Fact]
    public void Only_postgres_has_a_session_zone_to_set()
    {
        var registry = new ProviderRegistry();
        Assert.True(registry.Get(PostgresProvider.ProviderId).SupportsSessionTimeZone);
        Assert.False(registry.Get(SqlServer.SqlServerProvider.ProviderId).SupportsSessionTimeZone);
    }

    // ---- literals built as text -----------------------------------------------------------------

    [Fact]
    public void A_timestamptz_literal_states_its_offset()
    {
        // Copy as SQL, the SQL export and the FK lookup build literals as text, and the server reads an
        // offset-less one in the session's zone — this machine's since #163. So an instant carries +00:00, and
        // a timestamp without time zone, which has no zone to state, carries nothing.
        var instant = new DateTime(2026, 9, 18, 15, 0, 0, DateTimeKind.Utc);
        Assert.Equal("'2026-09-18 15:00:00+00:00'", SqlValue.Literal(instant));
        Assert.Equal("'2026-09-18 15:00:00'",
            SqlValue.Literal(DateTime.SpecifyKind(instant, DateTimeKind.Unspecified)));
    }

    // ---- against a real server ------------------------------------------------------------------

    private static async Task<object?> ScalarAsync(ConnectionInfo info, string sql)
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(info, PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);
        var result = await provider.CreateQueryExecutor(factory)
            .ExecuteAsync(sql, new QueryOptions(), CancellationToken.None);
        Assert.True(result[0].Success, result[0].Error?.Message);
        return result[0].Rows[0][0];
    }

    [SkippableFact]
    public async Task The_server_computes_in_the_zone_asked_for()
    {
        // The issue's own repro. 18:00 UTC is 20:00 in Zagreb in September, and the mixed subtraction is the
        // offset — what DBeaver returned and Bearing did not.
        var info = PgTestServer.Info() with { SessionTimeZone = "Europe/Zagreb" };

        Assert.Equal("Europe/Zagreb", await ScalarAsync(info, "show timezone"));
        Assert.Equal(new DateTime(2026, 9, 18, 20, 0, 0),
            await ScalarAsync(info, "select timestamptz '2026-09-18 18:00:00+00'::timestamp"));
        Assert.Equal(TimeSpan.FromHours(2),
            await ScalarAsync(info, "select timestamptz '2026-09-18 18:00:00+00' - timestamp '2026-09-18 18:00:00'"));
    }

    [SkippableFact]
    public async Task A_copied_timestamptz_means_the_same_instant_in_a_session_off_utc()
    {
        // The regression the literal fix closes, end to end: read a timestamptz, render it as SQL, and ask a
        // Zagreb session whether the text is the same instant. Offset-less, it was two hours off.
        var info = PgTestServer.Info() with { SessionTimeZone = "Europe/Zagreb" };
        var value = Assert.IsType<DateTime>(await ScalarAsync(info, "select timestamptz '2026-09-18 15:00:00+00'"));
        Assert.Equal(DateTimeKind.Utc, value.Kind);

        Assert.Equal(true, await ScalarAsync(info,
            $"select {SqlValue.Literal(value)}::timestamptz = timestamptz '2026-09-18 15:00:00+00'"));
    }

    [SkippableFact]
    public async Task A_windows_zone_id_opens_a_connection_rather_than_failing_it()
    {
        // Unconverted, the server refuses this at startup ("invalid value for parameter TimeZone").
        var info = PgTestServer.Info() with { SessionTimeZone = "Central Europe Standard Time" };
        Assert.Equal("Europe/Budapest", await ScalarAsync(info, "show timezone"));
    }

    [SkippableTheory]
    [InlineData(null, "client")]
    [InlineData("UTC", "client")]
    public async Task A_zone_we_send_is_the_clients_as_far_as_the_server_is_concerned(string? zone, string source)
    {
        // pg_settings.source is the server's own record of who decided the value. "client" is the startup
        // packet — which is also what says the default (this machine's) is really being sent.
        var info = PgTestServer.Info() with { SessionTimeZone = zone };
        Assert.Equal(source, await ScalarAsync(info, "select source from pg_settings where name = 'TimeZone'"));
    }

    [SkippableFact]
    public async Task The_server_setting_leaves_the_zone_to_the_server()
    {
        Skip.If(Environment.GetEnvironmentVariable("PGTZ") is not null,
            "PGTZ is set, and Npgsql sends it when Bearing sends nothing — as libpq does.");
        var info = PgTestServer.Info() with { SessionTimeZone = SessionTimeZonePolicy.Server };
        Assert.NotEqual("client", await ScalarAsync(info, "select source from pg_settings where name = 'TimeZone'"));
    }

    [SkippableFact]
    public async Task Every_connection_in_the_pool_computes_in_the_same_zone()
    {
        // §1.9's reason for the startup packet, for this setting: a SET would reach one socket of up to ten.
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        var info = PgTestServer.Info() with { SessionTimeZone = "America/Los_Angeles" };
        await using var factory = provider.CreateConnectionFactory(info, PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        var probes = Enumerable.Range(0, 10).Select(_ => exec.ExecuteAsync(
            "select current_setting('TimeZone'), pg_sleep(0.05)::text", new QueryOptions(), CancellationToken.None)).ToList();

        foreach (var probe in await Task.WhenAll(probes))
            Assert.Equal("America/Los_Angeles", Assert.IsType<string>(probe[0].Rows[0][0]));
    }
}
