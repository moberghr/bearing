using Microsoft.Data.SqlClient;

namespace Bearing.Data.SqlServer;

/// <summary>
/// Where <see cref="SqlServerQueryExecutor"/> gets the connection it runs a statement on — the sibling of
/// <c>IPgConnectionSource</c>, and deliberately the same shape (#131).
/// </summary>
internal interface ISqlConnectionSource
{
    Task<SqlRent> RentAsync(CancellationToken ct);
}

/// <summary>One statement's use of a connection: the connection, the transaction it must run in (null when
/// there is none), and whether the connection is this statement's to return.</summary>
internal sealed class SqlRent : IAsyncDisposable
{
    private readonly bool _owned;

    internal SqlRent(SqlConnection connection, SqlTransaction? transaction, bool owned)
    {
        Connection = connection;
        Transaction = transaction;
        _owned = owned;
    }

    public SqlConnection Connection { get; }

    /// <summary>The held transaction, or null when the caller is free to open its own.</summary>
    public SqlTransaction? Transaction { get; }

    public ValueTask DisposeAsync() => _owned ? Connection.DisposeAsync() : default;
}

internal sealed class PooledSqlConnections : ISqlConnectionSource
{
    private readonly SqlServerConnectionFactory _factory;

    internal PooledSqlConnections(SqlServerConnectionFactory factory) => _factory = factory;

    public async Task<SqlRent> RentAsync(CancellationToken ct)
        => new(await _factory.OpenConnectionAsync(ct).ConfigureAwait(false), null, owned: true);
}

internal sealed class PinnedSqlConnection : ISqlConnectionSource
{
    private readonly Func<(SqlConnection Connection, SqlTransaction Transaction)> _held;

    internal PinnedSqlConnection(Func<(SqlConnection, SqlTransaction)> held) => _held = held;

    public Task<SqlRent> RentAsync(CancellationToken ct)
    {
        var (conn, tx) = _held();
        return Task.FromResult(new SqlRent(conn, tx, owned: false));
    }
}
