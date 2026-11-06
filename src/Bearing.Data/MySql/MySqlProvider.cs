using Bearing.Core.Data;
using MySqlConnector;

namespace Bearing.Data.MySql;

/// <summary>
/// The MySQL and MariaDB engine provider — one provider for both, over MySqlConnector, because the two
/// share the wire protocol, the catalog (<c>information_schema</c>) and nearly all of the SQL this app
/// generates. Where they differ, the difference is settled from the server's own version string at the
/// point it matters (<see cref="MySqlServerFlavor"/>), not by asking the user which one they have. The SQL
/// text belongs to <c>Bearing.Sql</c>'s <c>MySqlDialect</c>, paired with this provider by <see cref="Id"/>.
/// </summary>
public sealed class MySqlProvider : IDbProvider
{
    public const string ProviderId = "mysql";

    public string Id => ProviderId;
    public string DisplayName => "MySQL / MariaDB";

    public IReadOnlyList<ConnectionField> ConnectionFields { get; } = new[]
    {
        new ConnectionField("Host", "Host", ConnectionFieldKind.Text, Required: true, Default: "localhost"),
        new ConnectionField("Port", "Port", ConnectionFieldKind.Number, Required: true, Default: "3306"),
        new ConnectionField("Database", "Database", ConnectionFieldKind.Text, Required: true),
        new ConnectionField("User", "User", ConnectionFieldKind.Text, Required: true),
        new ConnectionField("Password", "Password", ConnectionFieldKind.Password, Required: false),
    };

    /// <summary>
    /// True: a read-only connection sets <c>SESSION TRANSACTION READ ONLY</c> on every pooled connection
    /// (<see cref="MySqlConnectionFactory"/>), so the server itself refuses a write to a table — error 1792 —
    /// including one inside a procedure or built as dynamic SQL, which the lexer cannot see. Like Postgres'
    /// it can be lifted with a <c>SET</c> by the user, which the dialog already says.
    /// </summary>
    public bool EnforcesReadOnlyOnServer => true;

    /// <summary>
    /// False, deliberately, though both engines have a session <c>time_zone</c>. A named zone
    /// (<c>Europe/Zagreb</c>) only works on a server whose time-zone tables are loaded, and many are not —
    /// a stock Windows install, several managed offerings — so honouring the setting would make the default
    /// connection fail to open on those servers. A fixed offset would open, and then render every
    /// <c>TIMESTAMP</c> on the other side of a DST change an hour wrong. Until there is an answer that is
    /// right on both kinds of server, the dialog does not offer a setting it cannot keep, and timestamps
    /// render in the server's zone.
    /// </summary>
    public bool SupportsSessionTimeZone => false;

    public bool SupportsIntegratedAuth => false;

    /// <summary>True: Azure Database for MySQL takes the Entra access token as the password (sent with the
    /// cleartext plugin, which MySqlConnector only uses over TLS) and the Entra user as the user — the same
    /// arrangement as Azure Database for PostgreSQL, so it needs no path of its own.</summary>
    public bool SupportsEntraToken => true;

    public DbErrorKind Classify(QueryError error)
        => int.TryParse(error.SqlState, out var number) ? FromNumber(number) : DbErrorKind.Unknown;

    public DbErrorKind ClassifyException(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is MySqlException my)
            {
                var kind = FromNumber((int)my.ErrorCode);
                if (kind != DbErrorKind.Unknown)
                {
                    return kind;
                }
            }
            if (e.Message.Contains("Access denied for user", StringComparison.OrdinalIgnoreCase))
            {
                return DbErrorKind.Authentication;
            }
        }
        return DbErrorKind.Unknown;
    }

    /// <summary>MySQL error numbers on the neutral scale — numbers, as the executor records them.</summary>
    private static DbErrorKind FromNumber(int number) => number switch
    {
        // "Query execution was interrupted" — KILL QUERY, which is how MySqlConnector cancels on Esc.
        1317 => DbErrorKind.Canceled,

        // We were not let in and re-acquiring the credential may fix it: access denied (1045), denied to
        // this database (1044), the socket-auth refusal (1698), an expired password (1862) and one that must
        // be reset before anything else runs (1820).
        1044 or 1045 or 1698 or 1820 or 1862 => DbErrorKind.Authentication,

        // The statement's shape: a parse error (1064), a duplicate column name — what a join dies of
        // inside the count wrapper — (1060), and a statement the server will not run as written (1149).
        1060 or 1064 or 1149 => DbErrorKind.SyntaxOrShape,

        _ => DbErrorKind.Unknown,
    };

    /// <summary>True: both engines expose <c>information_schema.processlist</c> and <c>KILL</c>.</summary>
    public bool SupportsServerActivity => true;

    public IServerActivity CreateServerActivity(IDbConnectionFactory factory)
        => new MySqlServerActivity((MySqlConnectionFactory)factory);

    public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password)
        => new MySqlConnectionFactory(info, password);

    public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory)
        => new MySqlMetadataReader((MySqlConnectionFactory)factory);

    public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory)
        => new MySqlQueryExecutor((MySqlConnectionFactory)factory);

    public ITransactionScope CreateTransactionScope(IDbConnectionFactory factory)
        => new MySqlTransactionScope((MySqlConnectionFactory)factory);
}
