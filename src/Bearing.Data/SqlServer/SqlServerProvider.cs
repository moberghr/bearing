using Bearing.Core.Data;
using Microsoft.Data.SqlClient;

namespace Bearing.Data.SqlServer;

/// <summary>The Microsoft SQL Server engine provider: connections, metadata, execution, error
/// classification, and which credential kinds this engine can authenticate with. The SQL text belongs to
/// <c>Bearing.Sql</c>'s <c>SqlServerDialect</c>, which shares this provider's <see cref="Id"/> so the App
/// layer can pair the two.</summary>
public sealed class SqlServerProvider : IDbProvider
{
    public const string ProviderId = "sqlserver";

    public string Id => ProviderId;
    public string DisplayName => "Microsoft SQL Server";

    /// <summary>
    /// The dialog's fields. <c>Encrypt</c> and <c>TrustServerCertificate</c> are here rather than assumed:
    /// SqlClient 4.0+ defaults <c>Encrypt=true</c>, which fails against a dev server holding a self-signed
    /// certificate, and the fix must be the user's explicit choice — silently disabling encryption or
    /// trusting any certificate on their behalf is exactly what §1.4 forbids. Their defaults here restate
    /// the driver's own, so leaving them alone changes nothing.
    /// </summary>
    public IReadOnlyList<ConnectionField> ConnectionFields { get; } = new[]
    {
        new ConnectionField("Host", "Host", ConnectionFieldKind.Text, Required: true, Default: "localhost"),
        new ConnectionField("Port", "Port", ConnectionFieldKind.Number, Required: true, Default: "1433"),
        new ConnectionField("Database", "Database", ConnectionFieldKind.Text, Required: true),
        new ConnectionField("User", "User", ConnectionFieldKind.Text, Required: true),
        new ConnectionField("Password", "Password", ConnectionFieldKind.Password, Required: false),
    };

    /// <summary>True: Windows / integrated authentication is what most SQL Server installations are set up
    /// for, and SqlClient supports it directly (<see cref="CredentialKind.Integrated"/> →
    /// <c>Integrated Security=true</c>, no secret resolved and no prompt).</summary>
    public bool SupportsIntegratedAuth => true;

    /// <summary>True. SqlClient accepts an access token only via <c>SqlConnection.AccessToken</c>, never as
    /// a password keyword — so unlike Postgres this needed a path of its own, and
    /// <see cref="SqlServerConnectionFactory.CreateConnection"/> is it: the token is held apart from the
    /// connection string and attached to every connection the factory opens (§1.1). The audience is Azure
    /// SQL's, which the App layer supplies (<c>ProviderTraits.SqlServer.EntraResource</c>) — a token minted
    /// for Azure Database for PostgreSQL is refused by Azure SQL and vice versa.</summary>
    public bool SupportsEntraToken => true;

    public DbErrorKind Classify(QueryError error)
        => int.TryParse(error.SqlState, out var number) ? FromNumber(number) : DbErrorKind.Unknown;

    public DbErrorKind ClassifyException(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql)
            {
                var kind = FromNumber(sql.Number);
                if (kind != DbErrorKind.Unknown) return kind;
            }

            // A connect-time failure doesn't always arrive typed: SqlClient wraps some transport and
            // negotiation failures, and a pooled open can surface them through an AggregateException. The
            // message chain is scanned for the login-failure number and text as well, for the same reason
            // the Postgres provider scans for 28P01 — without it the "offer to re-enter the password" path
            // is lost on exactly the failures that need it. Deliberately narrow: only the one thing that
            // has a recovery action attached.
            if (e.Message.Contains("18456", StringComparison.Ordinal)
                || e.Message.Contains("Login failed", StringComparison.OrdinalIgnoreCase))
                return DbErrorKind.Authentication;
        }
        return DbErrorKind.Unknown;
    }

    /// <summary>
    /// SQL Server error numbers on the neutral scale. Numbers, not SQLSTATEs: SQL Server has none of its
    /// own, so <see cref="QueryError.SqlState"/> carries the number as text (see
    /// <c>SqlServerQueryExecutor.ErrorFrom</c>).
    /// </summary>
    private static DbErrorKind FromNumber(int number) => number switch
    {
        // "Operation cancelled by user." — how SqlClient reports a command it cancelled on our behalf,
        // which is what Run/Esc does. Mapped so a cancel is not toasted as a query failure, the same job
        // Postgres' 57014 does. The trade-off, stated: 0 is also the number a few client-side faults carry,
        // so one of those would read as a cancel. A timeout (-2) is *not* included — it is a real failure
        // and the user did not ask for it.
        0 => DbErrorKind.Canceled,

        // We weren't let in, and re-acquiring the credential may fix it: 18456 login failed, 18452 login
        // from an untrusted domain (a Windows-auth mismatch), 4060 cannot open the requested database —
        // which SQL Server itself reports as a login failure — and the 184xx password/lockout family.
        4060 or 18452 or 18456 or 18470 or 18486 or 18487 or 18488 => DbErrorKind.Authentication,

        // The statement's shape was rejected. 102/105/156 are the parse errors; 1033, 8155 and 8156 are
        // what a perfectly good statement fails with once it sits inside the count wrapper (ORDER BY in a
        // derived table, an unnamed column, a duplicated column name). The executor swallows a narrower
        // set of these to hide a total — see SqlServerQueryExecutor.IsUncountableShape — but for reporting,
        // "the shape was wrong" is what all of them mean.
        102 or 105 or 156 or 1033 or 8155 or 8156 => DbErrorKind.SyntaxOrShape,

        _ => DbErrorKind.Unknown,
    };

    public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password)
        => new SqlServerConnectionFactory(info, password);

    public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory)
        => new SqlServerMetadataReader((SqlServerConnectionFactory)factory);

    public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory)
        => new SqlServerQueryExecutor((SqlServerConnectionFactory)factory);
}
