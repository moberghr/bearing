using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Xunit;

namespace Bearing.App.Tests;

public class ConnectionSessionManagerTests
{
    private static ConnectionInfo Conn(Guid id, string host = "h", int port = 5432, string db = "app")
        => new() { Id = id, Name = "c", ProviderId = "postgres", Host = host, Port = port, Database = db, User = "u" };

    [Fact]
    public async Task Builds_once_and_reuses_for_same_settings()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null);
        var info = Conn(Guid.NewGuid());

        var a = await mgr.GetOrConnectAsync(info, CancellationToken.None);
        var b = await mgr.GetOrConnectAsync(info, CancellationToken.None);

        Assert.Same(a, b);
        Assert.Equal(1, provider.FactoriesCreated);
        Assert.Same(a, mgr.TryGet(SessionKey.For(info)));
    }

    [Fact]
    public async Task Concurrent_first_connects_single_flight_to_one_factory()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null);
        var info = Conn(Guid.NewGuid());

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => mgr.GetOrConnectAsync(info, CancellationToken.None)));

        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(1, provider.FactoriesCreated);
    }

    [Fact]
    public async Task Changed_settings_rebuild_and_dispose_the_stale_session()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null);
        var id = Guid.NewGuid();

        var first = await mgr.GetOrConnectAsync(Conn(id, host: "old-host"), CancellationToken.None);
        var second = await mgr.GetOrConnectAsync(Conn(id, host: "new-host"), CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.Equal(2, provider.FactoriesCreated);
        Assert.Equal(1, ((FakeFactory)first.Factory).DisposeCount); // stale one disposed
        Assert.Equal("new-host", second.Info.Host);
    }

    // ---- one session per (connection, database) — #54 --------------------------------------------

    /// <summary>The bug: <c>_live</c> was keyed by connection id, so pointing a tab at another database on the
    /// same server looked like "settings changed" and threw the working pool away — its TCP connection and TLS
    /// handshake, and every bit of server-side state on it (temp tables, <c>SET</c> values, advisory locks,
    /// prepared statements). Two tabs on two databases rebuilt the pool on every switch, in both directions.</summary>
    [Fact]
    public async Task Connecting_a_second_database_leaves_the_first_ones_pool_alone()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();

        var alpha = await mgr.GetOrConnectAsync(Conn(id, db: "alpha"), CancellationToken.None);
        var beta = await mgr.GetOrConnectAsync(Conn(id, db: "beta"), CancellationToken.None);

        Assert.NotSame(alpha, beta);
        Assert.Equal(0, ((FakeFactory)alpha.Factory).DisposeCount);   // the whole point: it is still open
        Assert.Same(alpha, mgr.TryGet(new SessionKey(id, "alpha")));
        Assert.Same(beta, mgr.TryGet(new SessionKey(id, "beta")));
    }

    /// <summary>And so switching back is free — the pool is reused, not rebuilt. Two factories total for any
    /// number of switches between the two databases.</summary>
    [Fact]
    public async Task Switching_back_and_forth_between_databases_reuses_both_pools()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();

        var alpha = await mgr.GetOrConnectAsync(Conn(id, db: "alpha"), CancellationToken.None);
        var beta = await mgr.GetOrConnectAsync(Conn(id, db: "beta"), CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            Assert.Same(alpha, await mgr.GetOrConnectAsync(Conn(id, db: "alpha"), CancellationToken.None));
            Assert.Same(beta, await mgr.GetOrConnectAsync(Conn(id, db: "beta"), CancellationToken.None));
        }

        Assert.Equal(2, provider.FactoriesCreated);
    }

    /// <summary>A settings edit still rebuilds — the key narrowed, it did not stop noticing edits. And it
    /// rebuilds only the database it names, leaving the connection's other pools to be rebuilt on their own
    /// next use (the caller pairs this with <c>EvictConnectionAsync</c> when the edit was a network change).</summary>
    [Fact]
    public async Task An_edit_rebuilds_only_the_database_it_is_requested_for()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();

        var alpha = await mgr.GetOrConnectAsync(Conn(id, db: "alpha"), CancellationToken.None);
        var beta = await mgr.GetOrConnectAsync(Conn(id, db: "beta"), CancellationToken.None);

        var movedAlpha = await mgr.GetOrConnectAsync(Conn(id, host: "elsewhere", db: "alpha"), CancellationToken.None);

        Assert.NotSame(alpha, movedAlpha);
        Assert.Equal(1, ((FakeFactory)alpha.Factory).DisposeCount);
        Assert.Same(beta, mgr.TryGet(new SessionKey(id, "beta")));   // untouched
    }

    [Fact]
    public async Task Evict_drops_only_the_database_it_names()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();
        var alpha = await mgr.GetOrConnectAsync(Conn(id, db: "alpha"), CancellationToken.None);
        var beta = await mgr.GetOrConnectAsync(Conn(id, db: "beta"), CancellationToken.None);

        await mgr.EvictAsync(new SessionKey(id, "alpha"));

        Assert.Null(mgr.TryGet(new SessionKey(id, "alpha")));
        Assert.Equal(1, ((FakeFactory)alpha.Factory).DisposeCount);
        Assert.Same(beta, mgr.TryGet(new SessionKey(id, "beta")));
        Assert.True(mgr.IsAnyLive(id));   // the server is still open, on its other database
    }

    /// <summary>What the toolbar Disconnect means: the server, not one of its databases. The schema tree's
    /// server row lights for any live session on the connection, so leaving one behind would show a linked
    /// chain immediately after the user pressed Disconnect.</summary>
    [Fact]
    public async Task EvictConnection_drops_every_database_on_that_connection_and_no_others()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var alpha = await mgr.GetOrConnectAsync(Conn(id, db: "alpha"), CancellationToken.None);
        var beta = await mgr.GetOrConnectAsync(Conn(id, db: "beta"), CancellationToken.None);
        var elsewhere = await mgr.GetOrConnectAsync(Conn(other, db: "alpha"), CancellationToken.None);

        await mgr.EvictConnectionAsync(id);

        Assert.False(mgr.IsAnyLive(id));
        Assert.Equal(1, ((FakeFactory)alpha.Factory).DisposeCount);
        Assert.Equal(1, ((FakeFactory)beta.Factory).DisposeCount);
        Assert.Same(elsewhere, mgr.TryGet(new SessionKey(other, "alpha")));   // a different connection
    }

    [Fact]
    public async Task EvictConnection_defers_disposal_of_a_leased_database_but_still_unlists_it()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();
        var lease = await mgr.AcquireAsync(Conn(id, db: "alpha"), CancellationToken.None);
        var factory = (FakeFactory)lease.Session.Factory;
        await mgr.GetOrConnectAsync(Conn(id, db: "beta"), CancellationToken.None);

        await mgr.EvictConnectionAsync(id);

        Assert.False(mgr.IsAnyLive(id));         // nothing new attaches to either
        Assert.Equal(0, factory.DisposeCount);   // …but the running query keeps its connection

        lease.Dispose();
        Assert.Equal(1, factory.DisposeCount);
    }

    [Fact]
    public async Task IsAnyLive_is_false_for_a_connection_that_was_never_opened()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        await mgr.GetOrConnectAsync(Conn(Guid.NewGuid()), CancellationToken.None);

        Assert.False(mgr.IsAnyLive(Guid.NewGuid()));
    }

    [Fact]
    public async Task Failed_test_disposes_factory_and_throws_connection_failed()
    {
        var provider = new FakeProvider { TestResult = false };
        await using var mgr = new ConnectionSessionManager(provider, () => null);
        var info = Conn(Guid.NewGuid());

        await Assert.ThrowsAsync<ConnectionFailedException>(
            () => mgr.GetOrConnectAsync(info, CancellationToken.None));

        Assert.Null(mgr.TryGet(SessionKey.For(info)));
        Assert.Equal(1, provider.FactoriesCreated);
    }

    [Fact]
    public async Task Throwing_test_is_wrapped_and_leaves_no_live_session()
    {
        var provider = new FakeProvider { TestThrows = new InvalidOperationException("boom") };
        await using var mgr = new ConnectionSessionManager(provider, () => null);
        var info = Conn(Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<ConnectionFailedException>(
            () => mgr.GetOrConnectAsync(info, CancellationToken.None));
        Assert.Contains("boom", ex.Message);
        Assert.Null(mgr.TryGet(SessionKey.For(info)));
    }

    [Fact]
    public async Task Fetches_password_by_connection_id()
    {
        var provider = new FakeProvider();
        var secrets = new FakeSecretStore();
        var info = Conn(Guid.NewGuid());
        await secrets.SetPasswordAsync(info.Id, "hunter2", CancellationToken.None);

        var resolver = new CredentialResolver(() => secrets, null, new ThrowingEntraTokens());
        await using var mgr = new ConnectionSessionManager(provider, () => resolver);
        await mgr.GetOrConnectAsync(info, CancellationToken.None);

        Assert.Contains(info.Id, secrets.Fetched);
        Assert.Equal("hunter2", provider.LastPassword);
    }

    [Fact]
    public async Task Ensure_schema_loads_once_and_caches_snapshot()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null);
        var session = await mgr.GetOrConnectAsync(Conn(Guid.NewGuid()), CancellationToken.None);

        var s1 = await mgr.EnsureSchemaAsync(session, CancellationToken.None);
        var s2 = await mgr.EnsureSchemaAsync(session, CancellationToken.None);

        Assert.NotNull(s1);
        Assert.Same(s1, s2);
        Assert.Same(s1, session.Snapshot);
        Assert.Equal(1, ((FakeMetadata)session.Metadata).LoadCount);
    }

    [Fact]
    public async Task LiveChanged_fires_when_a_session_is_created_and_evicted()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var info = Conn(Guid.NewGuid());
        var events = new List<SessionKey>();
        mgr.LiveChanged += events.Add;

        await mgr.GetOrConnectAsync(info, CancellationToken.None);  // enters the live map
        await mgr.EvictAsync(SessionKey.For(info));                 // leaves it

        Assert.Equal(new[] { SessionKey.For(info), SessionKey.For(info) }, events);
    }

    /// <summary>The event has to name the database, not just the connection: one connection can have several
    /// live sessions now, so "connection X changed" would not say which pool moved.</summary>
    [Fact]
    public async Task LiveChanged_names_the_database_whose_session_moved()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();
        await mgr.GetOrConnectAsync(Conn(id, db: "alpha"), CancellationToken.None);
        await mgr.GetOrConnectAsync(Conn(id, db: "beta"), CancellationToken.None);

        var events = new List<SessionKey>();
        mgr.LiveChanged += events.Add;
        await mgr.EvictAsync(new SessionKey(id, "beta"));

        Assert.Equal(new[] { new SessionKey(id, "beta") }, events);
    }

    [Fact]
    public async Task Evict_disposes_and_forgets_the_session()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null);
        var info = Conn(Guid.NewGuid());
        var session = await mgr.GetOrConnectAsync(info, CancellationToken.None);

        await mgr.EvictAsync(SessionKey.For(info));

        Assert.Null(mgr.TryGet(SessionKey.For(info)));
        Assert.Equal(1, ((FakeFactory)session.Factory).DisposeCount);

        // Next request rebuilds.
        await mgr.GetOrConnectAsync(info, CancellationToken.None);
        Assert.Equal(2, provider.FactoriesCreated);
    }

    [Fact]
    public async Task Evict_defers_disposal_while_a_lease_is_held_then_disposes_on_release()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var info = Conn(Guid.NewGuid());

        var lease = await mgr.AcquireAsync(info, CancellationToken.None);
        var factory = (FakeFactory)lease.Session.Factory;

        // Evicting mid-query must NOT tear down the pool the running query is using.
        await mgr.EvictAsync(SessionKey.For(info));
        Assert.Null(mgr.TryGet(SessionKey.For(info)));            // gone from the live map…
        Assert.Equal(0, factory.DisposeCount);       // …but still alive under the lease

        // Releasing the lease disposes the now-retired session.
        lease.Dispose();
        Assert.Equal(1, factory.DisposeCount);
    }

    /// <summary>Switching database while a query runs used to be the dangerous case, because the switch
    /// rebuilt the session. It no longer does — the other database has its own key — so the running query's
    /// pool is not merely deferred past disposal, it is never retired at all and outlives the switch.</summary>
    [Fact]
    public async Task A_database_switch_mid_query_leaves_the_running_querys_session_live()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();

        // Query running against db "old".
        var running = await mgr.AcquireAsync(Conn(id, db: "old"), CancellationToken.None);
        var oldFactory = (FakeFactory)running.Session.Factory;

        var next = await mgr.GetOrConnectAsync(Conn(id, db: "new"), CancellationToken.None);
        Assert.NotSame(running.Session, next);
        Assert.Equal(0, oldFactory.DisposeCount);
        Assert.Equal("old", running.Session.Info.Database);            // query still sees its own db
        Assert.Same(running.Session, mgr.TryGet(new SessionKey(id, "old")));   // and it is still listed

        running.Dispose();
        Assert.Equal(0, oldFactory.DisposeCount);   // releasing a live (never-retired) session keeps it pooled
    }

    /// <summary>The deferred-disposal path a rebuild still takes: a settings edit mid-query retires the old
    /// session rather than pulling the pool out from under it.</summary>
    [Fact]
    public async Task An_edit_mid_query_defers_disposal_until_the_query_releases()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();

        var running = await mgr.AcquireAsync(Conn(id, host: "old-host"), CancellationToken.None);
        var oldFactory = (FakeFactory)running.Session.Factory;

        var next = await mgr.GetOrConnectAsync(Conn(id, host: "new-host"), CancellationToken.None);
        Assert.NotSame(running.Session, next);
        Assert.Equal(0, oldFactory.DisposeCount);

        running.Dispose();
        Assert.Equal(1, oldFactory.DisposeCount);
    }

    [Fact]
    public async Task Idle_sweep_closes_unleased_sessions_past_the_timeout_but_keeps_leased_ones()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(
            provider, () => null, idleTimeout: TimeSpan.FromMinutes(30), clock: () => now, runSweepTimer: false);

        var idleInfo = Conn(Guid.NewGuid());
        var busyInfo = Conn(Guid.NewGuid());
        var idle = await mgr.GetOrConnectAsync(idleInfo, CancellationToken.None);
        var busyLease = await mgr.AcquireAsync(busyInfo, CancellationToken.None);

        // Not yet past the timeout → nothing swept.
        now = now.AddMinutes(29);
        await mgr.SweepIdleAsync();
        Assert.NotNull(mgr.TryGet(SessionKey.For(idleInfo)));

        // Past the timeout: the idle session is closed; the leased one is spared despite being idle.
        now = now.AddMinutes(2);
        await mgr.SweepIdleAsync();
        Assert.Null(mgr.TryGet(SessionKey.For(idleInfo)));
        Assert.Equal(1, ((FakeFactory)idle.Factory).DisposeCount);
        Assert.NotNull(mgr.TryGet(SessionKey.For(busyInfo)));
        Assert.Equal(0, ((FakeFactory)busyLease.Session.Factory).DisposeCount);

        busyLease.Dispose();
    }

    [Fact]
    public async Task Using_a_session_refreshes_its_idle_clock()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(
            provider, () => null, idleTimeout: TimeSpan.FromMinutes(30), clock: () => now, runSweepTimer: false);
        var info = Conn(Guid.NewGuid());
        await mgr.GetOrConnectAsync(info, CancellationToken.None);

        now = now.AddMinutes(25);
        await mgr.GetOrConnectAsync(info, CancellationToken.None); // reuse bumps LastUsed
        now = now.AddMinutes(25);                                   // 25 min since last use (< 30)
        await mgr.SweepIdleAsync();
        Assert.NotNull(mgr.TryGet(SessionKey.For(info)));                        // not swept — activity kept it warm
    }

    [Fact]
    public async Task Sweep_disconnects_a_session_whose_credential_nears_expiry()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var expiry = new DateTimeOffset(now).AddMinutes(10);
        var provider = new FakeProvider();
        var tokens = new FakeEntraTokens(_ => new Credential("tok", expiry));
        var resolver = new CredentialResolver(() => null, null, tokens, () => new DateTimeOffset(now));
        await using var mgr = new ConnectionSessionManager(
            provider, () => resolver, idleTimeout: TimeSpan.FromMinutes(30), clock: () => now, runSweepTimer: false);
        var info = Conn(Guid.NewGuid()) with { CredentialKind = CredentialKind.EntraToken };

        var session = await mgr.GetOrConnectAsync(info, CancellationToken.None);
        Assert.Equal(expiry, session.CredentialExpiresAt);

        // Comfortably before expiry → not swept.
        now = now.AddMinutes(5);
        await mgr.SweepIdleAsync();
        Assert.NotNull(mgr.TryGet(SessionKey.For(info)));

        // Within the 90 s eviction skew of the 10-min expiry → disconnected, and the cached token dropped.
        now = now.AddMinutes(4); // +9 min total
        await mgr.SweepIdleAsync();
        Assert.Null(mgr.TryGet(SessionKey.For(info)));
        Assert.Equal(1, ((FakeFactory)session.Factory).DisposeCount);

        // Next connect re-mints (the resolver was invalidated on eviction).
        await mgr.GetOrConnectAsync(info, CancellationToken.None);
        Assert.Equal(2, tokens.Calls);
    }

    [Fact]
    public async Task Sweep_retires_a_leased_expiring_session_but_defers_disposal_until_release()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var expiry = new DateTimeOffset(now).AddMinutes(5);
        var provider = new FakeProvider();
        var tokens = new FakeEntraTokens(_ => new Credential("tok", expiry));
        var resolver = new CredentialResolver(() => null, null, tokens, () => new DateTimeOffset(now));
        await using var mgr = new ConnectionSessionManager(
            provider, () => resolver, idleTimeout: TimeSpan.FromMinutes(30), clock: () => now, runSweepTimer: false);
        var info = Conn(Guid.NewGuid()) with { CredentialKind = CredentialKind.EntraToken };

        var lease = await mgr.AcquireAsync(info, CancellationToken.None);
        var factory = (FakeFactory)lease.Session.Factory;

        now = now.AddMinutes(5); // at expiry, within skew
        await mgr.SweepIdleAsync();
        Assert.Null(mgr.TryGet(SessionKey.For(info)));        // left the live map…
        Assert.Equal(0, factory.DisposeCount);   // …but the running query keeps its connection

        lease.Dispose();
        Assert.Equal(1, factory.DisposeCount);   // freed at last release
    }

    [Fact]
    public async Task Dispose_disposes_all_sessions_exactly_once()
    {
        var provider = new FakeProvider();
        var mgr = new ConnectionSessionManager(provider, () => null);
        var a = await mgr.GetOrConnectAsync(Conn(Guid.NewGuid()), CancellationToken.None);
        var b = await mgr.GetOrConnectAsync(Conn(Guid.NewGuid()), CancellationToken.None);

        await mgr.DisposeAsync();

        Assert.Equal(1, ((FakeFactory)a.Factory).DisposeCount);
        Assert.Equal(1, ((FakeFactory)b.Factory).DisposeCount);
        Assert.Null(mgr.TryGet(a.Key));
    }

    [Fact]
    public async Task CloseAll_disposes_sessions_but_leaves_the_manager_usable()
    {
        // Project switch resets the shared manager and then reuses it for the next project.
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null);
        var info = Conn(Guid.NewGuid());
        var first = await mgr.GetOrConnectAsync(info, CancellationToken.None);

        await mgr.CloseAllAsync();

        Assert.Equal(1, ((FakeFactory)first.Factory).DisposeCount); // old session disposed
        Assert.Null(mgr.TryGet(SessionKey.For(info)));                           // cache cleared

        var second = await mgr.GetOrConnectAsync(info, CancellationToken.None); // reconnects — does not throw
        Assert.NotSame(first, second);
        Assert.Equal(2, provider.FactoriesCreated);                 // a fresh factory was built
    }

    [Fact]
    public async Task Dispose_retires_the_manager_so_further_connects_throw()
    {
        var provider = new FakeProvider();
        var mgr = new ConnectionSessionManager(provider, () => null);
        await mgr.GetOrConnectAsync(Conn(Guid.NewGuid()), CancellationToken.None);

        await mgr.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => mgr.GetOrConnectAsync(Conn(Guid.NewGuid()), CancellationToken.None));
    }

    /// <summary>
    /// A schema load in flight for one database must not be handed to a session on another. The in-flight
    /// schema map was once keyed by connection id alone (§9.4), so a second database's session joined the
    /// first's load and adopted the *wrong* database's snapshot — which is what decides editability and FK
    /// targets. Both sessions now coexist, so this is the ordinary case rather than a switch.
    /// </summary>
    [Fact]
    public async Task A_schema_load_in_flight_for_one_database_is_not_reused_for_another()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider { MetadataGate = gate };
        await using var mgr = new ConnectionSessionManager(provider, () => null);
        var id = Guid.NewGuid();

        var oldSession = await mgr.GetOrConnectAsync(Conn(id, db: "old"), CancellationToken.None);
        var loadOld = mgr.EnsureSchemaAsync(oldSession, CancellationToken.None);   // held by the gate

        // Same connection, different database → new session, and it must start its own load.
        var newSession = await mgr.GetOrConnectAsync(Conn(id, db: "new"), CancellationToken.None);
        var loadNew = mgr.EnsureSchemaAsync(newSession, CancellationToken.None);
        Assert.NotSame(loadOld, loadNew);

        gate.SetResult();

        Assert.Equal("new", (await loadNew)!.Database);
        Assert.Equal("old", (await loadOld)!.Database);
    }

    /// <summary>Two callers on the *same* session still share one load — the fix narrows the key, it doesn't
    /// give up single-flighting.</summary>
    [Fact]
    public async Task Concurrent_schema_loads_for_the_same_session_still_share_one_read()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider { MetadataGate = gate };
        await using var mgr = new ConnectionSessionManager(provider, () => null);
        var session = await mgr.GetOrConnectAsync(Conn(Guid.NewGuid()), CancellationToken.None);

        var a = mgr.EnsureSchemaAsync(session, CancellationToken.None);
        var b = mgr.EnsureSchemaAsync(session, CancellationToken.None);
        Assert.Same(a, b);

        gate.SetResult();
        Assert.Equal("app", (await a)!.Database);
        Assert.Equal(1, ((FakeMetadata)session.Metadata).LoadCount);
    }

    // ---- server links: "connected to the server" vs "this database's pool is warm" ------------------

    /// <summary>A pool is per (connection, database) because Postgres binds one at startup, but the user's
    /// question is about the server. Any database's completed handshake is evidence for the whole server.</summary>
    [Fact]
    public async Task Any_databases_handshake_links_the_server()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();
        Assert.False(mgr.IsLinked(id));

        await mgr.GetOrConnectAsync(Conn(id, db: "alpha"), CancellationToken.None);

        Assert.True(mgr.IsLinked(id));
        Assert.False(mgr.IsLinked(Guid.NewGuid()));   // and only that server
    }

    /// <summary>The point of the split: reclaiming pools is not disconnecting. Npgsql has already pruned the
    /// idle physical connections by then, so the sweep is reclaiming bookkeeping — and telling the user they
    /// were disconnected while they sat reading a result set is the surprise this removes.</summary>
    [Fact]
    public async Task Idle_sweep_reclaims_the_pool_but_keeps_the_server_link()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(
            provider, () => null, idleTimeout: TimeSpan.FromMinutes(30), clock: () => now, runSweepTimer: false);
        var info = Conn(Guid.NewGuid());
        var links = new List<Guid>();
        await mgr.GetOrConnectAsync(info, CancellationToken.None);
        mgr.LinkChanged += links.Add;

        now = now.AddMinutes(31);
        await mgr.SweepIdleAsync();

        Assert.Null(mgr.TryGet(SessionKey.For(info)));   // pool gone
        Assert.True(mgr.IsLinked(info.Id));              // still connected
        Assert.Empty(links);                             // and nothing told the UI otherwise
    }

    /// <summary>An expiring credential is the opposite case: the thing the link was evidence of has gone
    /// stale, and the rebuild has to re-mint or re-prompt, so the chain genuinely breaks.</summary>
    [Fact]
    public async Task Sweep_unlinks_the_server_when_the_credential_expires()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var expiry = new DateTimeOffset(now).AddMinutes(10);
        var provider = new FakeProvider();
        var tokens = new FakeEntraTokens(_ => new Credential("tok", expiry));
        var resolver = new CredentialResolver(() => null, null, tokens, () => new DateTimeOffset(now));
        await using var mgr = new ConnectionSessionManager(
            provider, () => resolver, idleTimeout: TimeSpan.FromMinutes(30), clock: () => now, runSweepTimer: false);
        var info = Conn(Guid.NewGuid()) with { CredentialKind = CredentialKind.EntraToken };
        await mgr.GetOrConnectAsync(info, CancellationToken.None);
        var links = new List<Guid>();
        mgr.LinkChanged += links.Add;

        now = now.AddMinutes(9);   // within the 90 s eviction skew
        await mgr.SweepIdleAsync();

        Assert.False(mgr.IsLinked(info.Id));
        Assert.Equal(new[] { info.Id }, links);
    }

    /// <summary>Disconnect is a server-level teardown, so it must work when the sweep has already emptied the
    /// pools underneath the link — dropping the link only as a side effect of removing the last live session
    /// would leave the button doing nothing at all in exactly that state.</summary>
    [Fact]
    public async Task Evicting_the_connection_unlinks_even_with_no_pools_left_to_evict()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(
            provider, () => null, idleTimeout: TimeSpan.FromMinutes(30), clock: () => now, runSweepTimer: false);
        var info = Conn(Guid.NewGuid());
        await mgr.GetOrConnectAsync(info, CancellationToken.None);
        now = now.AddMinutes(31);
        await mgr.SweepIdleAsync();
        Assert.True(mgr.IsLinked(info.Id));

        var links = new List<Guid>();
        mgr.LinkChanged += links.Add;
        await mgr.EvictConnectionAsync(info.Id);

        Assert.False(mgr.IsLinked(info.Id));
        Assert.Equal(new[] { info.Id }, links);
    }

    /// <summary>A targeted evict — a cancelled connect, or the teardown before a credential retry — is
    /// abandoning the attempt, so it unlinks once nothing is left standing behind the claim.</summary>
    [Fact]
    public async Task Evicting_the_last_database_unlinks_but_evicting_one_of_two_does_not()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();
        await mgr.GetOrConnectAsync(Conn(id, db: "alpha"), CancellationToken.None);
        await mgr.GetOrConnectAsync(Conn(id, db: "beta"), CancellationToken.None);

        await mgr.EvictAsync(new SessionKey(id, "beta"));
        Assert.True(mgr.IsLinked(id));    // alpha still stands behind it

        await mgr.EvictAsync(new SessionKey(id, "alpha"));
        Assert.False(mgr.IsLinked(id));
    }

    /// <summary>A database that can't be opened (dropped, or no CONNECT grant) must not report the whole
    /// server as down while we are demonstrably still talking to it on another one.</summary>
    [Fact]
    public async Task A_failed_connect_on_one_database_keeps_a_link_another_database_still_evidences()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();
        await mgr.GetOrConnectAsync(Conn(id, db: "alpha"), CancellationToken.None);

        provider.TestResult = false;
        await Assert.ThrowsAsync<ConnectionFailedException>(
            () => mgr.GetOrConnectAsync(Conn(id, db: "gone"), CancellationToken.None));

        Assert.True(mgr.IsLinked(id));
        Assert.NotNull(mgr.TryGet(new SessionKey(id, "alpha")));
    }

    /// <summary>With nothing else open, a failed handshake leaves no link — the first connect never
    /// established one, and a later failure has to clear one an earlier success left behind.</summary>
    [Fact]
    public async Task A_failed_connect_with_nothing_else_open_leaves_the_server_unlinked()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var info = Conn(Guid.NewGuid());
        await mgr.GetOrConnectAsync(info, CancellationToken.None);
        await mgr.EvictConnectionAsync(info.Id);

        provider.TestResult = false;
        await Assert.ThrowsAsync<ConnectionFailedException>(
            () => mgr.GetOrConnectAsync(info, CancellationToken.None));

        Assert.False(mgr.IsLinked(info.Id));
    }

    /// <summary>LinkChanged is the coarse event: a second database opening on an already-linked server has
    /// moved no user-visible state, so it must stay quiet — unlike LiveChanged, which fires per pool.</summary>
    [Fact]
    public async Task LinkChanged_fires_once_per_server_not_once_per_pool()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();
        var links = new List<Guid>();
        var live = new List<SessionKey>();
        mgr.LinkChanged += links.Add;
        mgr.LiveChanged += live.Add;

        await mgr.GetOrConnectAsync(Conn(id, db: "alpha"), CancellationToken.None);
        await mgr.GetOrConnectAsync(Conn(id, db: "beta"), CancellationToken.None);

        Assert.Equal(new[] { id }, links);
        Assert.Equal(2, live.Count);

        await mgr.EvictConnectionAsync(id);
        Assert.Equal(new[] { id, id }, links);   // one gain, one loss
    }

    /// <summary>A project switch closes the sessions for real, so the links go with them — unlike the schema
    /// cache, which is a catalog we already read and is deliberately kept.</summary>
    [Fact]
    public async Task Closing_all_sessions_unlinks_every_server()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var a = Conn(Guid.NewGuid());
        var b = Conn(Guid.NewGuid());
        await mgr.GetOrConnectAsync(a, CancellationToken.None);
        await mgr.GetOrConnectAsync(b, CancellationToken.None);
        var links = new List<Guid>();
        mgr.LinkChanged += links.Add;

        await mgr.CloseAllAsync();

        Assert.False(mgr.IsLinked(a.Id));
        Assert.False(mgr.IsLinked(b.Id));
        Assert.Equal(2, links.Count);
    }
}
