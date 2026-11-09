using MySqlConnector;

namespace Bearing.Data.MySql;

/// <summary>Where <see cref="MySqlQueryExecutor"/> gets the connection it runs on — pooled, or pinned to a
/// held manual-commit transaction (#131). The same seam the other two engines have.</summary>
internal interface IMySqlConnectionSource
{
    Task<MySqlRent> RentAsync(CancellationToken ct);
}

/// <summary>One statement's use of a connection, the transaction it must run in (null when none), and
/// whether the connection is this statement's to return.</summary>
internal sealed class MySqlRent : IAsyncDisposable
{
    private readonly bool _owned;

    internal MySqlRent(MySqlConnection connection, MySqlTransaction? transaction, bool owned)
    {
        Connection = connection;
        Transaction = transaction;
        _owned = owned;
    }

    public MySqlConnection Connection { get; }

    public MySqlTransaction? Transaction { get; }

    /// <summary>Which engine and version is on the other end — read from the handshake, so free.</summary>
    public MySqlServerFlavor Flavor => MySqlServerFlavor.Parse(Connection.ServerVersion);

    public ValueTask DisposeAsync() => _owned ? Connection.DisposeAsync() : default;
}

internal sealed class PooledMySqlConnections : IMySqlConnectionSource
{
    private readonly MySqlConnectionFactory _factory;

    internal PooledMySqlConnections(MySqlConnectionFactory factory) => _factory = factory;

    public async Task<MySqlRent> RentAsync(CancellationToken ct)
        => new(await _factory.OpenConnectionAsync(ct).ConfigureAwait(false), null, owned: true);
}

internal sealed class PinnedMySqlConnection : IMySqlConnectionSource
{
    private readonly Func<(MySqlConnection Connection, MySqlTransaction Transaction)> _held;

    internal PinnedMySqlConnection(Func<(MySqlConnection, MySqlTransaction)> held) => _held = held;

    public Task<MySqlRent> RentAsync(CancellationToken ct)
    {
        var (conn, tx) = _held();
        return Task.FromResult(new MySqlRent(conn, tx, owned: false));
    }
}
