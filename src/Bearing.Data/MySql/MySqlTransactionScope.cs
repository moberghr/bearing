using Bearing.Core.Data;
using MySqlConnector;

namespace Bearing.Data.MySql;

/// <summary>
/// One MySQL / MariaDB transaction held open across calls, on one pooled connection checked out for as long
/// as the user leaves it open (#131) — the same shape as the Postgres and SQL Server scopes.
/// <para>
/// The abort rule is conservative here, as on SQL Server: an ordinary error leaves a MySQL transaction
/// committable, but a deadlock (1213) rolls the whole thing back on the server and a lock-wait timeout may
/// roll back the statement — and the scope cannot tell those apart from the outside without re-deciding
/// what it trusts. So any failure marks it <see cref="TransactionState.Aborted"/> and Commit is refused;
/// the user is told and rolls back. Statements that would end the transaction implicitly (DDL,
/// <c>LOCK TABLES</c>, …) never reach this connection on a manual-commit tab:
/// <c>MySqlDialect.TransactionControl</c> names them and <c>WriteRefusal</c> refuses them.
/// </para>
/// </summary>
public sealed class MySqlTransactionScope : ITransactionScope
{
    private readonly MySqlConnectionFactory _factory;
    private MySqlConnection? _connection;
    private MySqlTransaction? _transaction;

    public MySqlTransactionScope(MySqlConnectionFactory factory)
    {
        _factory = factory;
        LastActivityUtc = DateTime.UtcNow;
        Executor = new TrackedExecutor(
            new MySqlQueryExecutor(new PinnedMySqlConnection(Held)),
            onStatement: () =>
            {
                LastActivityUtc = DateTime.UtcNow;
                StatementCount++;
            },
            onTouch: () => LastActivityUtc = DateTime.UtcNow,
            onFailure: () =>
            {
                if (State == TransactionState.Active)
                {
                    State = TransactionState.Aborted;
                }
            });
    }

    public TransactionState State { get; private set; } = TransactionState.None;
    public int StatementCount { get; private set; }
    public DateTime LastActivityUtc { get; private set; }
    public IQueryExecutor Executor { get; }

    private (MySqlConnection, MySqlTransaction) Held()
        => _connection is { } c && _transaction is { } t
            ? (c, t)
            : throw new InvalidOperationException("No transaction is open on this scope.");

    public async Task BeginAsync(CancellationToken ct)
    {
        if (State != TransactionState.None)
        {
            throw new InvalidOperationException("A transaction is already open.");
        }
        _connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            _transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        }
        catch
        {
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
        {
            throw new InvalidOperationException($"Cannot commit a transaction that is {State}.");
        }
        try
        {
            await _transaction!.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
    }

    public async Task RollbackAsync(CancellationToken ct)
    {
        if (State == TransactionState.None)
        {
            return;
        }
        try
        {
            await _transaction!.RollbackAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Hand the connection back whichever verb ended the transaction, and whether or not it
    /// succeeded — a scope that kept it after a failed commit would leak a pool slot.</summary>
    private async ValueTask ReleaseAsync()
    {
        State = TransactionState.None;
        if (_transaction is { } tx)
        {
            _transaction = null;
            await tx.DisposeAsync().ConfigureAwait(false);
        }
        if (_connection is { } conn)
        {
            _connection = null;
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (State != TransactionState.None)
        {
            try
            {
                await _transaction!.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The connection may already be gone; closing it below is the backstop.
            }
        }
        await ReleaseAsync().ConfigureAwait(false);
    }
}
