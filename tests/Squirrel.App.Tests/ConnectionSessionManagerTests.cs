using System;
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

        await using var mgr = new ConnectionSessionManager(provider, () => secrets);
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
}
