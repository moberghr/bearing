using Bearing.Core.Data;
using MySqlConnector;

namespace Bearing.Data.MySql;

/// <summary>
/// Builds and hands out pooled <see cref="MySqlConnection"/>s for one connection's settings — MySQL and
/// MariaDB alike, since MySqlConnector speaks the protocol both share. The sibling of
/// <see cref="Postgres.NpgsqlConnectionFactory"/>, holding a <see cref="MySqlDataSource"/> the way that one
/// holds an <c>NpgsqlDataSource</c>, so disposing the factory tears the pool down (§5.3). The connection
/// string holds the password and never leaves this object (§1.1).
/// <para>
/// <b>Read-only rides a connection-open callback, not a <c>SET</c> sent once.</b> MySQL has no startup
/// packet to carry session settings the way Postgres does (§1.9), and a <c>SET</c> on one connection
/// reaches one pooled socket — the exact failure <c>SessionPolicyTests</c> pins for Postgres. The callback
/// runs on every physical connection the pool opens <em>and every time it resets one</em> (MySqlConnector
/// resets a connection as it leaves the pool, which reverts session variables), so a user's
/// <c>SET SESSION TRANSACTION READ WRITE</c> lasts until that connection is returned and not a statement
/// longer. Still not a privilege boundary — the user can lift it within their own session (§1.9).
/// </para>
/// </summary>
public sealed class MySqlConnectionFactory : IDbConnectionFactory
{
    /// <summary>Pooled connections per (connection, database) — the same cap as the other engines (§5.3).</summary>
    private const uint DefaultMaxPoolSize = 10;

    /// <summary>
    /// Connection-string keywords <see cref="ConnectionInfo.Options"/> may not set — identity, credentials,
    /// the target and transport security come from the connection record and the secret store, never from a
    /// bag that travels in the shared project.json. Every MySqlConnector synonym is listed, as the SQL Server
    /// factory lists SqlClient's, because the builder accepts them all.
    /// <para>
    /// Two are here for what they would let the <em>server</em> do: <c>AllowLoadLocalInfile</c> lets a server
    /// ask the client for any file the user can read, and <c>AllowPublicKeyRetrieval</c> lets a server that
    /// is not who it claims to be fetch the password over an unencrypted connection. Neither is a setting a
    /// shared project file should be able to turn on behind the user's back.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        // Credentials and identity.
        "password", "pwd", "user id", "userid", "uid", "user", "username", "user name",
        "default authentication plugin", "defaultauthenticationplugin",
        // Where we connect.
        "server", "host", "data source", "datasource", "address", "addr", "network address",
        "port", "database", "initial catalog", "pipe", "pipename", "protocol", "connection protocol",
        "connectionprotocol", "transport", "load balance", "loadbalance",
        // Transport security is ConnectionInfo.Tls.
        "sslmode", "ssl mode", "ssl-mode", "ssl ca", "sslca", "ssl-ca", "cacertificatefile",
        "certificate file", "certificatefile", "certificate password", "certificatepassword",
        "ssl cert", "sslcert", "ssl-cert", "ssl key", "sslkey", "ssl-key",
        "certificate store location", "certificatestorelocation", "certificate thumbprint",
        "certificatethumbprint", "tls version", "tlsversion", "tls-version", "tls cipher suites",
        "tlsciphersuites", "server rsa public key file", "serverrsapublickeyfile", "server spn", "serverspn",
        "allow public key retrieval", "allowpublickeyretrieval",
        // What the server may ask of us.
        "allow load local infile", "allowloadlocalinfile", "allow local infile",
        // Set by this factory for reasons the comments below give.
        "allow user variables", "allowuservariables",
    };

    private readonly MySqlDataSource _dataSource;

    public MySqlConnectionFactory(ConnectionInfo info, string? password)
    {
        var csb = new MySqlConnectionStringBuilder
        {
            Server = info.Host.Trim(),
            Port = info.Port > 0 ? (uint)info.Port : 3306,
            Database = info.Database,
            ApplicationName = "bearing",
            MaximumPoolSize = DefaultMaxPoolSize,
            // A user script may use @variables; without this MySqlConnector reads every one as an unbound
            // parameter and refuses the statement. Generated DML binds @p0… by name, which still works.
            AllowUserVariables = true,
            // '0000-00-00' is a legal stored value under a permissive sql_mode and throws on read by default,
            // which would make one bad row unreadable along with every row around it.
            ConvertZeroDateTime = true,
            // No client-side timeout by default — a slow query is the user's business and Esc stops it (#93).
            // A connection that asks for one gets it below.
            DefaultCommandTimeout = 0,
        };

        csb.SslMode = TlsPolicy.Resolve(info) switch
        {
            TlsMode.Disable => MySqlSslMode.None,
            TlsMode.Prefer => MySqlSslMode.Preferred,
            // Encrypts and accepts any certificate — exactly what Require means (§1.4).
            TlsMode.Require => MySqlSslMode.Required,
            TlsMode.VerifyCa => MySqlSslMode.VerifyCA,
            _ => MySqlSslMode.VerifyFull,
        };

        // The Entra token (Azure Database for MySQL) is the password, as on Postgres: MySqlConnector sends it
        // with the cleartext plugin, which it only does over TLS.
        if (!string.IsNullOrEmpty(info.User))
        {
            csb.UserID = info.User;
        }
        if (!string.IsNullOrEmpty(password))
        {
            csb.Password = password;
        }

        foreach (var (key, value) in info.Options)
        {
            if (Reserved.Contains(key) || !csb.ContainsKey(key))
            {
                // Reserved: see above. Unknown: Options also carries app-level keys (`entra.*`), which the
                // driver would throw on.
                continue;
            }
            csb[key] = value;
        }

        // §1.9's statement timeout, after the bag so a shared project file cannot quietly lift it.
        // MySqlConnector enforces it by issuing KILL QUERY on the server when it expires, so unlike
        // SqlClient's the statement is actually stopped there, on both engines. (The server-side variables
        // differ — max_statement_time on MariaDB, max_execution_time on MySQL and for SELECT only — which is
        // why the one mechanism both share is used instead.)
        var timeout = SessionPolicy.TimeoutSeconds(info);
        if (timeout > SessionPolicy.NoTimeout)
        {
            csb.DefaultCommandTimeout = (uint)timeout;
        }

        var readOnly = SessionPolicy.IsReadOnly(info);
        var builder = new MySqlDataSourceBuilder(csb.ConnectionString);
        if (readOnly)
        {
            builder.UseConnectionOpenedCallback(EnforceReadOnlyAsync);
        }
        _dataSource = builder.Build();
    }

    /// <summary>
    /// Every transaction on the session — and in autocommit mode every statement is one — starts read-only,
    /// so a write to a table fails on the server with error 1792 whatever Bearing's lexer made of it. Run for
    /// a new connection and for a reset one alike: the reset is what reverts the variable.
    /// </summary>
    private static async ValueTask EnforceReadOnlyAsync(MySqlConnectionOpenedContext context, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("set session transaction read only", context.Connection);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Opens a pooled connection. Internal: the driver type stops at this project's edge (§2.1).</summary>
    internal async Task<MySqlConnection> OpenConnectionAsync(CancellationToken ct)
        => await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return conn.State == System.Data.ConnectionState.Open;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
