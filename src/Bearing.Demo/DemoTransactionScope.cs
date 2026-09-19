using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;

namespace Bearing.Demo;

/// <summary>
/// Manual-commit mode with no server (#131 in #64's session). There is nothing to begin, commit or roll
/// back — the demo catalog is in memory and <see cref="DemoExecutor.ExecuteWriteAsync"/> only records the
/// DML it was handed — so this keeps the <i>state</i> a transaction has and delegates the running.
/// <para>
/// That is the right fake and not a shortcut: what a demo session is for is showing the affordance — a write
/// that does not commit itself, a chip that counts it, two buttons that end it — and every one of those
/// follows from <see cref="State"/> and <see cref="StatementCount"/> rather than from a server. What it
/// cannot show is a rollback undoing anything, because nothing was done.
/// </para>
/// </summary>
public sealed class DemoTransactionScope : ITransactionScope
{
    private readonly IQueryExecutor _inner;

    public DemoTransactionScope(IQueryExecutor inner)
    {
        _inner = inner;
        Executor = new CountingExecutor(this);
        LastActivityUtc = DateTime.UtcNow;
    }

    public TransactionState State { get; private set; } = TransactionState.None;
    public int StatementCount { get; private set; }
    public DateTime LastActivityUtc { get; private set; }
    public IQueryExecutor Executor { get; }

    public Task BeginAsync(CancellationToken ct)
    {
        State = TransactionState.Active;
        StatementCount = 0;
        LastActivityUtc = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken ct)
    {
        if (State != TransactionState.Active)
            throw new InvalidOperationException($"Cannot commit a transaction that is {State}.");
        State = TransactionState.None;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken ct)
    {
        State = TransactionState.None;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        State = TransactionState.None;
        return default;
    }

    private void Ran() { StatementCount++; Touched(); }

    /// <summary>Activity without a statement — see <see cref="CountingExecutor.CountAsync"/>.</summary>
    private void Touched() => LastActivityUtc = DateTime.UtcNow;

    private void Failed() { if (State == TransactionState.Active) State = TransactionState.Aborted; }

    /// <summary>The demo's equivalent of <c>Bearing.Data</c>'s tracker: the same two signals, over an
    /// executor that never opened a connection.</summary>
    private sealed class CountingExecutor : IQueryExecutor
    {
        private readonly DemoTransactionScope _scope;

        internal CountingExecutor(DemoTransactionScope scope) => _scope = scope;

        public async Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
        {
            _scope.Ran();
            var results = await _scope._inner.ExecuteAsync(sql, options, ct).ConfigureAwait(false);
            if (results.Any(r => !r.Success)) _scope.Failed();
            return results;
        }

        public async Task<QueryResult> ExecutePageAsync(string pageSql, CancellationToken ct)
        {
            _scope.Ran();
            return await _scope._inner.ExecutePageAsync(pageSql, ct).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<RowBatch> StreamRowsAsync(
            string sql, QueryOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            _scope.Ran();
            await foreach (var batch in _scope._inner.StreamRowsAsync(sql, options, ct).ConfigureAwait(false))
                yield return batch;
        }

        /// <summary>Touches without counting, exactly as <c>TrackedExecutor</c> does: a count is a probe
        /// Bearing runs on the user's behalf, and counting it would have every write confirmation and every
        /// press of <c>[Count]</c> claim a statement nobody wrote (#131).</summary>
        public Task<long?> CountAsync(string countSql, CancellationToken ct)
        {
            _scope.Touched();
            return _scope._inner.CountAsync(countSql, ct);
        }

        public async Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(
            IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
        {
            _scope.Ran();
            var results = await _scope._inner.ExecuteWriteAsync(commands, ct).ConfigureAwait(false);
            if (results.Any(r => !r.Success)) _scope.Failed();
            return results;
        }
    }
}
