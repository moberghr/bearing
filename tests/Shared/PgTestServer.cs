using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Xunit;

namespace Bearing.Testing;

/// <summary>
/// The live PostgreSQL the integration suites run against, and the single place its defaults live. Linked
/// into both test projects (<c>tests/Shared/</c>) rather than copied, because it was copied six times and the
/// copies drifted: four files defaulted to port 5433 while two said 5434, so the same
/// <c>dotnet test</c> hit two different servers depending on which suite was running.
/// <para>
/// Point it elsewhere with <c>BEARING_TEST_PG_{HOST,PORT,DB,USER,PASSWORD}</c>. Nothing here fails a run:
/// per §4.2 an unreachable server <b>skips</b>, so the suite stays green off a dev box.
/// </para>
/// </summary>
public static class PgTestServer
{
    /// <summary>The docker container this repo's tests expect (`squirrel-pg-test`, pagila loaded).</summary>
    private const string DefaultPort = "5434";

    public static string Host => Env("HOST", "localhost");
    public static int Port => int.TryParse(Env("PORT", DefaultPort), out var p) ? p : int.Parse(DefaultPort);
    public static string Database => Env("DB", "pagila");
    public static string User => Env("USER", "postgres");
    public static string Password => Env("PASSWORD", "squirrel");

    /// <summary>What the skip message names, so a misdirected run says which server it actually tried.</summary>
    public static string Endpoint => $"{Host}:{Port}/{Database}";

    public static string Env(string key, string dflt)
        => Environment.GetEnvironmentVariable($"BEARING_TEST_PG_{key}") ?? dflt;

    /// <summary>A <see cref="ConnectionInfo"/> pointed at the test server, with a fresh id per call.</summary>
    public static ConnectionInfo Info(string name = "pagila-test") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = PostgresProvider.ProviderId,
        Host = Host,
        Port = Port,
        Database = Database,
        User = User,
    };

    /// <summary>
    /// Why the server can't be used, or null when it can. Returning the reason rather than a bare bool is the
    /// point: "wrong port", "wrong password", "no such database" and "nothing listening" are four different
    /// problems that a <c>catch { return false; }</c> reports as one — which is exactly how a stale 5433
    /// default went unnoticed while a *different* project's Postgres answered on that port.
    /// </summary>
    public static async Task<string?> UnreachableReasonAsync(IDbConnectionFactory factory, CancellationToken ct = default)
    {
        try
        {
            return await factory.TestConnectionAsync(ct).ConfigureAwait(false)
                ? null
                : "the server refused a test connection without raising an error.";
        }
        catch (Exception ex)
        {
            // §1.1 — the message can quote the whole connection string, and these run in CI output.
            return SafeErrorText.Of(ex);
        }
    }

    /// <summary>Skips the calling <c>[SkippableFact]</c> unless <paramref name="factory"/>'s server answers.</summary>
    public static async Task RequireAsync(IDbConnectionFactory factory, CancellationToken ct = default)
    {
        var reason = await UnreachableReasonAsync(factory, ct).ConfigureAwait(false);
        Skip.If(reason is not null, $"No PostgreSQL reachable for integration test at {Endpoint} — "
            + $"{reason?.TrimEnd('.', ' ')}. "
            + "Set BEARING_TEST_PG_{HOST,PORT,DB,USER,PASSWORD} to point at your server.");
    }

    /// <summary>As above, probing the default endpoint with a throwaway factory of its own.</summary>
    public static async Task RequireAsync(CancellationToken ct = default)
    {
        await using var factory = new ProviderRegistry()
            .Get(PostgresProvider.ProviderId)
            .CreateConnectionFactory(Info("probe"), Password);
        await RequireAsync(factory, ct).ConfigureAwait(false);
    }
}
