using Npgsql;
using Bearing.Core.Data;

namespace Bearing.Data.Postgres;

/// <summary>
/// Owns a pooled <see cref="NpgsqlDataSource"/> for one connection's settings. Hides Npgsql from
/// the rest of the app behind <see cref="IDbConnectionFactory"/>. The settings themselves are assembled by
/// <see cref="PostgresConnectionString"/>, which is pure and therefore testable without a server.
/// </summary>
public sealed class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlConnectionFactory(ConnectionInfo info, string? password)
        => _dataSource = NpgsqlDataSource.Create(PostgresConnectionString.Build(info, password));

    internal NpgsqlDataSource DataSource => _dataSource;

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return conn.State == System.Data.ConnectionState.Open;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
