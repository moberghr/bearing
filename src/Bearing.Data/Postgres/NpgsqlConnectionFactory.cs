using Npgsql;
using Bearing.Core.Data;

namespace Bearing.Data.Postgres;

/// <summary>
/// Owns a pooled <see cref="NpgsqlDataSource"/> for one connection's settings. Hides Npgsql from
/// the rest of the app behind <see cref="IDbConnectionFactory"/>.
/// </summary>
public sealed class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlConnectionFactory(ConnectionInfo info, string? password)
    {
        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = info.Host,
            Port = info.Port,
            Database = info.Database,
            Username = info.User,
            Password = password,
            ApplicationName = "bearing",
        };

        foreach (var (key, value) in info.Options)
        {
            switch (key.ToLowerInvariant())
            {
                case "sslmode" when Enum.TryParse<SslMode>(value, ignoreCase: true, out var mode):
                    csb.SslMode = mode;
                    break;
                case "search_path":
                    csb.SearchPath = value;
                    break;
                default:
                    csb[key] = value;
                    break;
            }
        }

        _dataSource = NpgsqlDataSource.Create(csb);
    }

    internal NpgsqlDataSource DataSource => _dataSource;

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return conn.State == System.Data.ConnectionState.Open;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
