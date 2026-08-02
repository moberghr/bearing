using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squirrel.App.Connections;
using Squirrel.Core.Data;
using Squirrel.Core.Workspace;
using Xunit;

namespace Squirrel.App.Tests;

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
        Assert.Same(a, mgr.TryGet(info.Id));
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

        var first = await mgr.GetOrConnectAsync(Conn(id, db: "old"), CancellationToken.None);
        var second = await mgr.GetOrConnectAsync(Conn(id, db: "new"), CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.Equal(2, provider.FactoriesCreated);
        Assert.Equal(1, ((FakeFactory)first.Factory).DisposeCount); // stale one disposed
        Assert.Equal("new", second.Info.Database);
    }

    [Fact]
    public async Task Failed_test_disposes_factory_and_throws_connection_failed()
    {
        var provider = new FakeProvider { TestResult = false };
        await using var mgr = new ConnectionSessionManager(provider, () => null);
        var info = Conn(Guid.NewGuid());

        await Assert.ThrowsAsync<ConnectionFailedException>(
            () => mgr.GetOrConnectAsync(info, CancellationToken.None));

        Assert.Null(mgr.TryGet(info.Id));
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
        Assert.Null(mgr.TryGet(info.Id));
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
        var events = new List<Guid>();
        mgr.LiveChanged += events.Add;

        await mgr.GetOrConnectAsync(info, CancellationToken.None); // enters the live map
        await mgr.EvictAsync(info.Id);                              // leaves it

        Assert.Equal(new[] { info.Id, info.Id }, events);
    }

    [Fact]
    public async Task Evict_disposes_and_forgets_the_session()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null);
        var info = Conn(Guid.NewGuid());
        var session = await mgr.GetOrConnectAsync(info, CancellationToken.None);

        await mgr.EvictAsync(info.Id);

        Assert.Null(mgr.TryGet(info.Id));
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
        await mgr.EvictAsync(info.Id);
        Assert.Null(mgr.TryGet(info.Id));            // gone from the live map…
        Assert.Equal(0, factory.DisposeCount);       // …but still alive under the lease

        // Releasing the lease disposes the now-retired session.
        lease.Dispose();
        Assert.Equal(1, factory.DisposeCount);
    }

    [Fact]
    public async Task Database_switch_rebuild_does_not_dispose_the_session_a_query_still_holds()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();

        // Query running against db "old".
        var running = await mgr.AcquireAsync(Conn(id, db: "old"), CancellationToken.None);
        var oldFactory = (FakeFactory)running.Session.Factory;

        // Switch to db "new" on the same id → rebuild. The old session must survive until the query ends.
        var next = await mgr.GetOrConnectAsync(Conn(id, db: "new"), CancellationToken.None);
        Assert.NotSame(running.Session, next);
        Assert.Equal(0, oldFactory.DisposeCount);
        Assert.Equal("old", running.Session.Info.Database); // query still sees its own db

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
        Assert.NotNull(mgr.TryGet(idleInfo.Id));

        // Past the timeout: the idle session is closed; the leased one is spared despite being idle.
        now = now.AddMinutes(2);
        await mgr.SweepIdleAsync();
        Assert.Null(mgr.TryGet(idleInfo.Id));
        Assert.Equal(1, ((FakeFactory)idle.Factory).DisposeCount);
        Assert.NotNull(mgr.TryGet(busyInfo.Id));
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
        Assert.NotNull(mgr.TryGet(info.Id));                        // not swept — activity kept it warm
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
        Assert.NotNull(mgr.TryGet(info.Id));

        // Within the 90 s eviction skew of the 10-min expiry → disconnected, and the cached token dropped.
        now = now.AddMinutes(4); // +9 min total
        await mgr.SweepIdleAsync();
        Assert.Null(mgr.TryGet(info.Id));
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
        Assert.Null(mgr.TryGet(info.Id));        // left the live map…
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
        Assert.Null(mgr.TryGet(a.ConnectionId));
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
        Assert.Null(mgr.TryGet(info.Id));                           // cache cleared

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
}
