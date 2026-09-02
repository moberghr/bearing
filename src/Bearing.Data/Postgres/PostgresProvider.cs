using Bearing.Core.Data;
using Npgsql;

namespace Bearing.Data.Postgres;

/// <summary>The PostgreSQL engine provider — the only implementation in v1.</summary>
public sealed class PostgresProvider : IDbProvider
{
    public const string ProviderId = "postgres";

    public string Id => ProviderId;
    public string DisplayName => "PostgreSQL";

    public IReadOnlyList<ConnectionField> ConnectionFields { get; } = new[]
    {
        new ConnectionField("Host", "Host", ConnectionFieldKind.Text, Required: true, Default: "localhost"),
        new ConnectionField("Port", "Port", ConnectionFieldKind.Number, Required: true, Default: "5432"),
        new ConnectionField("Database", "Database", ConnectionFieldKind.Text, Required: true),
        new ConnectionField("User", "User", ConnectionFieldKind.Text, Required: true),
        new ConnectionField("Password", "Password", ConnectionFieldKind.Password, Required: false),
        // No sslmode field: transport security is ConnectionInfo.Tls now (#23), and a generic bag-backed
        // control for it would be a control that does nothing — Build reserves the key.
    };

    /// <summary>No integrated auth: Npgsql authenticates with a password (or a token used as one), and
    /// SSPI/GSS single sign-on is not a path this app offers. The dialog therefore never shows it here.</summary>
    public bool SupportsIntegratedAuth => false;

    /// <summary>True: Npgsql takes the Entra access token as the password, which is exactly how Azure
    /// Database for PostgreSQL expects it. This is the path EntraTokenProvider was built for.</summary>
    public bool SupportsEntraToken => true;

    public DbErrorKind Classify(QueryError error) => FromSqlState(error.SqlState);

    public DbErrorKind ClassifyException(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e is PostgresException pg)
            {
                var kind = FromSqlState(pg.SqlState);
                if (kind != DbErrorKind.Unknown) return kind;
            }

            // A connect-time failure doesn't always arrive typed — Npgsql wraps socket, TLS and SASL
            // failures, and a pooled open can surface them through an AggregateException — so the message
            // chain is scanned for the authentication SQLSTATEs and text as well. This is the heuristic the
            // App layer carried before classification became the provider's job; it is kept because
            // dropping it would lose the "offer to re-enter the password" path on exactly those failures.
            var m = e.Message;
            if (m.Contains("28P01", StringComparison.OrdinalIgnoreCase)
                || m.Contains("28000", StringComparison.OrdinalIgnoreCase)
                || m.Contains("authentication failed", StringComparison.OrdinalIgnoreCase))
                return DbErrorKind.Authentication;
        }
        return DbErrorKind.Unknown;
    }

    /// <summary>The SQLSTATE rules the App layer used to apply by hand, unchanged in meaning.</summary>
    private static DbErrorKind FromSqlState(string? sqlState) => sqlState switch
    {
        null => DbErrorKind.Unknown,
        PostgresErrorCodes.QueryCanceled => DbErrorKind.Canceled,
        // A multi-statement batch or a non-SELECT dies on syntax_error when wrapped in a count; a
        // data-modifying CTE, which must stay top-level, on feature_not_supported.
        PostgresErrorCodes.SyntaxError or PostgresErrorCodes.FeatureNotSupported => DbErrorKind.SyntaxOrShape,
        // Class 28 (invalid authorization specification) as a prefix, not a fixed code: 28000 and 28P01
        // are the ones seen in practice, but the whole class means the server didn't let us in.
        _ when sqlState.StartsWith("28", StringComparison.Ordinal) => DbErrorKind.Authentication,
        _ => DbErrorKind.Unknown,
    };

    public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password)
        => new NpgsqlConnectionFactory(info, password);

    public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory)
        => new PostgresMetadataReader((NpgsqlConnectionFactory)factory);

    public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory)
        => new PostgresQueryExecutor((NpgsqlConnectionFactory)factory);
}
