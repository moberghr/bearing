using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Bearing.Sessions;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// What saving a connection's safety settings (#99 / #105) does, and — just as much — what it does not.
/// <para>
/// The pool has to be rebuilt, because read-only and the statement timeout reach the server in the startup
/// packet and are fixed for the life of a pool. Nothing else should move: those settings say nothing about
/// which server this is or what is in its catalog, so the schema tree's node, its expansion and the
/// completion snapshot all have to survive. Folding the two questions into one predicate is how a review
/// found them moving together.
/// </para>
/// </summary>
public class ConnectionSafetySaveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-safety-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static ConnectionInfo Conn() => new()
    {
        Id = Guid.NewGuid(),
        Name = "prod",
        ProviderId = "postgres",
        Host = "db.example",
        Port = 5432,
        Database = "app",
        User = "u",
    };

    private (WorkspaceContext Ctx, ConnectionsViewModel Vm) NewVm(ConnectionInfo conn)
    {
        Directory.CreateDirectory(_root);
        var ctx = new WorkspaceContext(
            new FakeProvider(),
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore());
        var manifest = new ProjectManifest();
        manifest.Connections.Add(conn);
        ctx.Project = new Project { Directory = _root, Manifest = manifest };
        var vm = new ConnectionsViewModel(ctx);
        vm.RefreshConnections();
        return (ctx, vm);
    }

    private static async Task<ISchemaSnapshot?> WarmAsync(WorkspaceContext ctx, ConnectionInfo conn)
    {
        var session = await ctx.Sessions.GetOrConnectAsync(conn, CancellationToken.None);
        await ctx.Sessions.EnsureSchemaAsync(session, CancellationToken.None);
        return ctx.Sessions.TryGetSnapshot(conn.Id, conn.Database);
    }

    [Fact]
    public async Task Turning_read_only_on_rebuilds_the_pool()
    {
        // The pool is bound to the startup packet it was built with, and TryGet — which Fetch all rows, Count
        // total, paging and FK navigation all reach through — compares nothing. Without the evict, the result
        // already on screen keeps writing through a pool that is not read-only.
        var conn = Conn();
        var (ctx, vm) = NewVm(conn);
        await WarmAsync(ctx, conn);
        Assert.NotNull(ctx.Sessions.TryGet(SessionKey.For(conn)));

        await vm.AddOrUpdateConnectionAsync(conn with { ReadOnly = true }, password: null);

        Assert.Null(ctx.Sessions.TryGet(SessionKey.For(conn)));
    }

    [Fact]
    public async Task Changing_the_statement_timeout_rebuilds_the_pool()
    {
        var conn = Conn();
        var (ctx, vm) = NewVm(conn);
        await WarmAsync(ctx, conn);

        await vm.AddOrUpdateConnectionAsync(conn with { StatementTimeoutSeconds = 30 }, password: null);

        Assert.Null(ctx.Sessions.TryGet(SessionKey.For(conn)));
    }

    [Fact]
    public async Task Changing_the_session_time_zone_rebuilds_the_pool_and_keeps_the_schema()
    {
        // The zone rides the same startup packet (#163), so the pool on screen would otherwise go on computing
        // `::date` in the old zone while the status tip named the new one. It says nothing about the catalog.
        var conn = Conn() with { SessionTimeZone = "UTC" };
        var (ctx, vm) = NewVm(conn);
        Assert.NotNull(await WarmAsync(ctx, conn));

        await vm.AddOrUpdateConnectionAsync(conn with { SessionTimeZone = "Europe/Zagreb" }, password: null);

        Assert.Null(ctx.Sessions.TryGet(SessionKey.For(conn)));
        Assert.NotNull(ctx.Sessions.TryGetSnapshot(conn.Id, conn.Database));
    }

    [Theory]
    [InlineData("UTC", "time zone UTC")]
    [InlineData("server", "time zone the server's own zone")]
    public void The_status_tip_names_the_session_zone(string zone, string mark)
    {
        // What made #163 hard to find: the grid could show local time over SQL computed in UTC, and nothing
        // anywhere named the zone the session was in. It is named always, not only when set — every session
        // computes in some zone.
        var conn = Conn() with { SessionTimeZone = zone };
        var (ctx, vm) = NewVm(conn);
        var tab = new EditorTabViewModel("t1") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;

        Assert.EndsWith(" · " + mark, vm.StatusTip);
    }

    [Fact]
    public async Task Naming_the_zone_the_connection_already_runs_in_keeps_the_pool()
    {
        // Compared as the resolved zone, not the setting's text: "local" on a machine in UTC and an explicit
        // UTC send the same thing, so there is nothing to rebuild.
        var conn = Conn() with { SessionTimeZone = "server" };
        var (ctx, vm) = NewVm(conn);
        await WarmAsync(ctx, conn);

        await vm.AddOrUpdateConnectionAsync(conn with { SessionTimeZone = "SERVER" }, password: null);

        Assert.NotNull(ctx.Sessions.TryGet(SessionKey.For(conn)));
    }

    [Fact]
    public async Task A_safety_change_keeps_the_cached_schema()
    {
        // The snapshot describes the catalog, and neither setting touches the catalog. Dropping it would make
        // completion go dead and the tree reload for a change to a checkbox — and the snapshot deliberately
        // outlives sessions precisely so a mere disconnect does not cost it.
        var conn = Conn();
        var (ctx, vm) = NewVm(conn);
        Assert.NotNull(await WarmAsync(ctx, conn));

        await vm.AddOrUpdateConnectionAsync(conn with { ReadOnly = true }, password: null);

        Assert.NotNull(ctx.Sessions.TryGetSnapshot(conn.Id, conn.Database));
    }

    [Fact]
    public async Task Moving_the_connection_to_another_server_does_drop_the_cached_schema()
    {
        // The other half of the same rule: a changed network target means the cached catalog describes a
        // different server. Asserted alongside so the test above cannot pass by the schema never being
        // dropped at all.
        var conn = Conn();
        var (ctx, vm) = NewVm(conn);
        Assert.NotNull(await WarmAsync(ctx, conn));

        await vm.AddOrUpdateConnectionAsync(conn with { Host = "elsewhere.example" }, password: null);

        Assert.Null(ctx.Sessions.TryGetSnapshot(conn.Id, conn.Database));
    }

    [Fact]
    public async Task A_safety_change_keeps_the_tree_node_and_its_expansion()
    {
        // Rebuilding the node collapses the tree the user had opened. A statement timeout is not a reason to
        // lose their place.
        var conn = Conn();
        var (ctx, vm) = NewVm(conn);
        var before = vm.ServerNodes.OfType<ServerNodeViewModel>().Single();
        before.IsExpanded = true;

        await vm.AddOrUpdateConnectionAsync(conn with { StatementTimeoutSeconds = 30 }, password: null);

        var after = vm.ServerNodes.OfType<ServerNodeViewModel>().Single();
        Assert.Same(before, after);
        Assert.True(after.IsExpanded);
    }
}
