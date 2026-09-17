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
    /// Seconds between Npgsql's own keepalive messages. Present because <c>CommandTimeout</c> is 0: something
    /// has to notice a dead connection, and a keepalive can tell "the link is gone" from "the query is slow",
    /// which a command timeout cannot.
    /// </summary>
    public const int DefaultKeepAliveSeconds = 30;

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
        // …and every other keyword that decides how much the transport is trusted. Reserving sslmode alone
        // was not enough: "Trust Server Certificate=True" beside "SSL Mode=VerifyFull" turns verification off
        // while the dialog still reads Verify Full — a shared project.json defeating the setting is exactly
        // the threat this list exists to close.
        "trustservercertificate", "trust server certificate",
        "rootcertificate", "root certificate",
        "sslcertificate", "ssl certificate",
        "sslkey", "ssl key", "sslpassword", "ssl password",
        "sslnegotiation", "ssl negotiation",
        "checkcertificaterevocation", "check certificate revocation",
        // The startup packet, which carries the read-only and statement-timeout settings (#99 / #105). Both
        // are typed fields composed by StartupOptionsFor below, and this keyword is the whole packet rather
        // than one entry in it — so a bag key here would not merge with what we composed, it would replace
        // it, and a shared project.json could turn read-only off. Same threat as "Trust Server Certificate"
        // beside "SSL Mode=VerifyFull", and closed the same way.
        StartupOptionsKeyword,
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
            // Read-only and the statement timeout (#99 / #105). Null when the connection asks for neither,
            // which leaves the keyword out of the connection string entirely.
            Options = StartupOptionsFor(info),
            // No command timeout. Npgsql defaults to 30 seconds, which killed any query that took longer and
            // reported it as "Exception while reading from stream" — a message about the driver's plumbing,
            // for a query that was working. Running a slow analytical query is the point of the tool, and
            // Esc already cancels one, so a clock we impose can only get in the way. Override per connection
            // with a "CommandTimeout" entry in its options, the same way MaxPoolSize is overridable.
            CommandTimeout = 0,
            // What makes an unlimited command timeout safe rather than reckless: with no clock on the
            // command, a genuinely dead socket would otherwise be waited on forever. Npgsql sends its own
            // keepalive on this interval and fails the connection when one goes unanswered, so a broken
            // link is still noticed — by the thing that can actually tell the difference.
            KeepAlive = DefaultKeepAliveSeconds,
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

    /// <summary>The connection-string keyword carrying the startup packet — Postgres' own
    /// <c>options</c> parameter, as Npgsql spells it.</summary>
    private const string StartupOptionsKeyword = "options";

    /// <summary>
    /// The startup options for this connection's session settings (#99 / #105), or null when it asks for
    /// neither. Postgres' own <c>-c guc=value</c> syntax, which is why this lives here and not beside the
    /// neutral policy in <c>Core</c> (§2.1) — the same split <see cref="SslModeOf"/> makes for TLS.
    /// <para>
    /// <b>Why the startup packet and not a <c>SET</c>.</b> #99 and #105 both proposed
    /// <c>SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY</c> / <c>SET statement_timeout</c>, and
    /// neither can be issued here. A pool holds up to <see cref="DefaultMaxPoolSize"/> physical connections
    /// per (connection, database) and every read path opens one straight from the data source, so a
    /// <c>SET</c> reaches the one socket it was issued on and none of the others — and the idle sweep plus
    /// Npgsql's own pruning means those sockets come and go under a live session. A connection would be
    /// read-only or not depending on which socket a statement happened to land on. Postgres applies the
    /// startup packet to <b>every</b> physical connection before it can run anything, which is the only
    /// mechanism here that is true of. Do not "simplify" this into a SET.
    /// </para>
    /// <para>
    /// The timeout goes on the wire in milliseconds — the GUC's own unit when no suffix is given — so nothing
    /// depends on Postgres parsing a unit out of a startup value. The server normalizes it back, which is why
    /// <c>show statement_timeout</c> answers <c>30s</c> for the 30000 we sent.
    /// </para>
    /// </summary>
    public static string? StartupOptionsFor(ConnectionInfo info)
    {
        var parts = new List<string>(2);
        if (SessionPolicy.IsReadOnly(info)) parts.Add("-c default_transaction_read_only=on");
        if (SessionPolicy.TimeoutSeconds(info) is var seconds && seconds > SessionPolicy.NoTimeout)
            parts.Add($"-c statement_timeout={seconds * 1000}");
        return parts.Count == 0 ? null : string.Join(' ', parts);
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
