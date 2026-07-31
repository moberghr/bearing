using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Squirrel.App.Connections;
using Squirrel.App.ViewModels;
using Squirrel.App.Workspace;
using Squirrel.Core.Data;
using Squirrel.Core.Workspace;
using Squirrel.Persistence;
using Xunit;

namespace Squirrel.App.Tests;

/// <summary>
/// The toolbar/status-bar connection indicator + connect/cancel/disconnect toggle
/// (<see cref="ConnectionsViewModel"/>). Drives a real <see cref="WorkspaceContext"/> (hence a real
/// <see cref="ConnectionSessionManager"/>) over a <see cref="FakeProvider"/> whose connection test can be
/// gated, so Connecting / cancel / stale-result behaviour is deterministic with no live database.
/// </summary>
public class ConnectionStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "squirrel-connstatus", Guid.NewGuid().ToString("N"));

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
