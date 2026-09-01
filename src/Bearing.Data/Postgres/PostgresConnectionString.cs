using Bearing.Core.Data;
using Npgsql;

namespace Bearing.Data.Postgres;

/// <summary>
/// Turns a <see cref="ConnectionInfo"/> plus a resolved password into Npgsql's connection settings. Split out
/// of <see cref="NpgsqlConnectionFactory"/> so the rules below — which keywords the options bag may set, and
/// which mode TLS ends up in — are testable without opening a connection (§2.5).
/// </summary>
public static class PostgresConnectionString
{
    /// <summary>Pooled connections per (connection, database). Well under Npgsql's default of 100: this is a
    /// desktop tool that runs one query per tab plus paging/count follow-ups, and a pool exists per database
    /// now rather than per connection (#54), so the default would have been an N x 100 ceiling on a server the
    /// user does not administer. Overridable through <see cref="ConnectionInfo.Options"/> ("MaxPoolSize"),
    /// which is applied after this.</summary>
    public const int DefaultMaxPoolSize = 10;

    /// <summary>
    /// Connection-string keywords <see cref="ConnectionInfo.Options"/> is not allowed to set: identity,
    /// credentials and the transport's security come from the connection record plus the secret store, never
    /// from an option bag that travels in the shared project.json.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "pwd",
        "host", "server", "port", "database", "db",
        "username", "user", "user id", "userid", "uid", "user name",
        // sslmode has a typed field of its own now (#23). It is still *read* from the bag for older projects,
        // but through TlsPolicy.Resolve below — never applied a second time from here, which would let the bag
        // silently outrank the field the dialog wrote.
        "sslmode", "ssl mode",
    };

    public static NpgsqlConnectionStringBuilder Build(ConnectionInfo info, string? password)
    {
        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = info.Host,
            Port = info.Port,
            Database = info.Database,
            Username = info.User,
            Password = password,
            ApplicationName = "bearing",
            MaxPoolSize = DefaultMaxPoolSize,
            SslMode = SslModeOf(TlsPolicy.Resolve(info)),
        };

        foreach (var (key, value) in info.Options)
        {
            switch (key.ToLowerInvariant())
            {
                case "search_path":
                    csb.SearchPath = value;
                    break;
                case var k when Reserved.Contains(k):
                    // Identity, credentials and transport security come from ConnectionInfo + the secret
                    // store. An Options entry must never override them — a stray "Password" key would beat
                    // the stored secret, and silently at that.
                    break;
                case var k when !csb.ContainsKey(k):
                    // Not a driver keyword: Options doubles as app-level config (the documented `entra.*`
                    // keys live here), and those used to reach Npgsql and throw an unwrapped exception at
                    // connect time, which made the feature unusable. Ignore what the driver doesn't own.
                    break;
                default:
                    // A real Npgsql keyword: apply it, and let a bad *value* still throw — that's a typo
                    // worth surfacing, unlike an unknown key.
                    csb[key] = value;
                    break;
            }
        }

        return csb;
    }

    /// <summary>Npgsql's spelling of a <see cref="TlsMode"/>. One-to-one: the modes exist because Postgres
    /// draws these exact lines, so translating them into anything else would lose the distinction.</summary>
    public static SslMode SslModeOf(TlsMode mode) => mode switch
    {
        TlsMode.Disable => SslMode.Disable,
        TlsMode.Prefer => SslMode.Prefer,
        TlsMode.Require => SslMode.Require,
        TlsMode.VerifyCa => SslMode.VerifyCA,
        TlsMode.VerifyFull => SslMode.VerifyFull,
        _ => SslMode.Prefer,
    };
}
