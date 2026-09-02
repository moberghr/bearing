using Microsoft.Data.SqlClient;
using Bearing.Core.Data;

namespace Bearing.Data.SqlServer;

/// <summary>
/// Builds and hands out pooled <see cref="SqlConnection"/>s for one connection's settings. The SQL Server
/// sibling of <see cref="Postgres.NpgsqlConnectionFactory"/>, hiding SqlClient from the rest of the app
/// behind <see cref="IDbConnectionFactory"/>.
/// <para>
/// <b>There is no <c>SqlDataSource</c>.</b> SqlClient pools internally, keyed by the connection string, so
/// this class keeps the string rather than a data-source object and opens a connection per use — the same
/// shape the Npgsql factory gets from <c>NpgsqlDataSource</c>. That string holds the password, so it never
/// leaves this object (§1.1).
/// </para>
/// </summary>
public sealed class SqlServerConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    /// <summary>Pooled connections per (connection, database), capped for the same reason Npgsql's is
    /// (§5.3): a pool exists per database rather than per connection (#54), so SqlClient's default of 100
    /// would have been an N x 100 ceiling on a server the user does not administer. Overridable through
    /// <see cref="ConnectionInfo.Options"/> ("Max Pool Size"), which is applied after this.</summary>
    private const int DefaultMaxPoolSize = 10;

    /// <summary>
    /// Connection-string keywords <see cref="ConnectionInfo.Options"/> is not allowed to set: identity,
    /// credentials and the target come from the connection record plus the secret store, never from an
    /// option bag that travels in the shared project.json.
    /// <para>
    /// Every SqlClient synonym has to be listed, not just the canonical spelling, because the builder
    /// accepts them all — <c>Server</c>, <c>Addr</c>, <c>Address</c> and <c>Network Address</c> each set
    /// <c>Data Source</c>, and <c>Trusted_Connection</c> sets <c>Integrated Security</c>. Missing one is a
    /// credential-override hole, so the list is deliberately a superset: refusing a name the driver does
    /// not own (<c>user</c>, <c>username</c>, <c>db</c>) costs nothing, since such a key would be ignored
    /// by the next case anyway.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        // Credentials.
        "password", "pwd", "passwd",
        // Identity.
        "user id", "userid", "uid", "user", "username", "user name",
        // Who we authenticate *as*: Authentication switches the connection to an Entra/AAD mode, which
        // changes the identity as surely as a user name does. This app's Entra support goes through
        // CredentialKind.EntraToken, never through the options bag.
        "authentication",
        // Where we connect: every spelling of Data Source, and both of the database.
        "server", "data source", "datasource", "addr", "address", "network address",
        "database", "initial catalog", "db",
        // Whether we authenticate as the OS identity — CredentialKind.Integrated decides that.
        "integrated security", "trusted_connection", "trusted connection",
        // Transport security is ConnectionInfo.Tls now, not an Options entry. Blocked here for the
        // same reason the credentials are: a bag that travels in a shared project.json is the wrong
        // place for a security setting, and two sources of truth is how one of them gets ignored.
        "encrypt", "trustservercertificate", "trust server certificate",
    };

    public SqlServerConnectionFactory(ConnectionInfo info, string? password)
    {
        var csb = new SqlConnectionStringBuilder
        {
            DataSource = DataSourceFor(info),
            InitialCatalog = info.Database,
            ApplicationName = "bearing",
            MaxPoolSize = DefaultMaxPoolSize,
        };

        // Transport security comes from the typed field, resolved (not read raw) so an older project or an
        // import that still carries the mode in its options bag keeps working (#23).
        ApplyTls(csb, TlsPolicy.Resolve(info));

        // No command timeout. SqlClient defaults to 30 seconds, which kills any query that takes longer —
        // the same default #93 removed for Npgsql, and the same reason: a slow query is the user's business,
        // and Esc is how they stop one. Overridable through Options ("Command Timeout"), like MaxPoolSize.
        csb.CommandTimeout = 0;

        if (info.CredentialKind == CredentialKind.Integrated)
        {
            // The OS identity authenticates: no secret is resolved, nothing is read from the secret store,
            // and a user name alongside it would be ignored at best and contradictory at worst.
            csb.IntegratedSecurity = true;
        }
        else
        {
            // SqlClient's string setters reject null (Npgsql's accept it), so an unresolved secret leaves
            // the keyword off entirely — which is what a Prompt connection looks like before the password
            // is in hand. An EntraToken connection reaches here with the token as `password`; SqlClient
            // wants an access token on the connection object rather than in the string, so Entra against
            // Azure SQL is an App-layer follow-up (spec 4d) and not something this factory can fake.
            if (!string.IsNullOrEmpty(info.User)) csb.UserID = info.User;
            if (!string.IsNullOrEmpty(password)) csb.Password = password;
        }

        foreach (var (key, value) in info.Options)
        {
            switch (key.ToLowerInvariant())
            {
                case var k when Reserved.Contains(k):
                    // Identity, credentials and the target come from ConnectionInfo + the secret store. An
                    // Options entry must never override them — a stray "Password" key would beat the
                    // stored secret, and silently at that.
                    break;
                case var k when !csb.ContainsKey(k):
                    // Not a driver keyword: Options doubles as app-level config (the documented `entra.*`
                    // keys live here), and handing those to the driver throws at connect time. Ignore what
                    // the driver doesn't own.
                    break;
                default:
                    // A real SqlClient keyword: apply it, and let a bad *value* still throw — that's a typo
                    // worth surfacing, unlike an unknown key. Note this runs *after* ApplyTls, so a
                    // genuine keyword can still tune the connection (Command Timeout, Max Pool Size,
                    // Connect Timeout) — but not the two TLS keywords, which Reserved blocks above so the
                    // typed field stays the single source of truth for transport security (§1.4).
                    csb[key] = value;
                    break;
            }
        }

        _connectionString = csb.ConnectionString;
    }

    /// <summary>
    /// The <c>Data Source</c> for this connection. Two SqlClient specifics, both easy to get wrong:
    /// <list type="bullet">
    ///   <item>the host/port separator is a <b>comma</b>, not a colon — <c>host:1433</c> is read as a host
    ///     literally named "host:1433", so it fails in DNS rather than on the port;</item>
    ///   <item>a <b>named instance</b> (<c>HOST\INSTANCE</c>) is resolved by the SQL Browser service, which
    ///     hands back whatever dynamic port that instance listens on. Appending a port overrides the lookup
    ///     and aims at the wrong endpoint, so the port is dropped and the instance name stands. The dialog
    ///     still shows a port field; a named instance simply ignores it.</item>
    /// </list>
    /// </summary>
    /// <remarks>Internal, not private, so the two rules above can be asserted as strings. It exposes the
    /// <c>Data Source</c> only — never the connection string, which holds the password and stays inside
    /// this class (§1.1) — so nothing is traded away by making it reachable. Asserting "the constructor
    /// did not throw" left both rules free to regress: swapping the comma for a colon, or dropping the
    /// named-instance branch, kept the whole suite green.</remarks>
    /// <summary>
    /// A <see cref="TlsMode"/> as SqlClient expresses it. Two keywords rather than one, because the mode
    /// says two separate things and SqlClient splits them the same way: <c>Encrypt</c> decides whether the
    /// transport is encrypted, <c>TrustServerCertificate</c> decides whether the server's identity is
    /// checked.
    /// <para>
    /// <b>One honest approximation.</b> SqlClient has no chain-only validation, so
    /// <see cref="TlsMode.VerifyCa"/> and <see cref="TlsMode.VerifyFull"/> both come out as full validation
    /// — chain <em>and</em> host name. VerifyCa therefore gets a stricter connection than it asked for,
    /// which is the safe direction to be wrong in: it can refuse a certificate Postgres would have taken,
    /// and it can never accept one Postgres would have refused.
    /// </para>
    /// </summary>
    private static void ApplyTls(SqlConnectionStringBuilder csb, TlsMode mode)
    {
        switch (mode)
        {
            case TlsMode.Disable:
            case TlsMode.Prefer:
                // Optional is SqlClient's "encrypt only if the server insists" — the closest it has to
                // either of these, and what its own pre-4.0 default was.
                csb.Encrypt = SqlConnectionEncryptOption.Optional;
                csb.TrustServerCertificate = true;
                break;
            case TlsMode.Require:
                // Encrypt, and accept any certificate: stops an eavesdropper, stops nobody impersonating
                // the server. That is exactly what Require means, and why TlsPolicy warns about it.
                csb.Encrypt = SqlConnectionEncryptOption.Mandatory;
                csb.TrustServerCertificate = true;
                break;
            default:
                csb.Encrypt = SqlConnectionEncryptOption.Mandatory;
                csb.TrustServerCertificate = false;
                break;
        }
    }

    internal static string DataSourceFor(ConnectionInfo info)
    {
        var host = info.Host.Trim();
        if (host.Contains('\\')) return host;
        return info.Port > 0 ? $"{host},{info.Port}" : host;
    }

    /// <summary>Opens a pooled connection. Internal: the driver type stops at this project's edge (§2.1).</summary>
    internal async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            // A failed open still owns a SqlConnection nobody else has a reference to; dispose it rather
            // than leaving it for the finalizer.
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return conn.State == System.Data.ConnectionState.Open;
    }

    /// <summary>
    /// Clears this factory's pool, so evicting a session actually releases sockets instead of leaving
    /// SqlClient holding them for their idle lifetime — the lifecycle model in §5.3 assumes disposing the
    /// factory tears the pool down, the way <c>NpgsqlDataSource.DisposeAsync</c> does.
    /// <para>
    /// The pool is keyed by connection string, so this releases exactly the connections built from these
    /// settings. Two factories built from identical settings share that pool and would clear each other's
    /// idle connections — but that is the same (connection, database) pool by definition, and the session
    /// manager keeps one factory per key (§9.4a), so it is not a case that arises. Connections still in use
    /// are not killed: <c>ClearPool</c> marks them stale so they are discarded on close rather than reused,
    /// which is what a lease-holding read needs.
    /// </para>
    /// </summary>
    public ValueTask DisposeAsync()
    {
        using var conn = new SqlConnection(_connectionString);
        SqlConnection.ClearPool(conn);
        return ValueTask.CompletedTask;
    }
}
