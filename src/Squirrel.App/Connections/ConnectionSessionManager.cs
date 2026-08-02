using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squirrel.Core.Data;
using Squirrel.Core.Schema;
using Squirrel.Core.Workspace;

namespace Squirrel.App.Connections;

/// <summary>
/// Default <see cref="IConnectionSessionManager"/>. Live sessions and in-flight connects/schema-loads
/// are guarded by a single lock; each id is connected at most once concurrently (single-flight), and
/// a session whose settings changed is disposed and rebuilt on next use. The secret store is read
/// lazily (it is attached after construction), so a password is fetched fresh at connect time.
///
/// Disposal is lease-aware: a running query holds a <see cref="SessionLease"/>, and a session is only
/// torn down once it is no longer live AND has no outstanding leases — so evicting, editing, switching
/// database on, or idle-sweeping a connection never pulls the pool out from under an in-flight query.
/// Sessions with no lease that have been idle past <see cref="_idleTimeout"/> are closed by a periodic
/// sweep so connections don't stay open indefinitely.
/// </summary>
public sealed class ConnectionSessionManager : IConnectionSessionManager
{
    private readonly IProviderRegistry _providers;
    private readonly Func<CredentialResolver?> _credentials;
    private readonly TimeSpan _idleTimeout;
    private readonly Func<DateTime> _clock;
    private readonly Timer? _sweepTimer;

    /// <inheritdoc />
    public event Action<Guid>? LiveChanged;

    // Never let a subscriber's fault escape into the connect/evict path it fired from.
    private void RaiseLiveChanged(Guid id)
    {
        try { LiveChanged?.Invoke(id); } catch { /* indicator refresh is best-effort */ }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, ConnectionSession> _live = new();
    // Value carries the target Info so a connect in flight for one database isn't reused for another
    // (the toolbar can switch DB on the same connection id while the first connect is still running).
    private readonly Dictionary<Guid, (ConnectionInfo Info, Task<ConnectionSession> Task)> _inflight = new();
    private readonly Dictionary<Guid, Task<ISchemaSnapshot?>> _schemaInflight = new();
    private bool _disposed;

    /// <summary>Default idle timeout before an unused connection is closed.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>How far ahead of a credential's real expiry the sweep disconnects it, so a stale token is
    /// never handed to a new pooled open. Comfortably above the sweep period (≤ 60 s).</summary>
    private static readonly TimeSpan ExpiryEvictSkew = TimeSpan.FromSeconds(90);

    public ConnectionSessionManager(
        IProviderRegistry providers, Func<CredentialResolver?> credentials,
        TimeSpan? idleTimeout = null, Func<DateTime>? clock = null, bool runSweepTimer = true)
    {
        _providers = providers;
        _credentials = credentials;
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        _clock = clock ?? (() => DateTime.UtcNow);
        // Sweep roughly every minute (never more often than needed to catch the timeout). Tests pass
        // runSweepTimer:false and drive SweepIdleAsync directly.
        if (runSweepTimer)
        {
            var period = TimeSpan.FromMilliseconds(Math.Clamp(_idleTimeout.TotalMilliseconds / 4, 15_000, 60_000));
            _sweepTimer = new Timer(_ => _ = SweepIdleAsync(), null, period, period);
        }
    }

    public Task<ConnectionSession> GetOrConnectAsync(ConnectionInfo info, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_live.TryGetValue(info.Id, out var existing) && SameConnection(existing.Info, info))
            {
                existing.LastUsedUtc = _clock();
                return Task.FromResult(existing);
            }
            if (_inflight.TryGetValue(info.Id, out var pending))
            {
                if (SameConnection(pending.Info, info)) return pending.Task;
                // A connect is in flight for a different database on this id — wait for it to settle,
                // then connect the requested database (BuildAsync will retire the stale session).
                return WaitThenConnectAsync(pending.Task, info, ct);
            }
            var task = BuildAsync(info, ct);
            _inflight[info.Id] = (info, task);
            return task;
        }
    }

    public async Task<SessionLease> AcquireAsync(ConnectionInfo info, CancellationToken ct)
    {
        // Connect (or reuse) then lease atomically. If the session got retired in the tiny window
        // between connect and lease, rebuild rather than lease a stale/soon-to-die session.
        for (var attempt = 0; ; attempt++)
        {
            var session = await GetOrConnectAsync(info, ct);
            lock (_gate)
            {
                if (!session.Retired || attempt >= 3)
                {
                    session.LeaseCount++;
                    session.LastUsedUtc = _clock();
                    return new SessionLease(this, session);
                }
            }
        }
    }

    public SessionLease Lease(ConnectionSession session)
    {
        lock (_gate)
        {
            session.LeaseCount++;
            session.LastUsedUtc = _clock();
        }
        return new SessionLease(this, session);
    }

    internal void ReleaseLease(ConnectionSession session)
    {
        bool disposeNow;
        lock (_gate)
        {
            if (session.LeaseCount > 0) session.LeaseCount--;
            session.LastUsedUtc = _clock();
            disposeNow = session.Retired && session.LeaseCount == 0;
        }
        if (disposeNow) _ = SafeDisposeAsync(session); // retired-in-use session freed at its last release
    }

    private async Task<ConnectionSession> WaitThenConnectAsync(
        Task<ConnectionSession> prior, ConnectionInfo info, CancellationToken ct)
    {
        try { await prior.ConfigureAwait(false); } catch { /* prior's failure is its caller's concern */ }
        return await GetOrConnectAsync(info, ct).ConfigureAwait(false);
    }

    public ConnectionSession? TryGet(Guid connectionId)
    {
        lock (_gate) return _live.TryGetValue(connectionId, out var s) ? s : null;
    }

    public Task<ISchemaSnapshot?> EnsureSchemaAsync(ConnectionSession session, CancellationToken ct)
    {
        if (session.Snapshot is not null) return Task.FromResult<ISchemaSnapshot?>(session.Snapshot);
        lock (_gate)
        {
            if (session.Snapshot is not null) return Task.FromResult<ISchemaSnapshot?>(session.Snapshot);
            if (_schemaInflight.TryGetValue(session.ConnectionId, out var pending)) return pending;
            var task = LoadSchemaAsync(session, ct);
            _schemaInflight[session.ConnectionId] = task;
            return task;
        }
    }

    public async Task EvictAsync(Guid connectionId)
    {
        ConnectionSession? session;
        var disposeNow = false;
        lock (_gate)
        {
            if (_live.TryGetValue(connectionId, out session))
            {
                _live.Remove(connectionId);
                if (session.LeaseCount == 0) disposeNow = true; else session.Retired = true;
            }
        }
        if (session is not null && disposeNow) await SafeDisposeAsync(session);
        if (session is not null) RaiseLiveChanged(connectionId); // left the live map
    }

    /// <summary>Close sessions that hold no lease and have been idle past the timeout, and disconnect any
    /// session whose credential is about to expire so a stale token is never handed to a new pooled open.
    /// An expiring session still holding a lease is retired (removed from the live map, kept alive for the
    /// running query) so the next connect rebuilds with a fresh credential. Safe to call anytime.</summary>
    internal async Task SweepIdleAsync()
    {
        List<ConnectionSession> toDispose = new();
        List<Guid> leftLive = new();
        List<Guid> expired = new();
        try
        {
            var now = _clock();
            var nowOffset = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
            lock (_gate)
            {
                foreach (var (id, session) in _live.ToArray())
                {
                    var expiring = CredentialResolver.IsExpiring(session.CredentialExpiresAt, nowOffset, ExpiryEvictSkew);
                    var idle = session.LeaseCount == 0 && now - session.LastUsedUtc >= _idleTimeout;
                    if (!idle && !expiring) continue;

                    _live.Remove(id);
                    leftLive.Add(id);
                    if (expiring) expired.Add(id);
                    // A leased-but-expiring session is retired, not disposed — its running query keeps the
                    // already-open connection until it releases; the next acquire rebuilds fresh.
                    if (session.LeaseCount == 0) toDispose.Add(session);
                    else session.Retired = true;
                }
            }
        }
        catch { /* sweep is best-effort; never surface */ }
        // Drop the cached token/password for expired sessions so the rebuild re-mints / re-prompts.
        if (expired.Count > 0 && _credentials() is { } resolver)
            foreach (var id in expired) resolver.Invalidate(id);
        foreach (var s in toDispose) await SafeDisposeAsync(s);
        foreach (var id in leftLive) RaiseLiveChanged(id); // idle/expired connections left the live map
    }

    /// <summary>Close and dispose every live/in-flight session (e.g. on a project switch) but keep the
    /// manager usable — it can connect again for the next project. In-flight queries are expected to be
    /// cancelled by the caller first; leases are not honored here.</summary>
    public Task CloseAllAsync() => CloseAllAsync(retire: false);

    public async ValueTask DisposeAsync()
    {
        _sweepTimer?.Dispose();
        // Shutdown: retire the manager so any later use throws, and dispose everything regardless of leases.
        await CloseAllAsync(retire: true);
    }

    private async Task CloseAllAsync(bool retire)
    {
        ConnectionSession[] all;
        lock (_gate)
        {
            if (retire) _disposed = true;
            all = _live.Values.ToArray();
            _live.Clear();
            _inflight.Clear();
            _schemaInflight.Clear();
        }
        foreach (var s in all) await SafeDisposeAsync(s);
        // On a project switch the manager stays usable, so tell listeners those ids went away. On retire
        // (shutdown) skip it — the app is tearing down and handlers may already be gone.
        if (!retire) foreach (var s in all) RaiseLiveChanged(s.ConnectionId);
    }

    private async Task<ConnectionSession> BuildAsync(ConnectionInfo info, CancellationToken ct)
    {
        // Yield so GetOrConnectAsync registers this task in _inflight before the body can finish
        // (a synchronously-completing provider would otherwise run the finally before registration).
        await Task.Yield();
        try
        {
            // A live session may exist but be stale (settings edited) — retire it before rebuilding,
            // deferring its disposal if a query still holds a lease on it.
            ConnectionSession? stale;
            var disposeStale = false;
            lock (_gate)
            {
                _live.TryGetValue(info.Id, out stale);
                if (stale is not null && SameConnection(stale.Info, info)) { stale.LastUsedUtc = _clock(); return stale; }
                if (stale is not null)
                {
                    _live.Remove(info.Id);
                    if (stale.LeaseCount == 0) disposeStale = true; else stale.Retired = true;
                }
            }
            if (stale is not null && disposeStale) await SafeDisposeAsync(stale);

            var (provider, factory, credential) =
                await ConnectionFactoryBuilder.BuildAsync(_providers, _credentials(), info, forceRefresh: false, ct);

            bool ok;
            try { ok = await factory.TestConnectionAsync(ct); }
            catch (Exception ex)
            {
                await SafeDisposeFactoryAsync(factory);
                throw new ConnectionFailedException(Describe(info, ex.Message), ex);
            }
            if (!ok)
            {
                await SafeDisposeFactoryAsync(factory);
                throw new ConnectionFailedException(Describe(info, "connection test failed"));
            }

            var session = new ConnectionSession(
                info, factory, provider.CreateQueryExecutor(factory), provider.CreateMetadataReader(factory),
                credential.ExpiresAt)
            { LastUsedUtc = _clock() };
            bool disposedDuringConnect;
            lock (_gate)
            {
                // The manager may have been disposed while we were awaiting the connect. Don't repopulate
                // the cleared _live map — that would leak this session's factory (never disposed).
                disposedDuringConnect = _disposed;
                if (!disposedDuringConnect) _live[info.Id] = session;
            }
            if (disposedDuringConnect)
            {
                await SafeDisposeAsync(session);
                throw new ObjectDisposedException(nameof(ConnectionSessionManager));
            }
            RaiseLiveChanged(info.Id); // a new session entered the live map
            return session;
        }
        finally
        {
            lock (_gate) _inflight.Remove(info.Id);
        }
    }

    private async Task<ISchemaSnapshot?> LoadSchemaAsync(ConnectionSession session, CancellationToken ct)
    {
        try
        {
            var snapshot = await session.Metadata.LoadSnapshotAsync(session.Info.Database, ct);
            session.Snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            lock (_gate) _schemaInflight.Remove(session.ConnectionId);
        }
    }

    private static string Describe(ConnectionInfo info, string detail)
        => $"Could not connect to '{info.Name}' ({info.Host}:{info.Port}/{info.Database}): {detail}";

    /// <summary>Compares only the fields that define the live connection; name/environment/color are cosmetic.</summary>
    private static bool SameConnection(ConnectionInfo a, ConnectionInfo b)
        => a.ProviderId == b.ProviderId && a.Host == b.Host && a.Port == b.Port
           && a.Database == b.Database && a.User == b.User && SameOptions(a.Options, b.Options);

    private static bool SameOptions(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var bv) || bv != v) return false;
        return true;
    }

    private static async Task SafeDisposeAsync(ConnectionSession session)
    {
        try { await session.DisposeAsync(); } catch { /* best-effort */ }
    }

    private static async Task SafeDisposeFactoryAsync(IDbConnectionFactory factory)
    {
        try { await factory.DisposeAsync(); } catch { /* best-effort */ }
    }
}
