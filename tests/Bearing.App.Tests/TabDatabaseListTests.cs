using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.Services;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The toolbar's database list is read only from a server the user has already connected to. Reading it
/// opens a connection, so doing it on every tab selection connected to — and, for a "Prompt each time"
/// connection, asked the password of — servers nobody had asked for, at launch included: restoring tabs
/// selects each one in turn.
/// </summary>
public class TabDatabaseListTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-tabdbs", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "condition did not hold within the timeout");
            await Task.Delay(10);
        }
    }

    private (WorkspaceContext ctx, FakeProvider provider, FakeCredentialPrompt prompt, ConnectionInfo conn) NewContext(
        CredentialKind kind)
    {
        Directory.CreateDirectory(_root);
        var provider = new FakeProvider();
        var prompt = new FakeCredentialPrompt("typed");
        var ctx = new WorkspaceContext(
            provider,
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            credentialPrompt: prompt);
        // "postgres" as the connection's own database: the fake server lists only "app", so a list holding
        // "app" can only have come from a read.
        var conn = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "local",
            ProviderId = "postgres",
            Database = "postgres",
            CredentialKind = kind,
        };
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };
        return (ctx, provider, prompt, conn);
    }

    private static (ConnectionsViewModel vm, EditorTabViewModel tab) VmWithSelectedTab(WorkspaceContext ctx, ConnectionInfo conn)
    {
        var tab = new EditorTabViewModel("t") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;
        return (new ConnectionsViewModel(ctx), tab);
    }

    [Fact]
    public async Task Selecting_a_tab_on_a_server_nobody_connected_to_opens_no_connection_and_asks_nothing()
    {
        var (ctx, provider, prompt, conn) = NewContext(CredentialKind.Prompt);
        var (vm, _) = VmWithSelectedTab(ctx, conn);

        vm.OnSelectedTabChanged();
        // The read this guards against is fire-and-forget; give it the time it would need to reach the prompt.
        await Task.Delay(150);

        Assert.Equal(0, prompt.Calls);
        Assert.Equal(0, provider.FactoriesCreated);
        Assert.False(ctx.Sessions.IsLinked(conn.Id));
        Assert.Equal(["postgres"], vm.TabDatabases);   // the tab's own database, so the pill is never empty
    }

    [Fact]
    public async Task Pointing_a_tab_at_a_server_nobody_connected_to_opens_no_connection_either()
    {
        var (ctx, provider, prompt, conn) = NewContext(CredentialKind.Prompt);
        var other = new ConnectionInfo { Id = Guid.NewGuid(), Name = "other", ProviderId = "postgres", Database = "app" };
        ctx.Project!.Manifest.Connections.Add(other);
        var (vm, tab) = VmWithSelectedTab(ctx, other);

        vm.SetTabConnection(tab, conn.Id);
        await Task.Delay(150);

        Assert.Equal(0, prompt.Calls);
        Assert.Equal(0, provider.FactoriesCreated);
    }

    [Fact]
    public async Task A_connected_server_lists_its_databases()
    {
        var (ctx, _, _, conn) = NewContext(CredentialKind.StoredPassword);
        var (vm, _) = VmWithSelectedTab(ctx, conn);
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.True(ctx.Sessions.IsLinked(conn.Id));

        vm.OnSelectedTabChanged();

        await WaitUntil(() => vm.TabDatabases.Contains("app"));
        Assert.Contains("postgres", vm.TabDatabases);   // the tab's current database stays selectable
    }

    [Fact]
    public async Task A_server_that_becomes_connected_fills_the_list_for_the_selected_tab()
    {
        var (ctx, _, _, conn) = NewContext(CredentialKind.StoredPassword);
        var (vm, _) = VmWithSelectedTab(ctx, conn);
        vm.OnSelectedTabChanged();
        Assert.Equal(["postgres"], vm.TabDatabases);
        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        vm.OnServerLinked(conn.Id);   // what the LinkChanged handler runs on the UI thread

        await WaitUntil(() => vm.TabDatabases.Contains("app"));
    }

    [Fact]
    public async Task A_link_on_another_server_reads_nothing_for_the_selected_tab()
    {
        var (ctx, provider, _, conn) = NewContext(CredentialKind.StoredPassword);
        var (vm, _) = VmWithSelectedTab(ctx, conn);

        vm.OnServerLinked(Guid.NewGuid());
        await Task.Delay(150);

        Assert.Equal(0, provider.FactoriesCreated);
    }

    [Fact]
    public async Task Restoring_tabs_never_selects_a_tab_holding_its_neighbours_connection()
    {
        // A restored tab used to be created — and selected — with the previous tab's connection, and given its
        // own only afterwards. Everything that reacts to a selection then acted on the wrong server.
        var vm = new ShellViewModel(
            new ProviderRegistry(),
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            dialogs: new FakeDialogs(),
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));
        await vm.InitializeAsync(Path.Combine(_root, "project"));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var seen = new List<(EditorTabViewModel Tab, Guid? Connection)>();
        vm.Workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceViewModel.SelectedTab) && vm.Workspace.SelectedTab is { } tab)
                seen.Add((tab, tab.ConnectionId));
        };

        await vm.Workspace.RestoreTabsAsync(new SessionState
        {
            OpenEditors =
            [
                new OpenEditor { ScratchText = "-- one", ConnectionId = first },
                new OpenEditor { ScratchText = "-- two", ConnectionId = second },
                new OpenEditor { ScratchText = "-- three", ConnectionId = first },
            ],
            SelectedEditorIndex = 0,
        });

        Assert.Equal([first, second, first], vm.Workspace.Tabs.Select(t => t.ConnectionId));
        Assert.NotEmpty(seen);
        Assert.All(seen, s => Assert.Equal(s.Tab.ConnectionId, s.Connection));
    }
}
