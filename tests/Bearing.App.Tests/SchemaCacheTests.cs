using System;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// A schema snapshot outlives the session that read it. Reported 2026-08-13: completion stopped working the
/// moment a connection went away, even though the catalog was already in memory — the snapshot was reachable
/// only through the live session (<c>TryGet(id)?.Snapshot</c>), so a disconnect, a credential expiry, or the
/// automatic idle sweep silently switched IntelliSense off with nothing on screen to explain it.
/// <para>
/// A snapshot has no connection dependency at all — it's an immutable set of tables/columns/FKs — so the fix
/// is lifetime, not plumbing. What needs pinning is the <b>direction</b>: kept across events that merely end
/// the conversation (disconnect, sweep), dropped only by events that make the catalog untrue (re-pointed at
/// another server, deleted, explicitly refreshed).
/// </para>
/// </summary>
public class SchemaCacheTests
{
    private static ConnectionInfo Conn(Guid id, string db = "app")
        => new() { Id = id, Name = "c", ProviderId = "postgres", Host = "h", Port = 5432, Database = db, User = "u" };

    private static async Task<(ConnectionSessionManager Mgr, ConnectionInfo Info)> Loaded(
        FakeProvider provider, string db = "app")
    {
        var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var info = Conn(Guid.NewGuid(), db);
        var session = await mgr.GetOrConnectAsync(info, CancellationToken.None);
        Assert.NotNull(await mgr.EnsureSchemaAsync(session, CancellationToken.None));
        return (mgr, info);
    }

    // ---- kept: the connection ended, the catalog didn't ------------------------------------------

    [Fact]
    public async Task A_snapshot_survives_an_evict_so_completion_works_while_disconnected()
    {
        var (mgr, info) = await Loaded(new FakeProvider());
        await using var _ = mgr;

        await mgr.EvictAsync(SessionKey.For(info));

        Assert.Null(mgr.TryGet(SessionKey.For(info)));               // no live session, as expected
        Assert.NotNull(mgr.TryGetSnapshot(info.Id, info.Database));  // but the schema is still here
    }

    [Fact]
    public async Task A_snapshot_survives_the_idle_sweep()
    {
        // The sweep is the case that actually bit: it fires on a timer, so completion died with no user action
        // and no way to connect cause to effect.
        var provider = new FakeProvider();
        var now = DateTime.UtcNow;
        await using var mgr = new ConnectionSessionManager(
            provider, () => null, idleTimeout: TimeSpan.FromMinutes(30), clock: () => now, runSweepTimer: false);
        var info = Conn(Guid.NewGuid());
        var session = await mgr.GetOrConnectAsync(info, CancellationToken.None);
        await mgr.EnsureSchemaAsync(session, CancellationToken.None);

        now = now.AddHours(2);
        await mgr.SweepIdleAsync();

        Assert.Null(mgr.TryGet(SessionKey.For(info)));
        Assert.NotNull(mgr.TryGetSnapshot(info.Id, info.Database));
    }

    [Fact]
    public async Task A_reconnect_adopts_the_cached_snapshot_instead_of_re_reading_the_catalog()
    {
        var provider = new FakeProvider();
        var (mgr, info) = await Loaded(provider);
        await using var _ = mgr;
        await mgr.EvictAsync(SessionKey.For(info));

        var again = await mgr.GetOrConnectAsync(info, CancellationToken.None);
        var snapshot = await mgr.EnsureSchemaAsync(again, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Same(mgr.TryGetSnapshot(info.Id, info.Database), snapshot);
        // Each session gets its own metadata reader, so this counts only what the *reconnected* one read.
        Assert.Equal(0, ((FakeMetadata)again.Metadata).LoadCount);
    }

    [Fact]
    public async Task A_project_switch_keeps_cached_schemas()
    {
        // Switching projects parks tabs rather than tearing them down, so switching back should be cheap —
        // re-reading a catalog we already have is exactly what this cache exists to avoid.
        var (mgr, info) = await Loaded(new FakeProvider());
        await using var _ = mgr;

        await mgr.CloseAllAsync();

        Assert.Null(mgr.TryGet(SessionKey.For(info)));
        Assert.NotNull(mgr.TryGetSnapshot(info.Id, info.Database));
    }

    // ---- dropped: the catalog is no longer the truth ---------------------------------------------

    [Fact]
    public async Task InvalidateSchema_drops_it()
    {
        var (mgr, info) = await Loaded(new FakeProvider());
        await using var _ = mgr;

        mgr.InvalidateSchema(info.Id);

        Assert.Null(mgr.TryGetSnapshot(info.Id, info.Database));
    }

    /// <summary>Also the regression test for the stale single-flight entry: <c>EnsureSchemaAsync</c> used to
    /// register a load that had already completed synchronously (its own <c>finally</c> having removed the key
    /// first), leaving a completed task in <c>_schemaInflight</c> that nothing cleared — after which every
    /// later call for that key was served the old snapshot instead of re-reading.</summary>
    [Fact]
    public async Task After_invalidating_the_next_load_really_re_reads()
    {
        var provider = new FakeProvider();
        var (mgr, info) = await Loaded(provider);
        await using var _ = mgr;
        mgr.InvalidateSchema(info.Id);
        await mgr.EvictAsync(SessionKey.For(info));

        var again = await mgr.GetOrConnectAsync(info, CancellationToken.None);
        await mgr.EnsureSchemaAsync(again, CancellationToken.None);

        Assert.Equal(1, ((FakeMetadata)again.Metadata).LoadCount);
        Assert.NotNull(mgr.TryGetSnapshot(info.Id, info.Database));   // and it repopulates
    }

    // ---- keyed by connection AND database -------------------------------------------------------

    [Fact]
    public async Task Two_databases_on_one_connection_keep_their_own_snapshots()
    {
        // Sessions and snapshots are both keyed by connection+database (§9.4), so the two coexist rather than
        // one overwriting the other — which matters because a snapshot drives editability and FK navigation,
        // not just the popup.
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();

        var a = await mgr.GetOrConnectAsync(Conn(id, "alpha"), CancellationToken.None);
        await mgr.EnsureSchemaAsync(a, CancellationToken.None);
        var b = await mgr.GetOrConnectAsync(Conn(id, "beta"), CancellationToken.None);
        await mgr.EnsureSchemaAsync(b, CancellationToken.None);

        Assert.Equal("alpha", Assert.IsType<FakeSnapshot>(mgr.TryGetSnapshot(id, "alpha")).Database);
        Assert.Equal("beta", Assert.IsType<FakeSnapshot>(mgr.TryGetSnapshot(id, "beta")).Database);
    }

    [Fact]
    public async Task Invalidating_a_connection_drops_every_database_it_had_cached()
    {
        var provider = new FakeProvider();
        await using var mgr = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var id = Guid.NewGuid();
        var a = await mgr.GetOrConnectAsync(Conn(id, "alpha"), CancellationToken.None);
        await mgr.EnsureSchemaAsync(a, CancellationToken.None);
        var b = await mgr.GetOrConnectAsync(Conn(id, "beta"), CancellationToken.None);
        await mgr.EnsureSchemaAsync(b, CancellationToken.None);

        mgr.InvalidateSchema(id);

        Assert.Null(mgr.TryGetSnapshot(id, "alpha"));
        Assert.Null(mgr.TryGetSnapshot(id, "beta"));
    }

    [Fact]
    public async Task A_connection_never_read_has_no_snapshot()
    {
        await using var mgr = new ConnectionSessionManager(new FakeProvider(), () => null, runSweepTimer: false);

        Assert.Null(mgr.TryGetSnapshot(Guid.NewGuid(), "app"));
    }
}
