using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Views;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Switching projects is a view change, not a lifecycle event: the outgoing project's tabs are parked
/// (same view-model instances, so results and in-flight queries survive), its sessions stay pooled, and
/// switching back unparks exactly what was left behind. These cover the contract that used to be broken —
/// tabs cleared, results discarded, every connection closed.
/// </summary>
public class ProjectSwitchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-switch", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private string Dir(string name) => Path.Combine(_root, name);

    private ShellViewModel NewVm(FakeProvider provider) => new(
        provider,
        new JsonProjectStore(),
        new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(Path.Combine(_root, "recent.json")),
        new FakeSecretStore(),
        // Autosave off everywhere except the scratch-ownership test, which opts back in.
        settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));

    /// <summary>Open <paramref name="dir"/> and give it one named connection, returned for tab assignment.</summary>
    private static async Task<ConnectionInfo> SeedAsync(ShellViewModel vm, string dir, string name)
    {
        await vm.OpenProjectAsync(dir);
        var conn = new ConnectionInfo { Id = Guid.NewGuid(), Name = name, ProviderId = "postgres", Database = "app" };
        await vm.Connections.AddOrUpdateConnectionAsync(conn, "pw");
        foreach (var tab in vm.Workspace.Tabs) vm.Connections.SetTabConnection(tab, conn.Id);
        return conn;
    }

    // ---- parking ---------------------------------------------------------------------------------

    [Fact]
    public async Task Switching_projects_parks_the_old_tabs_instead_of_closing_them()
    {
        var vm = NewVm(new FakeProvider());
        await SeedAsync(vm, Dir("a"), "a");
        var a1 = vm.Workspace.SelectedTab!;
        a1.Text = "select 1 -- a1";
        var a2 = vm.Workspace.NewTab("select 2 -- a2");

        await vm.OpenProjectAsync(Dir("b"));

        // The strip shows only project B…
        Assert.DoesNotContain(a1, vm.Workspace.Tabs);
        Assert.DoesNotContain(a2, vm.Workspace.Tabs);
        // …but A's tabs are still alive, same instances, with their buffers intact.
        Assert.Contains(a1, vm.Workspace.AllTabs);
        Assert.Contains(a2, vm.Workspace.AllTabs);
        Assert.Equal("select 1 -- a1", a1.Text);
    }

    [Fact]
    public async Task Switching_back_restores_the_same_tabs_their_results_and_the_selection()
    {
        var provider = new FakeProvider();
        var vm = NewVm(provider);
        await SeedAsync(vm, Dir("a"), "a");
        var a1 = vm.Workspace.SelectedTab!;
        var a2 = vm.Workspace.NewTab("select 2");
        vm.Connections.SetTabConnection(a2, a1.ConnectionId);

        await vm.Execution.ExecuteAsync("select 2");
        Assert.NotNull(a2.LastResult);              // results live on the tab, never in session.json
        var results = a2.Results;

        await vm.OpenProjectAsync(Dir("b"));
        await vm.OpenProjectAsync(Dir("a"));

        Assert.Equal(new[] { a1, a2 }, vm.Workspace.Tabs);     // same instances, same order
        Assert.Same(a2, vm.Workspace.SelectedTab);             // and the tab that was focused
        Assert.Same(results, a2.Results);                      // results were never rebuilt
    }

    [Fact]
    public async Task Switching_back_keeps_unsaved_buffer_edits_made_after_the_last_save()
    {
        var vm = NewVm(new FakeProvider());
        await SeedAsync(vm, Dir("a"), "a");
        var tab = vm.Workspace.SelectedTab!;

        await vm.OpenProjectAsync(Dir("b"));
        // Typed into the parked tab (autosave off, nothing written) — a rebuild from disk would lose this.
        tab.Text = "-- typed while project B was on screen";
        await vm.OpenProjectAsync(Dir("a"));

        Assert.Same(tab, Assert.Single(vm.Workspace.Tabs));
        Assert.Equal("-- typed while project B was on screen", tab.Text);
    }

    // ---- a query running through the switch ------------------------------------------------------

    [Fact]
    public async Task A_query_started_before_the_switch_finishes_against_its_own_tab()
    {
        var gated = new GatedExecutor();
        var vm = NewVm(new FakeProvider { Executor = gated });
        await SeedAsync(vm, Dir("a"), "a");
        var tab = vm.Workspace.SelectedTab!;

        var toasts = new List<BackgroundCompletion>();
        vm.Execution.BackgroundCompleted += toasts.Add;

        var run = vm.Execution.ExecuteAsync("select pg_sleep(30)");
        await WaitUntil(() => gated.Started == 1);

        await vm.OpenProjectAsync(Dir("b"));
        Assert.True(tab.IsRunning);                       // the switch did not cancel it

        gated.Release("select pg_sleep(30)");
        await run;

        var toast = Assert.Single(toasts);
        Assert.True(toast.TabStillOpen);                  // parked is not closed
        Assert.Same(tab, toast.Tab);                      // and the toast can click through to it
        Assert.NotNull(tab.LastResult);                   // results waiting on the tab

        await vm.RevealTabAsync(toast.Tab!);              // what clicking the toast does
        Assert.Equal(Path.GetFullPath(Dir("a")), vm.ProjectDirectory);
        Assert.Same(tab, vm.Workspace.SelectedTab);
    }

    [Fact]
    public async Task The_quit_guard_sees_a_query_running_in_a_project_that_is_not_on_screen()
    {
        var gated = new GatedExecutor();
        var vm = NewVm(new FakeProvider { Executor = gated });
        await SeedAsync(vm, Dir("a"), "a");

        var run = vm.Execution.ExecuteAsync("select pg_sleep(30)");
        await WaitUntil(() => gated.Started == 1);
        await vm.OpenProjectAsync(Dir("b"));

        Assert.Equal(1, QuitGuard.RunningCount(vm));

        gated.Release("select pg_sleep(30)");
        await run;
    }

    // ---- connections -----------------------------------------------------------------------------

    [Fact]
    public async Task Switching_projects_leaves_the_connection_live()
    {
        var provider = new FakeProvider();
        var vm = NewVm(provider);
        await SeedAsync(vm, Dir("a"), "a");

        await vm.Execution.ExecuteAsync("select 1");      // lazily opens the session
        var connectsBefore = provider.FactoriesCreated;   // query session + the schema browser's reader

        await vm.OpenProjectAsync(Dir("b"));
        await vm.OpenProjectAsync(Dir("a"));

        // Restoring the project re-selects its tab, which re-derives the indicator from the real pool —
        // Connected here means the session was never closed by the switch.
        Assert.Equal(ConnectionState.Connected, vm.Connections.State);
        // Neither pool was torn down, so nothing had to be dialled again.
        Assert.Equal(connectsBefore, provider.FactoriesCreated);
    }

    [Fact]
    public void FindConnection_resolves_an_id_owned_by_a_project_that_is_not_active()
    {
        var ctx = NewContext();
        var parkedConn = new ConnectionInfo { Id = Guid.NewGuid(), Name = "parked", ProviderId = "postgres", Database = "app" };
        var parked = new Project { Directory = Dir("a"), Manifest = new ProjectManifest { Connections = { parkedConn } } };
        ctx.Project = parked;
        ctx.GetOrAdd(parked);
        ctx.Park(sidePaneOpen: true, sidePaneWidth: 260, ResultsViewMode.Stacked);

        var active = new Project { Directory = Dir("b"), Manifest = new ProjectManifest() };
        ctx.Project = active;
        ctx.GetOrAdd(active);

        Assert.Equal("parked", ctx.FindConnection(parkedConn.Id)?.Name);
        Assert.Null(ctx.FindConnection(Guid.NewGuid()));
    }

    // ---- the project switcher --------------------------------------------------------------------

    /// <summary>
    /// The toolbar switcher resolves its selection from <c>ProjectDirectory</c> against the items in
    /// <c>RecentProjects</c>, and loses it whenever that collection is rebuilt. Switching back to an
    /// already-open project used to rebuild the list *after* announcing the switch, leaving the pill blank.
    /// This drives the same two signals the view listens to.
    /// </summary>
    [Fact]
    public async Task Switching_leaves_the_project_switcher_showing_the_project_that_is_on_screen()
    {
        var vm = NewVm(new FakeProvider());
        RecentProjectItem? selected = null;
        vm.RecentProjects.CollectionChanged += (_, _) => selected = null;                 // Clear() nulls SelectedItem
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.ProjectDirectory))
                selected = vm.RecentProjects.FirstOrDefault(r => r.Directory == vm.ProjectDirectory);
        };

        await SwitchTo(Dir("a"));
        Assert.Equal(Path.GetFullPath(Dir("a")), selected?.Directory);

        await SwitchTo(Dir("b"));
        Assert.Equal(Path.GetFullPath(Dir("b")), selected?.Directory);

        await SwitchTo(Dir("a"));   // the parked-project path
        Assert.Equal(Path.GetFullPath(Dir("a")), selected?.Directory);

        // Switching also touches the recent list; wait for that rebuild to land before looking at the
        // selection, or the assertions pass on timing alone (the rebuild used to be fire-and-forget).
        async Task SwitchTo(string dir)
        {
            await vm.OpenProjectAsync(dir);
            await WaitUntil(() => vm.RecentProjects.FirstOrDefault()?.Directory == Path.GetFullPath(dir));
        }
    }

    // ---- persistence -----------------------------------------------------------------------------

    [Fact]
    public async Task Saving_the_workspace_writes_a_session_for_every_open_project()
    {
        var vm = NewVm(new FakeProvider());
        await SeedAsync(vm, Dir("a"), "a");
        vm.Workspace.SelectedTab!.Text = "-- from a";
        await SeedAsync(vm, Dir("b"), "b");
        vm.Workspace.SelectedTab!.Text = "-- from b";

        vm.SaveWorkspace();

        var store = new JsonSessionStore();
        var a = await store.LoadAsync(Dir("a"), CancellationToken.None);
        var b = await store.LoadAsync(Dir("b"), CancellationToken.None);

        Assert.Equal("-- from a", Assert.Single(a!.OpenEditors).ScratchText);
        Assert.Equal("-- from b", Assert.Single(b!.OpenEditors).ScratchText);
    }

    [Fact]
    public async Task A_parked_tabs_scratch_file_lands_in_its_own_projects_scratch_folder()
    {
        var vm = new ShellViewModel(
            new FakeProvider(),
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.OnEdit }));

        await vm.OpenProjectAsync(Dir("a"));
        var parked = vm.Workspace.SelectedTab!;

        await vm.OpenProjectAsync(Dir("b"));
        parked.Text = "-- written while project B is on screen";
        await vm.Workspace.Autosave.FlushAsync(parked);

        Assert.NotNull(parked.ScriptPath);
        var owner = new Project { Directory = Path.GetFullPath(Dir("a")), Manifest = new ProjectManifest() };
        Assert.StartsWith(owner.ScratchDirectory, parked.ScriptPath!, StringComparison.Ordinal);
        Assert.Equal("-- written while project B is on screen", File.ReadAllText(parked.ScriptPath!));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private WorkspaceContext NewContext() => new(
        new FakeProvider(),
        new JsonProjectStore(),
        new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(Path.Combine(_root, "recent.json")),
        new FakeSecretStore(),
        settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(10);
        }
    }
}
