using Bearing.Core.Data;

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

    public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password)
        => new NpgsqlConnectionFactory(info, password);

    public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory)
        => new PostgresMetadataReader((NpgsqlConnectionFactory)factory);

    public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory)
        => new PostgresQueryExecutor((NpgsqlConnectionFactory)factory);
}
