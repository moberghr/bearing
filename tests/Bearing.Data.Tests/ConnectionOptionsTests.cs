using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Testing;
using Npgsql;
using Xunit;
using Xunit.Sdk;

namespace Bearing.Data.Tests;

/// <summary>
/// How <see cref="ConnectionInfo.Options"/> reaches (or doesn't reach) the Npgsql connection string. The bag
/// is user-editable and travels in the shared project.json, and it also holds app-level keys (`entra.*`), so
/// it can neither be forwarded wholesale to the driver nor allowed to override identity/credentials.
/// </summary>
public class ConnectionOptionsTests
{
    private static ConnectionInfo Info(Dictionary<string, string> options)
        => PgTestServer.Info() with { Options = options };

    private static string Password => PgTestServer.Password;

    /// <summary>An app-level key used to be handed to Npgsql, which threw an unwrapped exception at connect —
    /// making the documented `entra.resource` override unusable. It must simply be ignored by the driver.</summary>
    [SkippableFact]
    public async Task An_app_level_option_key_does_not_break_the_connection()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var plain = provider.CreateConnectionFactory(Info(new()), Password);
        await PgTestServer.RequireAsync(plain);

        await using var factory = provider.CreateConnectionFactory(
            Info(new() { ["entra.resource"] = "https://ossrdbms-aad.database.windows.net", ["not.a.driver.key"] = "x" }),
            Password);

        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));
    }

    /// <summary>A "Password" entry in the options bag must not beat the secret store — silently connecting as
    /// something other than the resolved credential is the worst possible failure mode here.</summary>
    [SkippableFact]
    public async Task Options_cannot_override_the_resolved_password()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var plain = provider.CreateConnectionFactory(Info(new()), Password);
        await PgTestServer.RequireAsync(plain);

        await using var factory = provider.CreateConnectionFactory(
            Info(new() { ["Password"] = "definitely-not-the-password" }), Password);

        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));
    }

    /// <summary>Filtering unknown keys must not swallow real ones: ApplicationName is a genuine Npgsql keyword,
    /// and the server can be asked what it received.</summary>
    [SkippableFact]
    public async Task A_real_driver_option_is_still_applied()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var plain = provider.CreateConnectionFactory(Info(new()), Password);
        await PgTestServer.RequireAsync(plain);

        await using var factory = provider.CreateConnectionFactory(
            Info(new() { ["ApplicationName"] = "bearing-option-test" }), Password);
        var executor = provider.CreateQueryExecutor(factory);

        var result = Assert.Single(await executor.ExecuteAsync(
            "select application_name from pg_stat_activity where pid = pg_backend_pid()",
            new QueryOptions(), CancellationToken.None));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("bearing-option-test", result.Rows[0][0]);
    }

    /// <summary>A known keyword with a nonsense *value* is a typo worth surfacing, so it still throws —
    /// unlike an unknown key, which is ignored.</summary>
    [Fact]
    public void A_bad_value_for_a_real_driver_option_still_throws()
        => Assert.ThrowsAny<Exception>(() =>
            new NpgsqlConnectionFactory(Info(new() { ["Timeout"] = "not-a-number" }), Password));
}
