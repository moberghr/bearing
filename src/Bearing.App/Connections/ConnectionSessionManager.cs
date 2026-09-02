using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Core.Workspace;

namespace Bearing.App.Connections;

/// <summary>
/// Default <see cref="IConnectionSessionManager"/>. Live sessions and in-flight connects/schema-loads
/// are guarded by a single lock; each <see cref="SessionKey"/> is connected at most once concurrently
/// (single-flight), and a session whose settings changed is disposed and rebuilt on next use. The secret
/// store is read lazily (it is attached after construction), so a password is fetched fresh at connect time.
///
/// Disposal is lease-aware: a running query holds a <see cref="SessionLease"/>, and a session is only
/// torn down once it is no longer live AND has no outstanding leases — so evicting, editing, or
/// idle-sweeping a connection never pulls the pool out from under an in-flight query.
/// Sessions with no lease that have been idle past <see cref="_idleTimeout"/> are closed by a periodic
/// sweep so connections don't stay open indefinitely.
/// </summary>
public sealed class ConnectionSessionManager : IConnectionSessionManager
{
    private readonly IProviderRegistry _providers;
    private readonly Func<CredentialResolver?> _credentials;
    private TimeSpan _idleTimeout;
    private readonly Func<DateTime> _clock;
    private readonly Timer? _sweepTimer;

    /// <inheritdoc />
    public event Action<SessionKey>? LiveChanged;

    /// <inheritdoc />
    public event Action<Guid>? LinkChanged;

    // Never let a subscriber's fault escape into the connect/evict path it fired from.
    private void RaiseLiveChanged(SessionKey key)
    {
        try { LiveChanged?.Invoke(key); } catch { /* indicator refresh is best-effort */ }
    }

    private void RaiseLinkChanged(Guid connectionId)
    {
        try { LinkChanged?.Invoke(connectionId); } catch { /* indicator refresh is best-effort */ }
    }

    private readonly object _gate = new();
    // Every map here is keyed by (connection, database) — see SessionKey. A pool belongs to one database, so
    // an id-only key made switching database on a tab count as "settings changed" and tore the pool down (#54).
    private readonly Dictionary<SessionKey, ConnectionSession> _live = new();
    // Value carries the target Info because the key does not cover host/port/user/options: a connection edited
    // while its connect is still running must not have the in-flight (old-settings) task handed back.
    private readonly Dictionary<SessionKey, (ConnectionInfo Info, Task<ConnectionSession> Task)> _inflight = new();
    private readonly Dictionary<SessionKey, Task<ISchemaSnapshot?>> _schemaInflight = new();
    // Loaded snapshots, same key as the in-flight map, and deliberately OUTLIVING the sessions that read
    // them. A snapshot has no connection dependency — it's an immutable set of tables/columns/FKs — so tying
    // its lifetime to a pool meant completion died on every disconnect, credential expiry, and (worst,
    // because it is automatic and invisible) idle sweep. Only an event that makes the catalog untrue drops an
    // entry: see InvalidateSchema.
    private readonly Dictionary<SessionKey, ISchemaSnapshot> _schemaCache = new();
    // Server links: connections we have completed a handshake to, keyed by id alone. Deliberately COARSER
    // than _live and deliberately outliving it. Postgres binds a connection to a database at startup, so a
    // pool is per (connection, database) and there is nothing else it could be — but "am I connected to this
    // server?" is a question about the server, and answering it from _live made the app contradict itself:
    // the schema tree's server row lit while the tab next to it on another database showed a broken chain,
    // and Connect (one database) was not the inverse of Disconnect (all of them). A link is what the user
    // opted into; which database pools happen to be warm underneath is a cache, and the idle sweep emptying
    // that cache must not read as "the app disconnected me while I was reading results".
    private readonly HashSet<Guid> _links = new();
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
        // Tests pass runSweepTimer:false and drive SweepIdleAsync directly.
        if (runSweepTimer)
        {
            _sweepTimer = new Timer(_ => _ = SweepIdleAsync(), null, SweepPeriod, SweepPeriod);
        }
    }

    /// <summary>
    /// How long an unused, unleased session is kept before the sweep closes it. Settable so the setting
    /// applies to the running app: the next sweep uses the new value, and the sweep's own cadence is
    /// rescheduled so a shortened timeout isn't waited out at the old period.
    /// </summary>
    public TimeSpan IdleTimeout
    {
        get => _idleTimeout;
        set
        {
            if (value <= TimeSpan.Zero || value == _idleTimeout) return;
            _idleTimeout = value;
            _sweepTimer?.Change(SweepPeriod, SweepPeriod);
        }
    }

    // Sweep roughly every minute — often enough to catch the timeout, never busier than needed.
    private TimeSpan SweepPeriod
        => TimeSpan.FromMilliseconds(Math.Clamp(_idleTimeout.TotalMilliseconds / 4, 15_000, 60_000));

    public Task<ConnectionSession> GetOrConnectAsync(ConnectionInfo info, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = SessionKey.For(info);
            if (_live.TryGetValue(key, out var existing) && SameConnection(existing.Info, info))
            {
                existing.LastUsedUtc = _clock();
                return Task.FromResult(existing);
            }
            if (_inflight.TryGetValue(key, out var pending))
            {
                if (SameConnection(pending.Info, info)) return pending.Task;
                // Same connection+database, but the record was edited (host/port/user/options) while its
                // connect was still running — wait for that one to settle, then connect with the new
                // settings (BuildAsync will retire the stale session).
                return WaitThenConnectAsync(pending.Task, info, ct);
            }
            var task = BuildAsync(info, ct);
            _inflight[key] = (info, task);
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

    public ConnectionSession? TryGet(SessionKey key)
    {
        lock (_gate) return _live.TryGetValue(key, out var s) ? s : null;
    }

    /// <inheritdoc />
    public bool IsAnyLive(Guid connectionId)
    {
        lock (_gate) return _live.Keys.Any(k => k.ConnectionId == connectionId);
    }

    /// <inheritdoc />
    public bool IsLinked(Guid connectionId)
    {
        lock (_gate) return _links.Contains(connectionId);
    }

    /// <summary>Drop the server link when the caller has just removed the last evidence for it. Returns
    /// whether it changed, so the caller can raise <see cref="LinkChanged"/> outside the lock.</summary>
    private bool UnlinkIfNothingLive(Guid connectionId)
    {
        lock (_gate)
            return !_live.Keys.Any(k => k.ConnectionId == connectionId) && _links.Remove(connectionId);
    }

    /// <summary>The last snapshot read for this connection+database, whether or not a session is live now.
    /// This is what lets completion keep working while disconnected — it never needed the connection, only
    /// the catalog it already read.</summary>
    public ISchemaSnapshot? TryGetSnapshot(Guid connectionId, string database)
    {
        lock (_gate) return _schemaCache.TryGetValue(new SessionKey(connectionId, database), out var s) ? s : null;
    }

    /// <summary>
    /// Forget the cached schema for a connection (every database). Call this — and not plain
    /// <see cref="EvictAsync"/> — when the catalog the snapshot describes is no longer the truth: the
    /// connection was re-pointed at a different server, deleted, or explicitly refreshed. A disconnect is
    /// <b>not</b> one of those: the schema of a server you've stopped talking to is still its schema.
    /// </summary>
    public void InvalidateSchema(Guid connectionId)
    {
        lock (_gate)
        {
            foreach (var key in _schemaCache.Keys.Where(k => k.ConnectionId == connectionId).ToArray())
                _schemaCache.Remove(key);
        }
    }

    public Task<ISchemaSnapshot?> EnsureSchemaAsync(ConnectionSession session, CancellationToken ct)
    {
        if (session.Snapshot is not null) return Task.FromResult<ISchemaSnapshot?>(session.Snapshot);
        lock (_gate)
        {
            if (session.Snapshot is not null) return Task.FromResult<ISchemaSnapshot?>(session.Snapshot);
            var key = session.Key;
            // A reconnect to a connection+database we've already read adopts the cached snapshot instead of
            // re-reading the whole catalog. Same key as the load, so one database can never pick up another
            // database's snapshot — which drives editability and FK nav, not just the popup.
            if (_schemaCache.TryGetValue(key, out var cached))
            {
                session.Snapshot = cached;
                return Task.FromResult<ISchemaSnapshot?>(cached);
            }
            if (_schemaInflight.TryGetValue(key, out var pending)) return pending;
            var task = LoadSchemaAsync(session, ct);
            // Only register a load that is genuinely still in flight. LoadSchemaAsync clears its own key when
            // it finishes, so a load that completed *synchronously* has already done that removal — adding it
            // here would leave a completed task in the map that nothing ever clears, and every later call for
            // this key would then be served that stale snapshot instead of re-reading the catalog. Real
            // Npgsql reads always yield, which is the only reason this never bit in production.
            if (!task.IsCompleted) _schemaInflight[key] = task;
            return task;
        }
    }

    public async Task EvictAsync(SessionKey key)
    {
        ConnectionSession? session;
        var disposeNow = false;
        lock (_gate)
        {
            if (_live.TryGetValue(key, out session))
            {
                _live.Remove(key);
                if (session.LeaseCount == 0) disposeNow = true; else session.Retired = true;
            }
        }
        if (session is not null && disposeNow) await SafeDisposeAsync(session);
        if (session is not null) RaiseLiveChanged(key); // left the live map
        // Both callers of this overload are abandoning an attempt — a cancelled connect, or the evict before
        // a credential retry — so once the last pool is gone there is nothing left standing behind the claim
        // that we are linked to the server. The idle sweep does NOT come through here, which is exactly what
        // lets a swept connection stay linked (see SweepIdleAsync).
        if (UnlinkIfNothingLive(key.ConnectionId)) RaiseLinkChanged(key.ConnectionId);
    }

    /// <inheritdoc />
    public async Task EvictConnectionAsync(Guid connectionId)
    {
        SessionKey[] keys;
        bool wasLinked;
        lock (_gate)
        {
            keys = _live.Keys.Where(k => k.ConnectionId == connectionId).ToArray();
            // Unconditionally, and before the evicts: this is the server-level teardown, and after an idle
            // sweep there can be a link with no pools left to evict — dropping it only as a side effect of
            // removing the last session would leave Disconnect doing nothing at all in that state.
            wasLinked = _links.Remove(connectionId);
        }
        foreach (var key in keys) await EvictAsync(key);
        if (wasLinked) RaiseLinkChanged(connectionId);
    }

    /// <summary>Close sessions that hold no lease and have been idle past the timeout, and disconnect any
    /// session whose credential is about to expire so a stale token is never handed to a new pooled open.
    /// An expiring session still holding a lease is retired (removed from the live map, kept alive for the
    /// running query) so the next connect rebuilds with a fresh credential. Safe to call anytime.
    ///
    /// <b>An idle sweep does not break the server link.</b> It reclaims pools, and a pool is re-openable from
    /// the cached credential without a prompt — whereas the user sitting reading a result set for half an hour
    /// and watching the chain silently snap is the surprise this whole model exists to remove. Npgsql has
    /// already pruned the idle physical connections underneath by then anyway, so the sweep is reclaiming
    /// bookkeeping, not sockets. An <i>expiring credential</i> is the opposite case and does unlink: the thing
    /// the link was evidence of has gone stale, and the rebuild has to re-mint or re-prompt.</summary>
    internal async Task SweepIdleAsync()
    {
        List<ConnectionSession> toDispose = new();
        List<SessionKey> leftLive = new();
        HashSet<Guid> expired = new();
        try
        {
            var now = _clock();
            var nowOffset = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
            lock (_gate)
            {
                foreach (var (key, session) in _live.ToArray())
                {
                    var expiring = CredentialResolver.IsExpiring(session.CredentialExpiresAt, nowOffset, ExpiryEvictSkew);
                    var idle = session.LeaseCount == 0 && now - session.LastUsedUtc >= _idleTimeout;
                    if (!idle && !expiring) continue;

                    _live.Remove(key);
                    leftLive.Add(key);
                    // A connection's credential is shared by every database it is open on, so several keys can
                    // land here for one id — the resolver only needs invalidating once.
                    if (expiring) expired.Add(key.ConnectionId);
                    // A leased-but-expiring session is retired, not disposed — its running query keeps the
                    // already-open connection until it releases; the next acquire rebuilds fresh.
                    if (session.LeaseCount == 0) toDispose.Add(session);
                    else session.Retired = true;
                }
            }
        }
        catch { /* sweep is best-effort; never surface */ }
        // Drop the cached token/password for expired sessions so the rebuild re-mints / re-prompts, and with
        // it the server link — an expired credential is no longer evidence of anything.
        if (expired.Count > 0)
        {
            if (_credentials() is { } resolver)
                foreach (var id in expired) resolver.Invalidate(id);
            List<Guid> unlinked = new();
            lock (_gate)
                foreach (var id in expired)
                    if (_links.Remove(id)) unlinked.Add(id);
            foreach (var id in unlinked) RaiseLinkChanged(id);
        }
        foreach (var s in toDispose) await SafeDisposeAsync(s);
        foreach (var key in leftLive) RaiseLiveChanged(key); // idle/expired sessions left the live map
    }

    /// <summary>Close every live/in-flight session (e.g. on a project switch) but keep the manager usable —
    /// it can connect again for the next project. Lease-aware: a session a background query still holds is
    /// retired rather than disposed, so it leaves the live map immediately (nothing new attaches to it) and
    /// is freed at that query's last lease release. Shutdown goes through
    /// <see cref="DisposeAsync"/> instead, which ignores leases so a stuck query can't wedge quit.</summary>
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
        SessionKey[] allKeys;
        Guid[] linked;
        List<ConnectionSession> toDispose = new();
        lock (_gate)
        {
            if (retire) _disposed = true;
            all = _live.Values.ToArray();
            allKeys = _live.Keys.ToArray();
            linked = _links.ToArray();
            _live.Clear();
            _inflight.Clear();
            _schemaInflight.Clear();
            // A project switch parks tabs but genuinely closes the sessions, and shutdown ends everything —
            // neither leaves anything the link could still be evidence of. Unlike _schemaCache (kept, below),
            // a link is about a live conversation, not a catalog we already read.
            _links.Clear();
            // Kept across a project switch on purpose: switching back is meant to be cheap (tabs are parked,
            // not torn down), and re-reading a catalog we already have is exactly what this cache avoids.
            // On retire the app is going away, so drop it.
            if (retire) _schemaCache.Clear();
            foreach (var s in all)
            {
                // Shutdown tears everything down regardless of leases — a genuinely stuck query must not
                // wedge quit. A project switch honors them: a query started in the old project keeps running
                // on its already-open connection, and the retired session is freed at its last release.
                if (retire || s.LeaseCount == 0) toDispose.Add(s);
                else s.Retired = true;
            }
        }
        foreach (var s in toDispose) await SafeDisposeAsync(s);
        // On a project switch the manager stays usable, so tell listeners those sessions went away. On retire
        // (shutdown) skip it — the app is tearing down and handlers may already be gone.
        if (!retire)
        {
            foreach (var key in allKeys) RaiseLiveChanged(key);
            foreach (var id in linked) RaiseLinkChanged(id);
        }
    }

    private async Task<ConnectionSession> BuildAsync(ConnectionInfo info, CancellationToken ct)
    {
        // Yield so GetOrConnectAsync registers this task in _inflight before the body can finish
        // (a synchronously-completing provider would otherwise run the finally before registration).
        await Task.Yield();
        var key = SessionKey.For(info);
        try
        {
            // A live session may exist but be stale (settings edited) — retire it before rebuilding,
            // deferring its disposal if a query still holds a lease on it. A database switch no longer lands
            // here: it has its own key, so the other database's pool is left alone (#54).
            ConnectionSession? stale;
            var disposeStale = false;
            lock (_gate)
            {
                _live.TryGetValue(key, out stale);
                if (stale is not null && SameConnection(stale.Info, info)) { stale.LastUsedUtc = _clock(); return stale; }
                if (stale is not null)
                {
                    _live.Remove(key);
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
                throw new ConnectionFailedException(Describe(info, Bearing.Core.Data.SafeErrorText.Of(ex)), ex);
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
            bool newLink;
            lock (_gate)
            {
                // The manager may have been disposed while we were awaiting the connect. Don't repopulate
                // the cleared _live map — that would leak this session's factory (never disposed).
                disposedDuringConnect = _disposed;
                if (!disposedDuringConnect) _live[key] = session;
                // A completed handshake is the only thing that establishes a server link, and any one of its
                // databases establishes it: authenticating against `app` proves we can reach the server just
                // as well as authenticating against `reporting` would.
                newLink = !disposedDuringConnect && _links.Add(info.Id);
            }
            if (disposedDuringConnect)
            {
                await SafeDisposeAsync(session);
                throw new ObjectDisposedException(nameof(ConnectionSessionManager));
            }
            RaiseLiveChanged(key); // a new session entered the live map
            if (newLink) RaiseLinkChanged(info.Id);
            return session;
        }
        catch
        {
            // The handshake failed. Only unlink if no other database on this connection is still pooled:
            // a bad database name (dropped, or no CONNECT grant) must not report the *server* as down when
            // we are demonstrably still talking to it on another one.
            if (UnlinkIfNothingLive(info.Id)) RaiseLinkChanged(info.Id);
            throw;
        }
        finally
        {
            lock (_gate) _inflight.Remove(key);
        }
    }

    private async Task<ISchemaSnapshot?> LoadSchemaAsync(ConnectionSession session, CancellationToken ct)
    {
        try
        {
            var snapshot = await session.Metadata.LoadSnapshotAsync(session.Info.Database, ct);
            session.Snapshot = snapshot;
            lock (_gate) _schemaCache[session.Key] = snapshot;
            return snapshot;
        }
        finally
        {
            lock (_gate) _schemaInflight.Remove(session.Key);
        }
    }

    /// <summary>The failure message. The endpoint goes through <see cref="ConnectionEndpoint"/> because a
    /// SQL Server named instance has no meaningful port, and printing one next to <c>HOST\INSTANCE</c> sends
    /// the user to check a port that had nothing to do with the failure.</summary>
    private static string Describe(ConnectionInfo info, string detail)
        => $"Could not connect to '{info.Name}' ({ConnectionEndpoint.Of(info)}): {detail}";

    /// <summary>Compares only the fields that define the live connection; name/environment/color are cosmetic.
    /// Database is part of <see cref="SessionKey"/> and so always already equal at every call site — kept in
    /// the comparison so the predicate is true to its name if it is ever reused off the keyed path.
    /// <para>
    /// <see cref="ConnectionInfo.CredentialKind"/> belongs here because it decides <em>who</em> the pooled
    /// connection is authenticated as, not merely how the secret was fetched: the SQL Server factory
    /// branches on it to set <c>Integrated Security</c> and to omit the user name and password entirely.
    /// Without it, switching a live connection from a stored password to Windows authentication left the
    /// existing pool in place and every statement kept running as the old SQL login, while the dialog, the
    /// record and the beacon all said otherwise.
    /// </para></summary>
    private static bool SameConnection(ConnectionInfo a, ConnectionInfo b)
        => a.ProviderId == b.ProviderId && a.Host == b.Host && a.Port == b.Port
           && a.Database == b.Database && a.User == b.User
           && a.CredentialKind == b.CredentialKind && SameOptions(a.Options, b.Options);

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
