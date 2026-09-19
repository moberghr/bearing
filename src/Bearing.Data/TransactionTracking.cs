using System.Runtime.CompilerServices;
using Bearing.Core.Data;

namespace Bearing.Data;

/// <summary>
/// Wraps the executor a <see cref="ITransactionScope"/> hands out so the scope learns three things it cannot
/// see from the outside: that a statement ran, that the connection was touched, and that something failed
/// (#131).
/// <para>
/// <b>Touched and ran are separate signals, and the split matters twice.</b> A touch stamps
/// <see cref="ITransactionScope.LastActivityUtc"/>, which is what the idle clocks measure; a statement also
/// increments the count the status chip renders as "N uncommitted". <see cref="CountAsync"/> only touches,
/// because it is a probe Bearing runs on the user's behalf — counting it would have every write-confirmation
/// row count and every press of <c>[Count]</c> claim a statement nobody wrote.
/// </para>
/// <para>
/// <b>A touch is stamped on the way out as well as on the way in.</b> Stamping only at the start measures
/// idleness from when a statement <i>began</i>, so a sixteen-minute UPDATE — or a long "fetch all rows" —
/// crossed the auto-rollback threshold while it was still running, and the sweep then rolled back a
/// transaction with a live command on its connection.
/// </para>
/// <para>
/// <b>One abort rule, both engines.</b> A failure marks the transaction <see cref="TransactionState.Aborted"/>,
/// which is exactly what Postgres did — every later statement answers <c>25P02</c> until it ends — and
/// conservative on SQL Server, where some errors leave a transaction the server would still commit. The
/// asymmetry is deliberate and costs a rollback of work that server would have taken, against the
/// alternative of offering a <c>COMMIT</c> that Postgres silently turns into a <c>ROLLBACK</c>.
/// </para>
/// <para>
/// <b><see cref="CountAsync"/> is exempt from the abort rule</b>, and is the only one. The provider runs it
/// under a savepoint for that reason, so a shape that cannot be wrapped leaves the transaction exactly as it
/// was, and marking it aborted here would report a failure the transaction did not suffer.
/// </para>
/// </summary>
internal sealed class TrackedExecutor : IQueryExecutor
{
    private readonly IQueryExecutor _inner;
    private readonly Action _onStatement;
    private readonly Action _onTouch;
    private readonly Action _onFailure;

    internal TrackedExecutor(IQueryExecutor inner, Action onStatement, Action onTouch, Action onFailure)
    {
        _inner = inner;
        _onStatement = onStatement;
        _onTouch = onTouch;
        _onFailure = onFailure;
    }

    public async Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        _onStatement();
        try
        {
            var results = await _inner.ExecuteAsync(sql, options, ct).ConfigureAwait(false);
            // The executors return a statement error as a result rather than throwing it, so the failure
            // this has to notice is usually here and not in the catch below.
            if (results.Any(r => !r.Success)) _onFailure();
            return results;
        }
        catch { _onFailure(); throw; }
        finally { _onTouch(); }
    }

    public async Task<QueryResult> ExecutePageAsync(string pageSql, CancellationToken ct)
    {
        _onStatement();
        try
        {
            var result = await _inner.ExecutePageAsync(pageSql, ct).ConfigureAwait(false);
            if (!result.Success) _onFailure();
            return result;
        }
        catch { _onFailure(); throw; }
        finally { _onTouch(); }
    }

    public async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string sql, QueryOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        _onStatement();
        // Hand-driven rather than `await foreach`, because a yield may not sit inside a try with a catch.
        await using var rows = _inner.StreamRowsAsync(sql, options, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool more;
            try { more = await rows.MoveNextAsync().ConfigureAwait(false); }
            catch { _onFailure(); _onTouch(); throw; }
            // Touched per batch, not once at the end: a stream that takes an hour is the transaction being
            // used for that hour, and an idle clock that only learned about it afterwards would have rolled
            // it back half way through.
            _onTouch();
            if (!more) yield break;
            yield return rows.Current;
        }
    }

    /// <summary>Touches but does not count, and is exempt from the abort rule — see the class remarks.</summary>
    public async Task<long?> CountAsync(string countSql, CancellationToken ct)
    {
        _onTouch();
        try { return await _inner.CountAsync(countSql, ct).ConfigureAwait(false); }
        finally { _onTouch(); }
    }

    public async Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(
        IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
    {
        _onStatement();
        try
        {
            var results = await _inner.ExecuteWriteAsync(commands, ct).ConfigureAwait(false);
            if (results.Any(r => !r.Success)) _onFailure();
            return results;
        }
        catch { _onFailure(); throw; }
        finally { _onTouch(); }
    }
}
