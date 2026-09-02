using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.SqlServer;
using Bearing.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// How <see cref="ConnectionInfo.Options"/> reaches (or doesn't reach) the SqlClient connection string —
/// the SQL Server counterpart of <see cref="ConnectionOptionsTests"/>. The bag is user-editable, travels in
/// the shared project.json, and doubles as app-level config (the <c>entra.*</c> keys), so it can neither be
/// forwarded wholesale to the driver nor allowed to override identity or credentials (§1.1).
/// <para>
/// The first three tests need no server: they exercise the factory's three-way switch through the one thing
/// it can be observed by without a connection — whether the <see cref="SqlConnectionStringBuilder"/> was
/// handed the value at all. A bad value for a real keyword throws; a bad value for a key the filter drops
/// cannot, because the builder never sees it. That is a stronger assertion than reading the connection
/// string back would be, and it does not need the string to be exposed.
/// </para>
/// </summary>
public class SqlServerConnectionOptionsTests
{
    private static ConnectionInfo Info(Dictionary<string, string> options)
        => MsSqlTestServer.Info() with { Options = options };

    private static string Password => MsSqlTestServer.Password;

    /// <summary>A known keyword with a nonsense <em>value</em> is a typo worth surfacing, so it still
    /// throws — unlike an unknown key, which is ignored.</summary>
    [Fact]
    public void A_bad_value_for_a_real_driver_option_still_throws()
        => Assert.ThrowsAny<Exception>(() =>
            new SqlServerConnectionFactory(Info(new() { ["Connect Timeout"] = "not-a-number" }), Password));

    /// <summary>
    /// The reserved list is what stops an option bag from choosing who we connect as, and this is the proof
    /// it drops keys rather than merely losing an argument with them: <c>Integrated Security</c> is a real
    /// keyword whose value must be a boolean, so a builder that received <c>"not-a-bool"</c> would throw.
    /// Silence means the key never got there. Every synonym is checked because the builder accepts them all
    /// — missing one would be a credential-override hole, not a cosmetic gap.
    /// </summary>
    [Theory]
    [InlineData("Integrated Security")]
    [InlineData("integrated security")]   // matching is case-insensitive
    [InlineData("Trusted_Connection")]    // a synonym for the same switch
    public void A_reserved_boolean_keyword_is_dropped_before_the_builder_can_reject_it(string key)
    {
        var factory = new SqlServerConnectionFactory(Info(new() { [key] = "not-a-bool" }), Password);
        Assert.NotNull(factory);
    }

    /// <summary>The control for the test above: <c>Pooling</c> is an equally boolean keyword that is
    /// <em>not</em> reserved, so it does reach the builder and does throw. Without this, "no exception"
    /// could just mean the builder tolerates anything.</summary>
    [Fact]
    public void An_unreserved_boolean_keyword_still_reaches_the_builder()
        => Assert.ThrowsAny<Exception>(() =>
            new SqlServerConnectionFactory(Info(new() { ["Pooling"] = "not-a-bool" }), Password));

    /// <summary>An app-level key must simply be ignored: handing one to the driver throws at connect time,
    /// which is how the documented <c>entra.resource</c> override became unusable on the Postgres side.</summary>
    [Fact]
    public void An_app_level_option_key_is_ignored_rather_than_forwarded()
    {
        var factory = new SqlServerConnectionFactory(
            Info(new() { ["entra.resource"] = "https://database.windows.net/", ["not.a.driver.key"] = "x" }),
            Password);

        Assert.NotNull(factory);
    }

    /// <summary>
    /// The keys the dialog offers have to be the driver's own spelling, because a key SqlClient does not
    /// recognise is dropped <em>silently</em> by the case above — a mistyped <c>Encrypt</c> would look like
    /// a working toggle that never encrypts anything. This is that assertion, straight against the builder.
    /// </summary>
    [Fact]
    public void Every_option_field_the_provider_offers_is_a_real_sqlclient_keyword()
    {
        var builder = new SqlConnectionStringBuilder();
        // Host/Port/Database/User are ConnectionInfo columns and Password is the secret store's; everything
        // else a provider declares round-trips through Options and therefore through the driver.
        var intrinsic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Host", "Port", "Database", "User", "Password" };

        var optionFields = new SqlServerProvider().ConnectionFields
            .Where(f => !intrinsic.Contains(f.Key))
            .ToList();

        // Currently none: Encrypt/TrustServerCertificate became the typed ConnectionInfo.Tls (#23), so
        // every field this provider declares is intrinsic. The assertion is kept rather than deleted
        // because it is what would catch a future declared field that the factory would silently drop.
        Assert.Empty(optionFields);
        Assert.All(optionFields, f => Assert.True(
            builder.ContainsKey(f.Key),
            $"'{f.Key}' is not a SqlClient connection-string keyword, so the factory would drop it silently."));

        // Transport security is not here to have a default: ConnectionInfo.Tls owns it, and the factory
        // blocks both TLS keywords from the options bag so there is one source of truth (§1.4, #23).
        Assert.DoesNotContain("Encrypt", new SqlServerProvider().ConnectionFields.Select(f => f.Key));
    }

    // ---- Against a live server -------------------------------------------------------------------

    /// <summary>A "Password" entry in the options bag must not beat the secret store — silently connecting
    /// as something other than the resolved credential is the worst possible failure mode here. Every
    /// SqlClient synonym is tried, since the builder accepts all of them.</summary>
    [SkippableTheory]
    [InlineData("Password")]
    [InlineData("pwd")]
    [InlineData("User ID")]
    [InlineData("uid")]
    public async Task Options_cannot_override_the_resolved_credential(string key)
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var plain = provider.CreateConnectionFactory(Info(new()), Password);
        await MsSqlTestServer.RequireAsync(plain);

        await using var factory = provider.CreateConnectionFactory(
            Info(new() { [key] = "definitely-not-the-credential" }), Password);

        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));
    }

    /// <summary>The target is the connection's, not the bag's: pointing <c>Database</c> at a database that
    /// does not exist must not move the connection (or break it).</summary>
    [SkippableFact]
    public async Task Options_cannot_redirect_the_connection_to_another_database()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var plain = provider.CreateConnectionFactory(Info(new()), Password);
        await MsSqlTestServer.RequireAsync(plain);

        await using var factory = provider.CreateConnectionFactory(
            Info(new() { ["Initial Catalog"] = "no_such_database_here", ["Server"] = "no.such.host" }), Password);
        var executor = provider.CreateQueryExecutor(factory);

        var result = Assert.Single(await executor.ExecuteAsync(
            "select db_name() as db", new QueryOptions(), CancellationToken.None));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(MsSqlTestServer.Database, result.Rows[0][0]);
    }

    /// <summary>Filtering unknown keys must not swallow real ones: ApplicationName is a genuine SqlClient
    /// keyword, and the server can be asked what it received.</summary>
    [SkippableFact]
    public async Task A_real_driver_option_is_still_applied()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var plain = provider.CreateConnectionFactory(Info(new()), Password);
        await MsSqlTestServer.RequireAsync(plain);

        await using var factory = provider.CreateConnectionFactory(
            Info(new() { ["Application Name"] = "bearing-option-test" }), Password);
        var executor = provider.CreateQueryExecutor(factory);

        var result = Assert.Single(await executor.ExecuteAsync(
            "select app_name() as app", new QueryOptions(), CancellationToken.None));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("bearing-option-test", result.Rows[0][0]);
    }

    /// <summary>And without that option the connection still names itself, so a DBA looking at
    /// <c>sys.dm_exec_sessions</c> sees which tool is holding a session.</summary>
    [SkippableFact]
    public async Task The_application_name_defaults_to_bearing()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(new()), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var executor = provider.CreateQueryExecutor(factory);
        var result = Assert.Single(await executor.ExecuteAsync(
            "select app_name() as app", new QueryOptions(), CancellationToken.None));

        Assert.Equal("bearing", result.Rows[0][0]);
    }
}
