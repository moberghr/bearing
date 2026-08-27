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

    /// <summary>Force the idle sweep to fire immediately against the context's real session manager.</summary>
    private static async Task SweepNowAsync(WorkspaceContext ctx)
    {
        var mgr = (ConnectionSessionManager)ctx.Sessions;
        mgr.IdleTimeout = TimeSpan.FromMilliseconds(1);
        await Task.Delay(20);   // comfortably past the 1 ms timeout on a real clock
        await mgr.SweepIdleAsync();
    }

    /// <summary>Poll until a condition holds, for the handful of assertions that follow a fire-and-forget
    /// background warm. Fails the test rather than hanging if it never does.</summary>
    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "condition did not hold within the timeout");
            await Task.Delay(10);
        }
    }

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
        Assert.NotNull(ctx.Sessions.TryGet(SessionKey.For(conn)));
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
        Assert.Null(ctx.Sessions.TryGet(SessionKey.For(conn)));
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
        Assert.Null(ctx.Sessions.TryGet(SessionKey.For(conn)));  // the pool the tab was using is gone
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
        Assert.Null(ctx.Sessions.TryGet(SessionKey.For(conn)));
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
        Assert.NotNull(ctx.Sessions.TryGet(SessionKey.For(conn)));
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
        Assert.Null(ctx.Sessions.TryGet(SessionKey.For(conn)));  // merely switching tabs opened no session
    }

    /// <summary>The tab strip shows each tab's *own* beacon, so <c>ConnectionState</c> has to track the
    /// server link per tab — including tabs that are not selected and tabs on another server.</summary>
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

        Assert.Equal(ConnectionState.Connected, ctx.SelectedTab!.ConnectionState);
        Assert.Equal(ConnectionState.Connected, sameServer.ConnectionState);   // same connection *and* database, so the same session
        Assert.Equal(ConnectionState.Disconnected, otherServer.ConnectionState); // different server, no session of its own
    }

    [Fact]
    public async Task Disconnecting_breaks_the_chain_on_every_tab_that_shared_the_session()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        var sameServer = new EditorTabViewModel("t2") { ConnectionId = conn.Id };
        ctx.Tabs.Add(sameServer);
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Connected, sameServer.ConnectionState);

        await vm.ToggleConnectionCommand.ExecuteAsync(null); // disconnect

        Assert.Equal(ConnectionState.Disconnected, ctx.SelectedTab!.ConnectionState);
        Assert.Equal(ConnectionState.Disconnected, sameServer.ConnectionState);
    }

    /// <summary>The selected tab enters Connecting the moment an attempt starts, so its beacon pulses
    /// alongside the toolbar's rather than contradicting it — and unlike the old bool, it no longer has to
    /// borrow the Connected mark to say so. Cancelling has to put it back to Disconnected.</summary>
    [Fact]
    public async Task Selected_tab_pulses_while_connecting_and_goes_dark_again_on_cancel()
    {
        var (ctx, provider, conn) = NewContext();
        var gate = new TaskCompletionSource<bool>();
        provider.ConnectGate = gate;
        var vm = VmWithSelectedTab(ctx, conn);
        Assert.Equal(ConnectionState.Disconnected, ctx.SelectedTab!.ConnectionState);

        var connecting = vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Connecting, vm.State);
        Assert.Equal(ConnectionState.Connecting, ctx.SelectedTab.ConnectionState);

        await vm.ToggleConnectionCommand.ExecuteAsync(null); // cancel
        Assert.Equal(ConnectionState.Disconnected, ctx.SelectedTab.ConnectionState);

        gate.TrySetResult(true);
        await connecting;
        Assert.Equal(ConnectionState.Disconnected, ctx.SelectedTab.ConnectionState); // the stale attempt must not relink it
    }

    /// <summary>Connecting is about the <i>server</i>. A tab pointed at another database on the same server
    /// has its own pool (§9.4) and that pool may well not be open yet, but the user authenticated to that
    /// server and the tab must say so — the old per-(connection, database) reading had two tabs on one server
    /// showing opposite chain glyphs.</summary>
    [Fact]
    public async Task A_tab_on_another_database_of_the_same_connection_shares_the_server_link()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        var otherDb = new EditorTabViewModel("t2") { ConnectionId = conn.Id, DatabaseName = "reporting" };
        ctx.Tabs.Add(otherDb);

        await vm.ToggleConnectionCommand.ExecuteAsync(null); // connects on the connection's own db, "app"

        Assert.Equal(ConnectionState.Connected, ctx.SelectedTab!.ConnectionState);
        Assert.Equal(ConnectionState.Connected, otherDb.ConnectionState);                          // same server, same link
        Assert.Null(ctx.Sessions.TryGet(new SessionKey(conn.Id, "reporting"))); // …with no pool of its own yet
    }

    /// <summary>Switching the Database pill moves which pool the tab will use; it does not disconnect the
    /// user from the server they are authenticated to, and the indicator must stop saying it does.</summary>
    [Fact]
    public async Task Switching_the_selected_tab_to_another_database_stays_connected()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Connected, vm.State);

        vm.SetTabDatabase(ctx.SelectedTab!, "reporting");

        Assert.Equal("reporting", vm.SelectedTabDatabase);
        Assert.Equal(ConnectionState.Connected, vm.State);
        Assert.Equal(ConnectionState.Connected, ctx.SelectedTab!.ConnectionState);
        Assert.NotNull(ctx.Sessions.TryGet(SessionKey.For(conn)));    // the old database's pool is left alone
    }

    /// <summary>…and the new database's pool is opened in the background, so "connected" is a fact and not a
    /// promise. Reuses the credential already resolved for the server, so it never prompts.</summary>
    [Fact]
    public async Task Switching_database_warms_the_new_databases_pool()
    {
        var (ctx, provider, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(1, provider.FactoriesCreated);

        vm.SetTabDatabase(ctx.SelectedTab!, "reporting");
        await WaitUntil(() => ctx.Sessions.TryGet(new SessionKey(conn.Id, "reporting")) is not null);

        Assert.Equal(2, provider.FactoriesCreated);                   // a second pool, not a rebuilt one
        Assert.NotNull(ctx.Sessions.TryGet(SessionKey.For(conn)));
    }

    /// <summary>A database switch on a server the user never connected to must not connect to it — the warm
    /// piggybacks on an existing link, it does not create one.</summary>
    [Fact]
    public async Task Switching_database_while_disconnected_opens_nothing()
    {
        var (ctx, provider, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);

        vm.SetTabDatabase(ctx.SelectedTab!, "reporting");
        await Task.Delay(50); // give any (unwanted) background connect a chance to happen

        Assert.Equal(0, provider.FactoriesCreated);
        Assert.Equal(ConnectionState.Disconnected, vm.State);
    }

    /// <summary>An idle sweep reclaims pools; it does not disconnect the user. Watching the chain snap while
    /// reading a result set for half an hour is the surprise the server link exists to remove — and the next
    /// query reopens a pool from the still-cached credential without a prompt.</summary>
    [Fact]
    public async Task An_idle_sweep_reclaims_the_pool_without_breaking_the_chain()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        vm.RefreshConnections();
        var node = Assert.Single(vm.ServerNodes);
        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        await SweepNowAsync(ctx);

        Assert.Null(ctx.Sessions.TryGet(SessionKey.For(conn)));   // the pool really is gone
        Assert.Equal(ConnectionState.Connected, vm.State);        // …and the user is still connected
        Assert.Equal(ConnectionState.Connected, ctx.SelectedTab!.ConnectionState);
        Assert.Equal(ConnectionState.Connected, node.ConnectionState);
    }

    /// <summary>Disconnect has to work in that state too: there are no pools left to evict, so a teardown that
    /// only dropped the link as a side effect of removing the last session would do nothing at all.</summary>
    [Fact]
    public async Task Disconnect_after_an_idle_sweep_still_breaks_the_chain()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        await SweepNowAsync(ctx);
        Assert.Equal(ConnectionState.Connected, vm.State);

        await vm.ToggleConnectionCommand.ExecuteAsync(null); // disconnect

        Assert.Equal(ConnectionState.Disconnected, vm.State);
        Assert.Equal(ConnectionState.Disconnected, ctx.SelectedTab!.ConnectionState);
    }

    /// <summary>The Connections pane's server row carries the same chain glyph, driven off
    /// <see cref="SchemaNodeViewModel.ConnectionState"/>. The node is
    /// the server, so any live session on that connection counts whichever database it is open on.</summary>
    [Fact]
    public async Task Server_node_tracks_its_connection_regardless_of_database()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        vm.RefreshConnections();
        var node = Assert.Single(vm.ServerNodes);
        Assert.True(node.ShowsConnectionState);
        Assert.Equal(ConnectionState.Disconnected, node.ConnectionState);
        Assert.Equal(conn.EnvironmentColor, node.RowAccentColor);

        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Connected, node.ConnectionState);
        Assert.Equal("Connected", node.ConnectionStateTip);

        // The tab moves to another database — the server is still open, and the tab agrees with the row.
        vm.SetTabDatabase(ctx.SelectedTab!, "reporting");
        Assert.Equal(ConnectionState.Connected, ctx.SelectedTab!.ConnectionState);
        Assert.Equal(ConnectionState.Connected, node.ConnectionState);
    }

    [Fact]
    public async Task Server_node_breaks_when_the_session_goes_away()
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);
        vm.RefreshConnections();
        var node = Assert.Single(vm.ServerNodes);
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Connected, node.ConnectionState);

        await vm.ToggleConnectionCommand.ExecuteAsync(null); // disconnect

        Assert.Equal(ConnectionState.Disconnected, node.ConnectionState);
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

        Assert.Equal(ConnectionState.Disconnected, unassigned.ConnectionState);
    }

    [Theory]
    [InlineData(ConnectionState.Connected, "Connected", false, false, "Disconnect from server")]
    [InlineData(ConnectionState.Connecting, "Connecting…", true, false, "Cancel connecting")]
    [InlineData(ConnectionState.Disconnected, "Disconnected", false, true, "Connect to server")]
    public void Derived_members_map_per_state(
        ConnectionState state, string label, bool connecting, bool disconnected, string tip)
    {
        var (ctx, _, conn) = NewContext();
        var vm = VmWithSelectedTab(ctx, conn);

        vm.State = state;

        Assert.Equal(label, vm.StatusLabel);
        Assert.Equal(connecting, vm.IsConnecting);
        Assert.Equal(disconnected, vm.IsDisconnected);
        Assert.Equal(tip, vm.ToggleTip);
    }
}
