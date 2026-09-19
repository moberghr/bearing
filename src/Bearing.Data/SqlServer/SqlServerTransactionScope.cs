using Microsoft.Data.SqlClient;
using Bearing.Core.Data;

namespace Bearing.Data.SqlServer;

/// <summary>
/// One SQL Server transaction held open across calls, on one connection checked out of the pool for as long
/// as the user leaves it open (#131) — the sibling of <c>PostgresTransactionScope</c>, and deliberately the
/// same shape.
/// <para>
/// Two things differ from Postgres and neither changes this class. The abort rule
/// (<see cref="TransactionState.Aborted"/>) is conservative here rather than exact, which is stated where it
/// is applied. And SqlClient's pooling is the driver's own, keyed by connection string rather than owned by
/// a data-source object — so a held connection is a slot out of that pool just the same, and
/// <c>CommitPolicy.MaxOpenPerPool</c> bounds it just the same.
/// </para>
/// </summary>
public sealed class SqlServerTransactionScope : ITransactionScope
{
    private readonly SqlServerConnectionFactory _factory;
    private SqlConnection? _connection;
    private SqlTransaction? _transaction;

    public SqlServerTransactionScope(SqlServerConnectionFactory factory)
    {
        _factory = factory;
        LastActivityUtc = DateTime.UtcNow;
        Executor = new TrackedExecutor(
            new SqlServerQueryExecutor(new PinnedSqlConnection(Held)),
            onStatement: () => { LastActivityUtc = DateTime.UtcNow; StatementCount++; },
            onTouch: () => LastActivityUtc = DateTime.UtcNow,
            onFailure: () => { if (State == TransactionState.Active) State = TransactionState.Aborted; });
    }

    public TransactionState State { get; private set; } = TransactionState.None;
    public int StatementCount { get; private set; }
    public DateTime LastActivityUtc { get; private set; }
    public IQueryExecutor Executor { get; }

    private (SqlConnection, SqlTransaction) Held()
        => _connection is { } c && _transaction is { } t
            ? (c, t)
            : throw new InvalidOperationException("No transaction is open on this scope.");

    public async Task BeginAsync(CancellationToken ct)
    {
        if (State != TransactionState.None) throw new InvalidOperationException("A transaction is already open.");
        _connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            _transaction = (SqlTransaction)await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Never leave the pool a connection short because the BEGIN failed.
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
            throw;
        }
        State = TransactionState.Active;
        StatementCount = 0;
        LastActivityUtc = DateTime.UtcNow;
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        if (State != TransactionState.Active)
            throw new InvalidOperationException($"Cannot commit a transaction that is {State}.");
        try { await _transaction!.CommitAsync(ct).ConfigureAwait(false); }
        finally { await ReleaseAsync().ConfigureAwait(false); }
    }

    public async Task RollbackAsync(CancellationToken ct)
    {
        if (State == TransactionState.None) return; // already over; rolling back nothing is not an error
        try { await _transaction!.RollbackAsync(ct).ConfigureAwait(false); }
        finally { await ReleaseAsync().ConfigureAwait(false); }
    }

    /// <summary>Hand the connection back to the pool and go back to holding nothing — whichever verb ended
    /// the transaction, and whether or not it succeeded. A scope that kept the connection after a failed
    /// commit would leak a pool slot for the life of the app.</summary>
    private async ValueTask ReleaseAsync()
    {
        State = TransactionState.None;
        if (_transaction is { } tx) { _transaction = null; await tx.DisposeAsync().ConfigureAwait(false); }
        if (_connection is { } conn) { _connection = null; await conn.DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>Disposing an open transaction rolls it back — the server would anyway once the connection
    /// closed, and doing it here means it happens while there is still somewhere to report a failure.</summary>
    public async ValueTask DisposeAsync()
    {
        if (State != TransactionState.None)
        {
            try { await _transaction!.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* the connection may already be gone; closing it below is the backstop */ }
        }
        await ReleaseAsync().ConfigureAwait(false);
    }
}
