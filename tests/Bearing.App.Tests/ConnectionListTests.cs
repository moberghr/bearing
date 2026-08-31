using System;
using System.IO;
using System.Linq;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The side pane's connection list: the filter box, and the reconciling refresh that keeps a server row —
/// and everything it has loaded — across an edit or a filter change. Both behaviours are invisible to the
/// build and untestable through the UI (§4.3), which is exactly why they are asserted on the view model.
/// </summary>
public class ConnectionListTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-connlist", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private static ConnectionInfo Conn(string name, string host = "localhost", int port = 5432,
                                       string database = "app", string? environment = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProviderId = "postgres",
            Host = host,
            Port = port,
            Database = database,
            Environment = environment,
        };

    private (WorkspaceContext ctx, ConnectionsViewModel vm) NewVm(params ConnectionInfo[] connections)
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
        manifest.Connections.AddRange(connections);
        ctx.Project = new Project { Directory = _root, Manifest = manifest };
        var vm = new ConnectionsViewModel(ctx);
        vm.RefreshConnections();
        return (ctx, vm);
    }

    // ---- reconciling refresh -------------------------------------------------------------------

    [Fact]
    public void Refresh_keeps_the_same_node_for_an_unchanged_connection()
    {
        var (_, vm) = NewVm(Conn("prod"));
        var before = vm.ServerNodes.Single();

        vm.RefreshConnections();

        Assert.Same(before, vm.ServerNodes.Single());
    }

    [Fact]
    public void A_rename_relabels_the_node_instead_of_replacing_it()
    {
        var conn = Conn("prod");
        var (ctx, vm) = NewVm(conn);
        var before = vm.ServerNodes.Single();
        before.IsExpanded = true;

        ctx.Project!.Manifest.Connections[0] = conn with { Name = "production" };
        vm.RefreshConnections();

        var after = vm.ServerNodes.Single();
        Assert.Same(before, after);          // the tree does not collapse and the databases survive
        Assert.True(after.IsExpanded);
        Assert.Equal("production", after.Title);
    }

    [Fact]
    public void Changing_the_server_replaces_the_node()
    {
        var conn = Conn("prod", port: 5432);
        var (ctx, vm) = NewVm(conn);
        var before = vm.ServerNodes.Single();

        ctx.Project!.Manifest.Connections[0] = conn with { Port = 5434 };
        vm.RefreshConnections();

        // Its children describe a machine it no longer talks to, so the node is not reused.
        Assert.NotSame(before, vm.ServerNodes.Single());
        Assert.Equal("localhost:5434", vm.ServerNodes.Single().Detail);
    }

    [Fact]
    public void The_server_row_shows_the_port()
    {
        var (_, vm) = NewVm(Conn("test", port: 5434));
        Assert.Equal("localhost:5434", vm.ServerNodes.Single().Detail);
    }

    [Fact]
    public void A_deleted_connection_drops_its_node()
    {
        var (ctx, vm) = NewVm(Conn("a"), Conn("b"));
        ctx.Project!.Manifest.Connections.RemoveAt(0);

        vm.RefreshConnections();

        Assert.Equal("b", vm.ServerNodes.Single().Title);
    }

    // ---- filter --------------------------------------------------------------------------------

    [Fact]
    public void Filter_narrows_the_tree_by_name()
    {
        var (_, vm) = NewVm(Conn("aur prod"), Conn("netgiro test"));

        vm.ConnectionFilter = "netgiro";

        Assert.Equal("netgiro test", vm.ServerNodes.Single().Title);
    }

    [Fact]
    public void Filter_matches_the_port_so_two_localhosts_can_be_told_apart()
    {
        var (_, vm) = NewVm(Conn("one", port: 5432), Conn("two", port: 5434));

        vm.ConnectionFilter = "5434";

        Assert.Equal("two", vm.ServerNodes.Single().Title);
    }

    [Fact]
    public void Filter_matches_the_environment_label()
    {
        var (_, vm) = NewVm(Conn("a", environment: "production"), Conn("b", environment: "local"));

        vm.ConnectionFilter = "produc";

        Assert.Equal("a", vm.ServerNodes.Single().Title);
    }

    [Fact]
    public void Filter_leaves_the_toolbar_picker_alone()
    {
        var (_, vm) = NewVm(Conn("aur"), Conn("netgiro"));

        vm.ConnectionFilter = "aur";

        Assert.Single(vm.ServerNodes);
        Assert.Equal(2, vm.Connections.Count);   // the picker lists the project, not the pane's view of it
    }

    [Fact]
    public void Clearing_the_filter_brings_back_the_very_same_node()
    {
        var (_, vm) = NewVm(Conn("aur"), Conn("netgiro"));
        var netgiro = vm.ServerNodes.Single(n => n.Title == "netgiro");
        netgiro.IsExpanded = true;

        vm.ConnectionFilter = "aur";
        vm.ConnectionFilter = "";

        var back = vm.ServerNodes.Single(n => n.Title == "netgiro");
        Assert.Same(netgiro, back);   // typing in the filter box must not cost a catalog re-read
        Assert.True(back.IsExpanded);
    }
}
