using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.SqlServer;
using Xunit;

namespace Bearing.Testing;

/// <summary>
/// The live Microsoft SQL Server the integration suites run against, and the single place its defaults
/// live. Linked into the test projects from <c>tests/Shared/</c> rather than copied, for the reason
/// <see cref="PgTestServer"/> spells out: its six copies drifted onto two different ports, so the same
/// <c>dotnet test</c> hit two different servers depending on which suite was running.
/// <para>
/// Point it elsewhere with <c>BEARING_TEST_MSSQL_{HOST,PORT,DB,USER,PASSWORD}</c>. Nothing here fails a
/// run: per §4.2 an unreachable server <b>skips</b>, so the suite stays green off a dev box — which is the
/// normal case, since no SQL Server is installed on any machine that has built this branch so far.
/// </para>
/// <para>
/// Unlike the Postgres suites, these tests do not need a sample database loaded: every one of them creates
/// the objects it asserts on and drops them again, so any database the login may write to will do. A bare
/// official container is enough —
/// <c>docker run -d --name bearing-mssql-test -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Bearing!Test1'
/// -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest</c> followed by
/// <c>create database bearing_test</c>. The database is not <c>master</c> on purpose: these tests create
/// and drop tables, and doing that in the server's own book-keeping database is not something a test
/// should decide on a developer's behalf.
/// </para>
/// </summary>
public static class MsSqlTestServer
{
    /// <summary>SQL Server's default TCP port. A <b>named instance</b> ignores it (the SQL Browser hands
    /// back a dynamic port instead), so <c>BEARING_TEST_MSSQL_HOST=box\SQLEXPRESS</c> works with whatever
    /// this says — see <c>SqlServerConnectionFactory.DataSourceFor</c>.</summary>
    private const string DefaultPort = "1433";

    public static string Host => Env("HOST", "localhost");
    public static int Port => int.TryParse(Env("PORT", DefaultPort), out var p) ? p : int.Parse(DefaultPort);
    public static string Database => Env("DB", "bearing_test");
    public static string User => Env("USER", "sa");

    /// <summary>The documented container password above, not a secret: it never leaves a dev box, and the
    /// real thing would belong in the OS credential store like every other password here (§1.1).</summary>
    public static string Password => Env("PASSWORD", "Bearing!Test1");

    /// <summary>What the skip message names, so a misdirected run says which server it actually tried.
    /// A named instance carries no meaningful port, so it is not printed — the same honesty
    /// <c>ConnectionSessionManager.Describe</c> owes the status bar.</summary>
    public static string Endpoint => Host.Contains('\\')
        ? $"{Host}/{Database}"
        : $"{Host}:{Port}/{Database}";

    public static string Env(string key, string dflt)
        => Environment.GetEnvironmentVariable($"BEARING_TEST_MSSQL_{key}") ?? dflt;

    /// <summary>A <see cref="ConnectionInfo"/> pointed at the test server, with a fresh id per call.</summary>
    public static ConnectionInfo Info(string name = "mssql-test") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = SqlServerProvider.ProviderId,
        Host = Host,
        Port = Port,
        Database = Database,
        User = User,
    };

    /// <summary>
    /// Why the server can't be used, or null when it can. The reason rather than a bare bool, for the
    /// reason <see cref="PgTestServer.UnreachableReasonAsync"/> gives: "nothing listening", "wrong
    /// password", "no such database" and "the certificate isn't trusted" are four different problems, and
    /// the last one is specific to this engine — SqlClient 4.0+ encrypts by default, so a dev server with a
    /// self-signed certificate is reached and then refused. Collapsing that into "unreachable" would have a
    /// developer checking their firewall instead of setting
    /// <c>BEARING_TEST_MSSQL_*</c> plus <c>TrustServerCertificate</c>.
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

    private static readonly object ProbeGate = new();
    private static Task<string?>? _probe;

    /// <summary>
    /// Skips the calling <c>[SkippableFact]</c> unless <paramref name="factory"/>'s server answers.
    /// <para>
    /// The probe runs <b>once per test run</b> and every later caller awaits that same answer.
    /// <see cref="PgTestServer"/> probes per test because a Postgres that is not there refuses the socket
    /// immediately; SqlClient instead spends its full connect timeout (15s by default) before giving up, so
    /// a per-test probe added a quarter of an hour to <c>dotnet test</c> on a box with no SQL Server — which
    /// is every box so far. The cost of caching is that starting the server <em>during</em> a run does not
    /// un-skip the rest of it, which is a trade a developer can see and re-running fixes.
    /// </para>
    /// </summary>
    public static async Task RequireAsync(IDbConnectionFactory factory)
    {
        Task<string?> probe;
        // Deliberately not the caller's token: the probe is shared, so one test's cancellation must not
        // decide the verdict for the others.
        lock (ProbeGate) probe = _probe ??= UnreachableReasonAsync(factory, CancellationToken.None);

        var reason = await probe.ConfigureAwait(false);
        Skip.If(reason is not null, $"No SQL Server reachable for integration test at {Endpoint} — "
            + $"{reason?.TrimEnd('.', ' ')}. "
            + "Set BEARING_TEST_MSSQL_{HOST,PORT,DB,USER,PASSWORD} to point at your server.");
    }

    /// <summary>As above, probing the default endpoint with a throwaway factory of its own. No token, for
    /// the reason the overload above gives: the probe is shared, so no one caller owns its lifetime.</summary>
    public static async Task RequireAsync()
    {
        await using var factory = new ProviderRegistry()
            .Get(SqlServerProvider.ProviderId)
            .CreateConnectionFactory(Info("probe"), Password);
        await RequireAsync(factory).ConfigureAwait(false);
    }
}
