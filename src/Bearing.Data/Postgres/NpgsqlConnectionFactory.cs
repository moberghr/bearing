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

    /// <summary>Connection-string keywords <see cref="ConnectionInfo.Options"/> is not allowed to set:
    /// identity and credentials come from the connection record plus the secret store, never from an option
    /// bag that travels in the shared project.json.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "pwd",
        "host", "server", "port", "database", "db",
        "username", "user", "user id", "userid", "uid", "user name",
    };

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
                case var k when Reserved.Contains(k):
                    // Identity and credentials come from ConnectionInfo + the secret store. An Options entry
                    // must never override them — a stray "Password" key would beat the stored secret, and
                    // silently at that.
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

        _dataSource = NpgsqlDataSource.Create(csb);
    }

    internal NpgsqlDataSource DataSource => _dataSource;

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return conn.State == System.Data.ConnectionState.Open;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
