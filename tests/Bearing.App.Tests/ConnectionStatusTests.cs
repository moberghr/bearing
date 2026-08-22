using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The toolbar/status-bar connection indicator + connect/cancel/disconnect toggle
/// (<see cref="ConnectionsViewModel"/>). Drives a real <see cref="WorkspaceContext"/> (hence a real
/// <see cref="ConnectionSessionManager"/>) over a <see cref="FakeProvider"/> whose connection test can be
/// gated, so Connecting / cancel / stale-result behaviour is deterministic with no live database.
/// </summary>
public class ConnectionStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-connstatus", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private (WorkspaceContext ctx, FakeProvider provider, ConnectionInfo conn) NewContext()
    {
        Directory.CreateDirectory(_root);
        var provider = new FakeProvider();
        var ctx = new WorkspaceContext(
            provider,
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore());
        var conn = new ConnectionInfo { Id = Guid.NewGuid(), Name = "prod", ProviderId = "postgres", Database = "app" };
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };
        return (ctx, provider, conn);
    }

    /// <summary>Build the VM with the tab already selected, so the constructor's SelectedTabChanged
    /// subscription does not fire an auto-warm — leaving the toggle as the only driver of state.</summary>
    private static ConnectionsViewModel VmWithSelectedTab(WorkspaceContext ctx, ConnectionInfo conn)
    {
        var tab = new EditorTabViewModel("t") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;
        return new ConnectionsViewModel(ctx);
    }

    [Fact]
    public async Task Toggle_from_disconnected_connects()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        Assert.Equal(ConnectionState.Disconnected, vm.State);

        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionState.Connected, vm.State);
        Assert.NotNull(ctx.Sessions.TryGet(conn.Id));
        Assert.True(ctx.IsConnected);
    }

    [Fact]
    public async Task Toggle_while_connecting_cancels_and_ignores_the_stale_result()
    {
        var (ctx, provider, conn) = NewContext();
        var gate = new TaskCompletionSource<bool>();
        provider.ConnectGate = gate;
        var vm = VmWithSelectedTab(ctx, conn);

        // Start connecting — the gate holds the attempt open at Connecting.
        var connecting = vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Connecting, vm.State);

        // Cancel while connecting → immediately Disconnected.
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Disconnected, vm.State);

        // The cancelled attempt settling must NOT flip the dot back to Connected.
        gate.TrySetResult(true);
        await connecting;
        Assert.Equal(ConnectionState.Disconnected, vm.State);
        Assert.Null(ctx.Sessions.TryGet(conn.Id));
    }

    [Fact]
    public async Task Toggle_while_connected_disconnects()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Connected, vm.State);

        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionState.Disconnected, vm.State);
        Assert.Null(ctx.Sessions.TryGet(conn.Id)); // shared session evicted
        Assert.False(ctx.IsConnected);
    }

    [Fact]
    public async Task Connect_failure_leaves_disconnected()
    {
        var (ctx, provider, conn) = NewContext();
        provider.TestResult = false; // the connection test fails
        var vm = VmWithSelectedTab(ctx, conn);

        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionState.Disconnected, vm.State);
        Assert.Null(ctx.Sessions.TryGet(conn.Id));
    }

    [Fact]
    public async Task Reconnect_after_disconnect_works()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);

        await vm.ToggleConnectionCommand.ExecuteAsync(null); // connect
        await vm.ToggleConnectionCommand.ExecuteAsync(null); // disconnect
        Assert.Equal(ConnectionState.Disconnected, vm.State);

        await vm.ToggleConnectionCommand.ExecuteAsync(null); // connect again

        Assert.Equal(ConnectionState.Connected, vm.State);
        Assert.NotNull(ctx.Sessions.TryGet(conn.Id));
    }

    [Fact]
    public async Task Selecting_a_tab_does_not_auto_connect()
    {
        var (ctx, _, conn) = NewContext();
        // The VM subscribes to SelectedTabChanged in its constructor; setting the tab afterwards drives the
        // tab-switch path (the one that used to eagerly warm the connection).
        var vm = new ConnectionsViewModel(ctx);
        var tab = new EditorTabViewModel("t") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;

        await Task.Delay(50); // give any (unwanted) background connect a chance to happen

        Assert.Equal(ConnectionState.Disconnected, vm.State);
        Assert.Null(ctx.Sessions.TryGet(conn.Id)); // merely switching tabs opened no session
    }

    /// <summary>The tab strip shows each tab's *own* chain glyph, so <c>ConnectionLive</c> has to track the
    /// session pool per tab — including tabs that are not selected and tabs on another server.</summary>
    [Fact]
    public async Task Connecting_marks_every_tab_on_that_connection_live()
    {
        var (ctx, _, conn) = NewContext();
        var other = new ConnectionInfo { Id = Guid.NewGuid(), Name = "dev", ProviderId = "postgres", Database = "app" };
        ctx.Project!.Manifest.Connections.Add(other);
        var vm = VmWithSelectedTab(ctx, conn);
        var sameServer = new EditorTabViewModel("t2") { ConnectionId = conn.Id };
        var otherServer = new EditorTabViewModel("t3") { ConnectionId = other.Id };
        ctx.Tabs.Add(sameServer);
        ctx.Tabs.Add(otherServer);

        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.True(ctx.SelectedTab!.ConnectionLive);
        Assert.True(sameServer.ConnectionLive);   // shares the session, keyed by connection Id
        Assert.False(otherServer.ConnectionLive); // different server, no session of its own
    }

    [Fact]
    public async Task Disconnecting_breaks_the_chain_on_every_tab_that_shared_the_session()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        var sameServer = new EditorTabViewModel("t2") { ConnectionId = conn.Id };
        ctx.Tabs.Add(sameServer);
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.True(sameServer.ConnectionLive);

        await vm.ToggleConnectionCommand.ExecuteAsync(null); // disconnect

        Assert.False(ctx.SelectedTab!.ConnectionLive);
        Assert.False(sameServer.ConnectionLive);
    }

    /// <summary>The selected tab links as soon as an attempt starts — the toolbar shows a linked chain while
    /// Connecting, and the tab must not contradict it. Cancelling has to put the break back.</summary>
    [Fact]
    public async Task Selected_tab_links_while_connecting_and_breaks_again_on_cancel()
    {
        var (ctx, provider, conn) = NewContext();
        var gate = new TaskCompletionSource<bool>();
        provider.ConnectGate = gate;
        var vm = VmWithSelectedTab(ctx, conn);
        Assert.False(ctx.SelectedTab!.ConnectionLive);

        var connecting = vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Connecting, vm.State);
        Assert.True(ctx.SelectedTab.ConnectionLive);

        await vm.ToggleConnectionCommand.ExecuteAsync(null); // cancel
        Assert.False(ctx.SelectedTab.ConnectionLive);

        gate.TrySetResult(true);
        await connecting;
        Assert.False(ctx.SelectedTab.ConnectionLive); // the stale attempt must not relink it
    }

    /// <summary>Sessions are keyed by connection Id alone (§9.4), so a tab pointed at another database on
    /// the same server shares the key but has no pool of its own — its next query rebuilds the session.
    /// The indicator must not claim it is connected.</summary>
    [Fact]
    public async Task A_tab_on_another_database_of_the_same_connection_is_not_live()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        var otherDb = new EditorTabViewModel("t2") { ConnectionId = conn.Id, DatabaseName = "reporting" };
        ctx.Tabs.Add(otherDb);

        await vm.ToggleConnectionCommand.ExecuteAsync(null); // connects on the connection's own db, "app"

        Assert.True(ctx.SelectedTab!.ConnectionLive);
        Assert.False(otherDb.ConnectionLive); // shares the session key, but the pool is open on "app"
    }

    /// <summary>The toolbar indicator answers the same question, so switching the selected tab's database
    /// away from the open one has to drop it out of Connected rather than keep claiming a live pool.</summary>
    [Fact]
    public async Task Switching_the_selected_tab_to_another_database_leaves_connected()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Connected, vm.State);

        vm.SetTabDatabase(ctx.SelectedTab!, "reporting");

        Assert.Equal("reporting", vm.SelectedTabDatabase);
        Assert.Equal(ConnectionState.Disconnected, vm.State);
        Assert.False(ctx.SelectedTab!.ConnectionLive);
    }

    /// <summary>The Connections pane's server row carries the same chain glyph, driven off
    /// <see cref="SchemaNodeViewModel.ConnectionLive"/>. Coarser than the per-tab flag by design: the node is
    /// the server, so any live session on that connection counts whichever database it is open on.</summary>
    [Fact]
    public async Task Server_node_tracks_its_connection_regardless_of_database()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        vm.RefreshConnections();
        var node = Assert.Single(vm.ServerNodes);
        Assert.True(node.ShowsConnectionState);
        Assert.False(node.ConnectionLive);
        Assert.Equal(conn.EnvironmentColor, node.RowAccentColor);

        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.True(node.ConnectionLive);
        Assert.Equal("Connected", node.ConnectionStateTip);

        // The tab moves to another database — its own chip breaks, but the *server* is still open.
        vm.SetTabDatabase(ctx.SelectedTab!, "reporting");
        Assert.False(ctx.SelectedTab!.ConnectionLive);
        Assert.True(node.ConnectionLive);
    }

    [Fact]
    public async Task Server_node_breaks_when_the_session_goes_away()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        vm.RefreshConnections();
        var node = Assert.Single(vm.ServerNodes);
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.True(node.ConnectionLive);

        await vm.ToggleConnectionCommand.ExecuteAsync(null); // disconnect

        Assert.False(node.ConnectionLive);
        Assert.Equal("Not connected", node.ConnectionStateTip);
    }

    /// <summary>A non-server row has no state slot and no row wash — the environment fill and the chain are
    /// the server row's alone.</summary>
    [Fact]
    public void Non_server_nodes_carry_neither_a_wash_nor_a_state_glyph()
    {
        var node = new MessageNodeViewModel("", "Loading…");
        Assert.False(node.ShowsConnectionState);
        Assert.Null(node.RowAccentColor);
    }

    [Fact]
    public async Task A_tab_with_no_connection_is_never_live()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        var unassigned = new EditorTabViewModel("t2");
        ctx.Tabs.Add(unassigned);

        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.False(unassigned.ConnectionLive);
    }

    [Theory]
    [InlineData(ConnectionState.Connected, "Connected", true, false, false, "Disconnect from server")]
    [InlineData(ConnectionState.Connecting, "Connecting…", true, true, false, "Cancel connecting")]
    [InlineData(ConnectionState.Disconnected, "Disconnected", false, false, true, "Connect to server")]
    public void Derived_members_map_per_state(
        ConnectionState state, string label, bool linked, bool connecting, bool disconnected, string tip)
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);

        vm.State = state;

        Assert.Equal(label, vm.StatusLabel);
        Assert.Equal(linked, vm.IsLinked);
        Assert.Equal(connecting, vm.IsConnecting);
        Assert.Equal(disconnected, vm.IsDisconnected);
        Assert.Equal(tip, vm.ToggleTip);
    }
}
