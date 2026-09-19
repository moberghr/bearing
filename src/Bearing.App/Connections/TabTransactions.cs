using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Logging;

namespace Bearing.App.Connections;

/// <summary>
/// One open transaction, held by the tab that opened it (#131): the scope that owns the physical connection,
/// the lease that stops the pool under it being disposed, and the identity of what it was opened against.
/// </summary>
public sealed class TabTransaction
{
    internal TabTransaction(SessionLease lease, ITransactionScope scope, ConnectionInfo info)
    {
        Lease = lease;
        Scope = scope;
        Key = SessionKey.For(info);
        ConnectionName = info.Name;
        Database = info.Database;
        ProviderId = info.ProviderId;
        Environment = string.IsNullOrWhiteSpace(info.Environment) ? null : info.Environment;
        OpenedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Opaque, per transaction, and only ever written to the query log — what ties the statements
    /// that ran inside this one to the commit or rollback that ended it (#131, §1.6). Not the server's: no
    /// engine hands out an id a client can rely on across a whole transaction, and this needs no more than
    /// to be unique.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    internal SessionLease Lease { get; }

    /// <summary>The engine's transaction. Its <see cref="ITransactionScope.Executor"/> is what the tab runs
    /// everything on while this is open.</summary>
    public ITransactionScope Scope { get; }

    /// <summary>What it was opened against. A tab may not change connection or database while it is open —
    /// the pool would be a different one, and this transaction lives on a connection out of <i>this</i>
    /// one (§9.4a).</summary>
    public SessionKey Key { get; }

    /// <summary>Named at open time, so a message about it reads the same after the connection is renamed
    /// or deleted — the reason <c>QueryLogReport</c> stores an environment rather than resolving one.</summary>
    public string ConnectionName { get; }

    public string Database { get; }

    /// <summary>Captured at open time along with the name and database, so the entry the log gets when this
    /// ends describes the connection as it was — §1.6's rule that an environment is a fact about the past,
    /// not something to resolve later from a connection that may since have been re-pointed or deleted.</summary>
    public string ProviderId { get; }

    /// <inheritdoc cref="ProviderId"/>
    public string? Environment { get; }

    public DateTime OpenedAtUtc { get; }

    public TransactionState State => Scope.State;
    public int StatementCount => Scope.StatementCount;

    /// <summary>How long since anything ran in it — what the idle clocks measure. Not the age: a transaction
    /// worked in continuously for an hour is not abandoned, and one opened a minute ago and forgotten is on
    /// its way to being.</summary>
    public TimeSpan Idle => DateTime.UtcNow - Scope.LastActivityUtc;

    public TimeSpan Age => DateTime.UtcNow - OpenedAtUtc;
}

/// <summary>
/// Which tabs are holding a transaction open, and the only place one is opened or ended (#131).
/// <para>
/// <b>Per tab, not per session.</b> A pool is shared by every tab on the same (connection, database), so a
/// transaction owned by the session would be one two tabs were writing into without either saying so — a
/// Commit in one would commit the other's work. A tab is the unit the user is actually thinking in.
/// </para>
/// <para>
/// <b>What the lease buys.</b> It is not what keeps the physical connection: the scope holds that itself.
/// It stops the <i>pool</i> being disposed while the transaction is open, which is exactly what the idle
/// sweep, an evict and a connection rebuild all try to do. It also means an <c>EvictConnectionAsync</c>
/// merely retires the session rather than disposing it — so Disconnect must roll these back itself, or the
/// transaction survives it invisibly.
/// </para>
/// </summary>
public sealed class TabTransactions
{
    private readonly Dictionary<EditorTabViewModel, TabTransaction> _open = new();
    private readonly IConnectionSessionManager _sessions;
    private readonly IProviderRegistry _providers;
    private readonly IQueryLog _log;

    public TabTransactions(IConnectionSessionManager sessions, IProviderRegistry providers, IQueryLog log)
    {
        _sessions = sessions;
        _providers = providers;
        _log = log;
    }

    /// <summary>Raised whenever a transaction opens, ends, or changes state — the chip and the two commands
    /// read from it. Coarse on purpose: there are at most a handful of these.</summary>
    public event Action? Changed;

    public TabTransaction? For(EditorTabViewModel tab) => _open.GetValueOrDefault(tab);

    public bool Any => _open.Count > 0;

    public int Count => _open.Count;

    public IReadOnlyCollection<TabTransaction> All => _open.Values.ToList();

    /// <summary>The tabs holding one, with their transactions — for the guards, which have to name the tab.</summary>
    public IReadOnlyList<(EditorTabViewModel Tab, TabTransaction Transaction)> Entries
        => _open.Select(kv => (kv.Key, kv.Value)).ToList();

    /// <summary>
    /// Open a transaction for <paramref name="tab"/> on <paramref name="info"/>, or return the refusal to
    /// show the user. Null <c>Error</c> means it opened.
    /// </summary>
    public async Task<(TabTransaction? Transaction, string? Error)> OpenAsync(
        EditorTabViewModel tab, ConnectionInfo info, ConnectionSession session, CancellationToken ct)
    {
        if (_open.TryGetValue(tab, out var already)) return (already, null);

        var key = SessionKey.For(info);
        if (_open.Values.Count(t => t.Key == key) >= CommitPolicy.MaxOpenPerPool)
            return (null, $"{CommitPolicy.MaxOpenPerPool} tabs already have an uncommitted transaction on "
                          + $"{info.Name}/{info.Database}. Commit or roll one back before starting another — "
                          + "each one holds a connection from that database's pool.");

        // A second lease of the caller's own session, not a fresh one: the caller's is released when its
        // run ends, and this one has to outlive it by as long as the user leaves the transaction open.
        var lease = _sessions.Lease(session);
        try
        {
            var scope = _providers.Get(info.ProviderId).CreateTransactionScope(session.Factory);
            try
            {
                await scope.BeginAsync(ct).ConfigureAwait(true);
            }
            catch
            {
                await scope.DisposeAsync().ConfigureAwait(true);
                throw;
            }
            var open = new TabTransaction(lease, scope, info);
            _open[tab] = open;
            Changed?.Invoke();
            return (open, null);
        }
        catch (Exception ex)
        {
            // The lease is only released here — on the success path it is the transaction's for its whole
            // life, and releasing it now would let the sweep dispose the pool the transaction sits on.
            lease.Dispose();
            return (null, $"Could not start a transaction on {info.Name}: {ex.Message}");
        }
    }

    /// <summary>Commit <paramref name="tab"/>'s transaction. The message is the status line either way; the
    /// transaction is released whatever happens, because a scope that failed to commit has already let go
    /// of its connection.</summary>
    public Task<string> CommitAsync(EditorTabViewModel tab, CancellationToken ct)
        => EndAsync(tab, commit: true, ct);

    public Task<string> RollbackAsync(EditorTabViewModel tab, CancellationToken ct)
        => EndAsync(tab, commit: false, ct);

    private async Task<string> EndAsync(EditorTabViewModel tab, bool commit, CancellationToken ct)
    {
        if (!_open.TryGetValue(tab, out var open)) return "No open transaction on this tab.";
        if (commit && open.State == TransactionState.Aborted)
            return $"A statement failed in this transaction — it can only be rolled back. "
                   + $"({open.ConnectionName}/{open.Database})";

        var statements = open.StatementCount;
        string? failure = null;
        try
        {
            if (commit) await open.Scope.CommitAsync(ct).ConfigureAwait(true);
            else await open.Scope.RollbackAsync(ct).ConfigureAwait(true);
            return commit
                ? $"Committed {Statements(statements)} on {open.ConnectionName}/{open.Database}."
                : $"Rolled back {Statements(statements)} on {open.ConnectionName}/{open.Database}.";
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return $"{(commit ? "Commit" : "Rollback")} failed on {open.ConnectionName}: {ex.Message}";
        }
        finally
        {
            // Logged whether or not it worked, and in the finally for that reason. A failed commit still
            // ends the transaction here — the scope has already let go of its connection — so leaving it out
            // would put the statements in the log with a transaction id no terminating row ever matches,
            // which reads exactly like a transaction that is still open (§1.6).
            Log(open, committed: commit, failure);
            await ForgetAsync(tab).ConfigureAwait(true);
        }
    }

    /// <summary>Roll back and forget every transaction on <paramref name="connectionId"/> — what Disconnect,
    /// a connection edit and a project close each do before tearing the session down.</summary>
    public async Task<int> RollbackConnectionAsync(Guid connectionId, CancellationToken ct)
        => await RollbackWhereAsync(t => t.Key.ConnectionId == connectionId, ct).ConfigureAwait(true);

    /// <summary>
    /// Roll back and forget the transactions held by <paramref name="tabs"/> — for a route that drops tabs
    /// without closing them one at a time (a project removed while its tabs are parked).
    /// <para>
    /// A transaction left on a tab that is no longer in any tab list is unreachable rather than merely
    /// invisible: nothing can select it, so neither the chip nor either command can ever reach it, while it
    /// goes on holding a pooled connection and a lease that stops the session being disposed — and it still
    /// counts against <see cref="CommitPolicy.MaxOpenPerPool"/>.
    /// </para>
    /// </summary>
    public async Task<int> RollbackForTabsAsync(IEnumerable<EditorTabViewModel> tabs, CancellationToken ct)
    {
        var set = new HashSet<EditorTabViewModel>(tabs);
        var held = _open.Where(kv => set.Contains(kv.Key)).Select(kv => kv.Value).ToHashSet();
        return held.Count == 0 ? 0 : await RollbackWhereAsync(held.Contains, ct).ConfigureAwait(true);
    }

    /// <summary>Roll back and forget everything <paramref name="match"/> selects; returns how many. The
    /// primitive the guards in <c>TransactionGuard</c> are written against.</summary>
    public async Task<int> RollbackWhereAsync(Func<TabTransaction, bool> match, CancellationToken ct)
    {
        var doomed = _open.Where(kv => match(kv.Value)).Select(kv => kv.Key).ToList();
        foreach (var tab in doomed)
        {
            var open = _open[tab];
            // The two verbs refuse outright while the tab is mid-statement, because a commit issued on a
            // connection with a live command throws and the teardown behind it disposes that connection
            // under a live reader. This path cannot refuse — the disconnect, the close or the quit is
            // already happening — so it cancels the statement instead. Fire-and-forget, as every other
            // cancel here is: the rollback below and the dispose after it are what actually end the
            // transaction, and the server ends it either way once the connection goes.
            if (tab.IsRunning) tab.CancelRun();
            try { await open.Scope.RollbackAsync(ct).ConfigureAwait(true); }
            catch { /* best-effort: the connection may already be gone, and the server rolls back either way */ }
            // Logged here too, and that is the point of ending every transaction through these two methods:
            // a rollback forced by quit, a disconnect, a tab close or a deleted connection is still the end
            // of that transaction, and a history that recorded only the ones ended by the two buttons could
            // not tell "committed" from "discarded on the way out" (§1.6).
            Log(open, committed: false);
            await ForgetAsync(tab).ConfigureAwait(true);
        }
        return doomed.Count;
    }

    /// <summary>
    /// Record the end as an entry of its own, carrying the transaction's id so a report can tie it to the
    /// statements that ran inside it (§1.6). It <em>is</em> SQL the user caused to run against the server,
    /// which is the line §9.13 draws — a terminate is not a statement anybody typed, and this is the
    /// statement the whole feature exists to make them type.
    /// <para>
    /// Best-effort, like every other write to the log (§5.2): a persistence failure must never be the reason
    /// a transaction cannot be ended.
    /// </para>
    /// </summary>
    private void Log(TabTransaction open, bool committed, string? failure = null)
    {
        try
        {
            _log.Append(new QueryLogEntry
            {
                ExecutedAt = DateTimeOffset.UtcNow,
                ProviderId = open.ProviderId,
                ConnectionName = open.ConnectionName,
                ConnectionId = open.Key.ConnectionId,
                Environment = open.Environment,
                Database = open.Database,
                SqlText = committed ? "commit" : "rollback",
                Duration = open.Age,
                RowCount = 0,
                // False records an end that was attempted and failed — which is a third outcome, and not
                // one a report may read as either of the other two.
                Success = failure is null,
                ErrorMessage = failure,
                TransactionId = open.Id,
            });
        }
        catch { /* §5.2 */ }
    }

    /// <summary>Drop a tab's transaction from the map and let go of everything it held. Idempotent.</summary>
    private async ValueTask ForgetAsync(EditorTabViewModel tab)
    {
        if (!_open.Remove(tab, out var open)) return;
        await open.Scope.DisposeAsync().ConfigureAwait(true);
        open.Lease.Dispose();
        Changed?.Invoke();
    }

    private static string Statements(int count) => count == 1 ? "1 statement" : $"{count} statements";
}
