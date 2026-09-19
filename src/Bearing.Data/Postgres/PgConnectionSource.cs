using Npgsql;

namespace Bearing.Data.Postgres;

/// <summary>
/// Where <see cref="PostgresQueryExecutor"/> gets the connection it runs a statement on. Two implementations,
/// and the executor is written once against both (#131).
/// <para>
/// <see cref="Pooled"/> is what every call did inline until manual-commit mode existed: open one from the
/// data source, hand it back when the statement is done. <see cref="Pinned"/> hands out one connection that
/// a <see cref="PostgresTransactionScope"/> is holding open, with its transaction, and disposes nothing —
/// a transaction lives on one physical connection, so an executor that opened its own could never be in it.
/// </para>
/// </summary>
internal interface IPgConnectionSource
{
    Task<PgRent> RentAsync(CancellationToken ct);
}

/// <summary>One statement's use of a connection: the connection, the transaction it must run in (null when
/// there is none), and whether the connection is this statement's to return.</summary>
internal sealed class PgRent : IAsyncDisposable
{
    private readonly bool _owned;

    internal PgRent(NpgsqlConnection connection, NpgsqlTransaction? transaction, bool owned)
    {
        Connection = connection;
        Transaction = transaction;
        _owned = owned;
    }

    public NpgsqlConnection Connection { get; }

    /// <summary>The held transaction, or null when the caller is free to open its own.</summary>
    public NpgsqlTransaction? Transaction { get; }

    public ValueTask DisposeAsync() => _owned ? Connection.DisposeAsync() : default;
}

internal sealed class PooledPgConnections : IPgConnectionSource
{
    private readonly NpgsqlConnectionFactory _factory;

    internal PooledPgConnections(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<PgRent> RentAsync(CancellationToken ct)
        => new(await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false), null, owned: true);
}

internal sealed class PinnedPgConnection : IPgConnectionSource
{
    private readonly Func<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> _held;

    internal PinnedPgConnection(Func<(NpgsqlConnection, NpgsqlTransaction)> held) => _held = held;

    public Task<PgRent> RentAsync(CancellationToken ct)
    {
        var (conn, tx) = _held();
        return Task.FromResult(new PgRent(conn, tx, owned: false));
    }
}
