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
    // 55434, not 5434, and the digit matters. 5434 is inside the ephemeral/short-lived range that
    // developer tooling hands out, and on at least one machine here it was an AWS Session Manager tunnel
    // forwarding to a *real remote* PostgreSQL: these defaults reached it and were turned away by its
    // pg_hba.conf, which read as "no server, skipping" and was "a server that refused us". Several tests in
    // this suite create and drop schemas. A port well outside the range anything else claims is the cheap
    // half of the fix; RequireWritableAsync's marker gate below is the half that does not depend on luck.
    private const string DefaultPort = "55434";

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

    /// <summary>The table <c>build/test-db.sh</c> stamps a provisioned database with.</summary>
    private const string MarkerTable = "public.bearing_test_marker";

    /// <summary>What <c>build/test-db.sh</c>'s stamp row says. Matched as a prefix, so the gate turns on the
    /// row's <em>content</em> and not merely on a table of that name existing — a half-applied stamp (the
    /// CREATE succeeded, the INSERT did not) and a same-named table belonging to something else both have to
    /// read as unstamped, or the gate is decoration.</summary>
    private const string MarkerNotePrefix = "provisioned by build/test-db.sh";

    /// <summary>Opt out of the stamp check for a hand-built server: <c>BEARING_TEST_PG_ALLOW_DDL=1</c>.</summary>
    private const string AllowDdlVariable = "ALLOW_DDL";

    /// <summary>
    /// Skips the calling test unless the server is reachable <b>and</b> has identified itself as a Bearing
    /// test database — for the tests that create and drop schemas.
    /// <para>
    /// Reachability is not ownership. The default endpoint on one dev machine turned out to be an AWS Session
    /// Manager tunnel to a real remote database: the suites were connecting to it and being refused by its
    /// <c>pg_hba.conf</c>, which looked exactly like "nothing is listening". Had the credentials matched, the
    /// tests that build a schema would have built it there. So a test that issues DDL asks for a stamp rather
    /// than for a connection.
    /// </para>
    /// <para>
    /// <c>build/test-db.sh</c> writes the stamp. A server provisioned by hand can set
    /// <c>BEARING_TEST_PG_ALLOW_DDL=1</c> instead — an explicit assertion by someone who knows what they are
    /// pointed at, which is the whole property being protected.
    /// </para>
    /// </summary>
    public static async Task RequireWritableAsync(IDbConnectionFactory factory, CancellationToken ct = default)
    {
        await RequireAsync(factory, ct).ConfigureAwait(false);
        if (DdlExplicitlyAllowed) return;

        var stamped = await IsStampedAsync(factory, ct).ConfigureAwait(false);
        Skip.IfNot(stamped,
            $"{Endpoint} is reachable but is not stamped as a Bearing test database, and this test creates "
            + $"and drops objects. Provision one with ./build/test-db.sh, or set "
            + $"BEARING_TEST_PG_{AllowDdlVariable}=1 if you are certain this server is disposable.");
    }

    /// <summary>Why a reachable-but-unstamped server is skipped, for a fixture that checks it once.</summary>
    public static string NotStampedReason =>
        $"{Endpoint} is reachable but is not stamped as a Bearing test database, and this fixture creates "
        + $"and drops objects. Provision one with ./build/test-db.sh, or set "
        + $"BEARING_TEST_PG_{AllowDdlVariable}=1 if you are certain this server is disposable.";

    /// <summary>Whether this server may be written to — stamped, or explicitly allowed. For a fixture that
    /// has to decide in <c>InitializeAsync</c>, where skipping would fail the test instead.</summary>
    public static async Task<bool> IsWritableAsync(IDbConnectionFactory factory, CancellationToken ct = default)
        => DdlExplicitlyAllowed || await IsStampedAsync(factory, ct).ConfigureAwait(false);

    /// <summary>
    /// Whether the operator has asserted this server is disposable. Only <c>1</c>, <c>true</c> and <c>yes</c>
    /// count, case-insensitively.
    /// <para>
    /// Deliberately strict, because the old reading was "anything but 0" — under which
    /// <c>BEARING_TEST_PG_ALLOW_DDL=false</c> turned DDL <b>on</b>. For a gate whose failure mode is dropping
    /// schemas on a server nobody meant to point at, an unrecognised value has to mean off.
    /// </para>
    /// </summary>
    private static bool DdlExplicitlyAllowed => Env(AllowDdlVariable, "").Trim().ToLowerInvariant()
        is "1" or "true" or "yes";

    private static async Task<bool> IsStampedAsync(IDbConnectionFactory factory, CancellationToken ct)
    {
        try
        {
            var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
            var results = await provider.CreateQueryExecutor(factory)
                .ExecuteAsync(
                    $"select count(*) from {MarkerTable} where note like '{MarkerNotePrefix}%'",
                    new QueryOptions(), ct)
                .ConfigureAwait(false);

            if (results.Count == 0 || !results[0].Success || results[0].Rows.Count == 0) return false;

            // The *value*, not the row count. `select count(*)` returns one row whether the table holds a
            // stamp or nothing at all, so `Rows.Count > 0` was true for any table of that name — which is
            // precisely the "reachable, therefore ours" inference this gate exists to refuse.
            return results[0].Rows[0] is [{ } scalar, ..] && Convert.ToInt64(scalar) > 0;
        }
        catch (Exception)
        {
            // No such table, no permission, no connection: none of them is a stamp.
            return false;
        }
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
