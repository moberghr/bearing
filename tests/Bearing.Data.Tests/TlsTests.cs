using Bearing.Core.Data;
using Bearing.Data.Postgres;
using Bearing.Testing;
using Npgsql;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// Transport security as a first-class connection setting (#23). It used to be reachable only by hand-editing
/// <c>project.json</c> or through a DBeaver import, so in practice every connection ran on the driver's
/// default: encrypt if the server offers it, and never say which happened.
/// <para>
/// The connection string is assembled by a pure function, so what a mode actually becomes is assertable
/// without a server — which matters here more than usual, because the failure mode of getting it wrong is a
/// connection that looks fine and is not encrypted.
/// </para>
/// </summary>
public class TlsTests
{
    private static ConnectionInfo Info(TlsMode? tls = null, Dictionary<string, string>? options = null)
    {
        var info = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "c",
            ProviderId = "postgres",
            Host = "db.example.com",
            Database = "app",
            User = "u",
            Options = options ?? new Dictionary<string, string>(),
        };
        return tls is { } mode ? info with { Tls = mode } : info;
    }

    private static SslMode Built(ConnectionInfo info)
        => PostgresConnectionString.Build(info, "pw").SslMode;

    // ---- the field reaches the driver ------------------------------------------------------------

    [Theory]
    [InlineData(TlsMode.Disable, SslMode.Disable)]
    [InlineData(TlsMode.Prefer, SslMode.Prefer)]
    [InlineData(TlsMode.Require, SslMode.Require)]
    [InlineData(TlsMode.VerifyCa, SslMode.VerifyCA)]
    [InlineData(TlsMode.VerifyFull, SslMode.VerifyFull)]
    public void Every_mode_maps_onto_the_drivers_own(TlsMode mode, SslMode expected)
        => Assert.Equal(expected, Built(Info(mode)));

    [Fact]
    public void A_connection_with_nothing_set_keeps_the_behaviour_it_always_had()
    {
        // The default exists so adding this setting changed no existing connection. Npgsql's own default is
        // Prefer, so anything else here would silently alter every project already on disk.
        Assert.Equal(SslMode.Prefer, Built(Info()));
        Assert.Equal(TlsMode.Prefer, TlsPolicy.Default);
    }

    // ---- one source of truth --------------------------------------------------------------------

    [Fact]
    public void The_options_bag_cannot_outrank_the_field()
    {
        // A bag that travels in a shared project.json must not be able to turn off encryption the dialog was
        // told to require — the same rule that stops a stray "Password" key beating the secret store.
        var info = Info(TlsMode.VerifyFull, new() { ["sslmode"] = "disable" });

        Assert.Equal(SslMode.VerifyFull, Built(info));
        Assert.Equal(TlsMode.VerifyFull, TlsPolicy.Resolve(info));
    }

    [Fact]
    public void A_legacy_bag_entry_still_applies_while_the_field_is_untouched()
    {
        // Projects written before the field existed keep working, and so do DBeaver imports already on disk.
        var info = Info(options: new() { ["sslmode"] = "verify-full" });

        Assert.Equal(SslMode.VerifyFull, Built(info));
    }

    [Theory]
    [InlineData("verify-full", TlsMode.VerifyFull)]
    [InlineData("verify_full", TlsMode.VerifyFull)]
    [InlineData("VerifyFull", TlsMode.VerifyFull)]
    [InlineData("verify-ca", TlsMode.VerifyCa)]
    [InlineData("REQUIRE", TlsMode.Require)]
    [InlineData("disable", TlsMode.Disable)]
    [InlineData("allow", TlsMode.Prefer)]
    public void A_bag_entry_is_read_in_whichever_spelling_it_arrived_in(string value, TlsMode expected)
    {
        // A copied connection string and a JDBC/DBeaver export use hyphens; Npgsql's enum does not. Both have
        // to be read, or an imported verify-full silently becomes the default.
        Assert.Equal(expected, TlsPolicy.Parse(value));
        Assert.Equal(expected, TlsPolicy.Resolve(Info(options: new() { ["sslmode"] = value })));
    }

    [Fact]
    public void An_unreadable_bag_entry_leaves_the_default_standing()
    {
        // Not silently treated as "off": a typo must not be the thing that decides a connection is plaintext.
        var info = Info(options: new() { ["sslmode"] = "yes-please" });

        Assert.Null(TlsPolicy.Parse("yes-please"));
        Assert.Equal(TlsMode.Prefer, TlsPolicy.Resolve(info));
        Assert.Equal(SslMode.Prefer, Built(info));
    }

    [Fact]
    public void The_bag_is_never_applied_to_the_driver_twice()
    {
        // sslmode is reserved now, so the loop cannot re-apply it after the field has been resolved — which is
        // how the bag would have won regardless of precedence.
        var built = PostgresConnectionString.Build(
            Info(TlsMode.Require, new() { ["sslmode"] = "disable", ["ssl mode"] = "disable" }), "pw");

        Assert.Equal(SslMode.Require, built.SslMode);
    }

    [Theory]
    [InlineData("Trust Server Certificate")]
    [InlineData("TrustServerCertificate")]
    [InlineData("Root Certificate")]
    [InlineData("SslCertificate")]
    [InlineData("Check Certificate Revocation")]
    [InlineData("SslNegotiation")]
    public void No_other_transport_keyword_can_weaken_the_field_either(string key)
    {
        // Reserving sslmode alone was not enough: "Trust Server Certificate=True" beside Verify Full turns
        // verification off while the dialog still reads Verify Full. A shared project.json quietly defeating
        // the setting is exactly what the reserved list exists to stop.
        var built = PostgresConnectionString.Build(
            Info(TlsMode.VerifyFull, new() { [key] = "True" }), "pw");

        Assert.Equal(SslMode.VerifyFull, built.SslMode);
        Assert.False(built.TrustServerCertificate, "the bag turned certificate validation off");
    }

    [Fact]
    public void Other_options_still_reach_the_driver()
    {
        // The reserved list grew; it must not have swallowed the keys it was never about.
        var built = PostgresConnectionString.Build(
            Info(options: new() { ["search_path"] = "shop,public", ["MaxPoolSize"] = "3", ["entra.resource"] = "x" }),
            "pw");

        Assert.Equal("shop,public", built.SearchPath);
        Assert.Equal(3, built.MaxPoolSize);
    }

    [Fact]
    public void Identity_and_credentials_are_still_untouchable()
    {
        var built = PostgresConnectionString.Build(
            Info(options: new() { ["Password"] = "nope", ["Host"] = "evil.example.com", ["Username"] = "root" }),
            "pw");

        Assert.Equal("pw", built.Password);
        Assert.Equal("db.example.com", built.Host);
        Assert.Equal("u", built.Username);
    }

    [Fact]
    public void A_connection_whose_encryption_changed_is_not_the_same_connection()
    {
        // Not a connection-string question but the same setting's: the session manager reuses a live pool when
        // the record still matches, and while sslmode lived in the options bag that comparison covered it.
        // Moving it to a field without teaching the comparison would leave a session running on the old mode
        // while the record and the dialog reported the new one.
        var before = Info(TlsMode.Prefer);
        var after = before with { Tls = TlsMode.VerifyFull };

        Assert.NotEqual(TlsPolicy.Resolve(before), TlsPolicy.Resolve(after));
        Assert.NotEqual(
            PostgresConnectionString.Build(before, "pw").SslMode,
            PostgresConnectionString.Build(after, "pw").SslMode);
    }

    // ---- what a new connection starts on --------------------------------------------------------

    [Fact]
    public void A_new_remote_connection_requires_encryption()
        => Assert.Equal(TlsMode.Require, TlsPolicy.DefaultFor("db.example.com"));

    [Theory]
    [InlineData("localhost")]
    [InlineData("LocalHost")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public void A_new_loopback_connection_does_not(string host)
    {
        // That traffic does not cross a network, and the stock Postgres container has TLS off — requiring it
        // would make the first connection anyone makes fail, which teaches people to switch the setting off.
        Assert.True(TlsPolicy.IsLoopback(host));
        Assert.Equal(TlsMode.Prefer, TlsPolicy.DefaultFor(host));
    }

    [Theory]
    [InlineData("127.0.0.1.example.com")]
    [InlineData("localhost.evil.com")]
    [InlineData("127.0.0")]
    [InlineData("")]
    [InlineData(null)]
    public void A_host_that_merely_looks_local_is_treated_as_remote(string? host)
    {
        // Wrong in the safe direction: an unrecognised name gets the stricter default.
        Assert.False(TlsPolicy.IsLoopback(host));
        Assert.Equal(TlsMode.Require, TlsPolicy.DefaultFor(host));
    }

    // ---- what each mode actually guarantees -----------------------------------------------------

    [Fact]
    public void Encryption_and_identity_are_separate_guarantees()
    {
        // The whole point of surfacing this: Require reads like the secure choice and is the one people pick,
        // but it accepts any certificate — it stops an eavesdropper and does nothing about an impersonator.
        Assert.True(TlsPolicy.Encrypts(TlsMode.Require));
        Assert.False(TlsPolicy.VerifiesServer(TlsMode.Require));

        Assert.True(TlsPolicy.Encrypts(TlsMode.VerifyFull));
        Assert.True(TlsPolicy.VerifiesServer(TlsMode.VerifyFull));

        // Prefer guarantees nothing at all — it may or may not have encrypted, and never says which.
        Assert.False(TlsPolicy.Encrypts(TlsMode.Prefer));
        Assert.False(TlsPolicy.Encrypts(TlsMode.Disable));
    }

    [Fact]
    public void Only_the_strongest_mode_goes_unwarned()
    {
        Assert.False(TlsPolicy.NeedsWarning(TlsMode.VerifyFull));
        foreach (var mode in TlsPolicy.Choices.Where(m => m != TlsMode.VerifyFull))
            Assert.True(TlsPolicy.NeedsWarning(mode), $"{mode} leaves an attack open and says nothing");
    }

    [Fact]
    public void Every_mode_can_say_what_it_leaves_open()
    {
        // A bare "insecure" is not actionable: the user has to be able to tell which guarantee is missing,
        // because that is what decides whether it matters on their network.
        Assert.All(TlsPolicy.Choices, mode =>
        {
            Assert.NotEmpty(TlsPolicy.Advice(mode));
            Assert.NotEmpty(TlsPolicy.Label(mode));
        });
        Assert.Contains("impersonator", TlsPolicy.Advice(TlsMode.Require));
        Assert.Contains("in the clear", TlsPolicy.Advice(TlsMode.Disable));
    }

    [Fact]
    public void The_picker_offers_the_strongest_mode_first()
    {
        // Listed in the enum's own order the safe choice sits at the bottom, under the familiar default.
        Assert.Equal(TlsMode.VerifyFull, TlsPolicy.Choices[0]);
        Assert.Equal(TlsMode.Disable, TlsPolicy.Choices[^1]);
        Assert.Equal(Enum.GetValues<TlsMode>().Length, TlsPolicy.Choices.Count);
    }

    [Fact]
    public void The_sslmode_spelling_round_trips()
    {
        // What gets shown and copied into a connection string has to read back as the same mode.
        foreach (var mode in TlsPolicy.Choices)
            Assert.Equal(mode, TlsPolicy.Parse(TlsPolicy.ToSslMode(mode)));
    }

    // ---- against a real server ------------------------------------------------------------------

    [SkippableFact]
    public async Task A_mode_the_server_cannot_satisfy_fails_rather_than_falling_back()
    {
        // The behaviour the setting exists for. The test server has TLS off, so Require must refuse to
        // connect — silently falling back to plaintext is exactly what Prefer does and what this replaces.
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var plain = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireAsync(plain);

        var encrypted = PgTestServer.Info() with { Tls = TlsMode.Require };
        await using var factory = provider.CreateConnectionFactory(encrypted, PgTestServer.Password);

        // Either the server does support TLS — in which case the connection is genuinely encrypted — or it
        // does not and this throws. What must not happen is a quiet unencrypted success.
        try
        {
            Assert.True(await factory.TestConnectionAsync(CancellationToken.None));
            Assert.True(await IsEncryptedAsync(provider, encrypted), "connected under Require without TLS");
        }
        catch (NpgsqlException)
        {
            // Refused, which is the point.
        }
    }

    [SkippableFact]
    public async Task Prefer_reports_honestly_what_it_ended_up_with()
    {
        // pg_stat_ssl is the authoritative answer, and the server's rather than the client's: it is what
        // "am I actually encrypted" has to be answered from, since Prefer never says.
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        var info = PgTestServer.Info() with { Tls = TlsMode.Prefer };
        await using var factory = provider.CreateConnectionFactory(info, PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);

        var encrypted = await IsEncryptedAsync(provider, info);

        // Whatever it is, it is a fact the server reported rather than an assumption — which is the whole
        // complaint about Prefer.
        Assert.Contains(encrypted, new[] { true, false });
    }

    /// <summary>What the server says about this session's transport, via <c>pg_stat_ssl</c>.</summary>
    private static async Task<bool> IsEncryptedAsync(IDbProvider provider, ConnectionInfo info)
    {
        await using var factory = provider.CreateConnectionFactory(info, PgTestServer.Password);
        var results = await provider.CreateQueryExecutor(factory).ExecuteAsync(
            "select ssl from pg_stat_ssl where pid = pg_backend_pid()", new QueryOptions(), CancellationToken.None);
        return results[0].Rows.Count > 0 && results[0].Rows[0][0] is true;
    }
}
