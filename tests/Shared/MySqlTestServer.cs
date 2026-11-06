using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.MySql;
using Xunit;

namespace Bearing.Testing;

/// <summary>Which of the two engines the MySQL provider serves a live test is aimed at.</summary>
public enum MySqlEngine
{
    MariaDb,
    MySql,
}

/// <summary>
/// The MariaDB and MySQL servers the MySql* live suites use — <see cref="MsSqlTestServer"/>'s sibling, with
/// two endpoints because one provider serves both engines and a suite run against only one would prove half
/// of it. Defaults match <c>build/test-db-mysql.sh</c>; override with <c>BEARING_TEST_MARIADB_*</c> /
/// <c>BEARING_TEST_MYSQL_*</c>. Unreachable means skipped (§4.2) — and a skip is not a pass.
/// </summary>
public static class MySqlTestServer
{
    public static string Host(MySqlEngine engine) => Env(engine, "HOST", "localhost");

    public static int Port(MySqlEngine engine)
        => int.TryParse(Env(engine, "PORT", engine == MySqlEngine.MariaDb ? "13306" : "13307"), out var p) ? p : 0;

    public static string Database => Environment.GetEnvironmentVariable("BEARING_TEST_MYSQL_DB") ?? "bearing_test";

    public static string User => Environment.GetEnvironmentVariable("BEARING_TEST_MYSQL_USER") ?? "root";

    public static string Password => Environment.GetEnvironmentVariable("BEARING_TEST_MYSQL_PASSWORD") ?? "Bearing!Test1";

    private static string Env(MySqlEngine engine, string key, string dflt)
        => Environment.GetEnvironmentVariable(
               $"BEARING_TEST_{(engine == MySqlEngine.MariaDb ? "MARIADB" : "MYSQL")}_{key}") ?? dflt;

    public static string Endpoint(MySqlEngine engine) => $"{Host(engine)}:{Port(engine)}/{Database}";

    public static ConnectionInfo Info(MySqlEngine engine, string name = "mysql-test") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = MySqlProvider.ProviderId,
        Host = Host(engine),
        Port = Port(engine),
        Database = Database,
        User = User,
        // The containers have self-signed certificates; Prefer encrypts where offered without verifying.
        Tls = TlsMode.Prefer,
    };

    public static IDbConnectionFactory Factory(MySqlEngine engine, ConnectionInfo? info = null)
        => new ProviderRegistry().Get(MySqlProvider.ProviderId).CreateConnectionFactory(info ?? Info(engine), Password);

    private static readonly object Gate = new();
    private static readonly Dictionary<MySqlEngine, Task<string?>> Probes = new();

    /// <summary>Skip unless <paramref name="engine"/>'s server answers. Probed once per engine per run.</summary>
    public static async Task RequireAsync(MySqlEngine engine)
    {
        Task<string?> probe;
        lock (Gate)
        {
            if (!Probes.TryGetValue(engine, out probe!))
            {
                probe = Probes[engine] = ProbeAsync(engine);
            }
        }

        var reason = await probe.ConfigureAwait(false);
        Skip.If(reason is not null, $"No {engine} reachable for integration test at {Endpoint(engine)} — "
            + $"{reason?.TrimEnd('.', ' ')}. Start one with ./build/test-db-mysql.sh.");
    }

    private static async Task<string?> ProbeAsync(MySqlEngine engine)
    {
        try
        {
            await using var factory = Factory(engine);
            return await factory.TestConnectionAsync(CancellationToken.None).ConfigureAwait(false)
                ? null
                : "the server refused a test connection without raising an error.";
        }
        catch (Exception ex)
        {
            return SafeErrorText.Of(ex);
        }
    }
}
