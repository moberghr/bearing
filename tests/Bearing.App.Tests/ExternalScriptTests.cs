using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// `bearing file.sql`: a script from outside any project opens in the one on screen, stays where it is on
/// disk, runs against that project's connections — and can be copied into the project, or moved with its
/// tab to the project it is really for.
/// </summary>
public class ExternalScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-external", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private string Dir(string name) => Path.Combine(_root, name);

    private string Outside(string name, string text)
    {
        var path = Path.Combine(_root, "elsewhere", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private ShellViewModel NewVm(FakeProvider? provider = null) => new(
        provider ?? new FakeProvider(),
        new JsonProjectStore(),
        new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(Path.Combine(_root, "recent.json")),
        new FakeSecretStore(),
        settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));

    private static async Task<ConnectionInfo> SeedAsync(ShellViewModel vm, string dir, string name)
    {
        await vm.OpenProjectAsync(dir);
        var conn = new ConnectionInfo { Id = Guid.NewGuid(), Name = name, ProviderId = "postgres", Database = "app" };
        await vm.Connections.AddOrUpdateConnectionAsync(conn, "pw");
        foreach (var tab in vm.Workspace.Tabs) vm.Connections.SetTabConnection(tab, conn.Id);
        return conn;
    }

    // ---- opening ---------------------------------------------------------------------------------

    [Fact]
    public async Task An_outside_file_opens_in_the_current_project_where_it_is_and_on_its_connection()
    {
        var vm = NewVm();
        var conn = await SeedAsync(vm, Dir("a"), "a");
        var file = Outside("report.sql", "select 42");

        await vm.OpenScriptFilesAsync([file]);

        var tab = vm.Workspace.SelectedTab!;
        Assert.Equal(file, tab.ScriptPath);                       // not copied anywhere
        Assert.Equal("select 42", tab.Text);
        Assert.False(tab.IsScratch);
        Assert.Equal(Path.GetFullPath(Dir("a")), tab.ProjectDirectory);
        Assert.Equal(conn.Id, tab.ConnectionId);                  // runs in the project's context
        Assert.True(vm.IsOutsideProject(tab));
        Assert.Contains("runs against 'a'", vm.StatusText);
        Assert.False(File.Exists(Path.Combine(Dir("a"), "scripts", "report.sql")));
    }

    [Fact]
    public async Task Opening_a_file_that_is_already_open_brings_its_tab_forward_instead_of_a_second_one()
    {
        var vm = NewVm();
        await SeedAsync(vm, Dir("a"), "a");
        var file = Outside("report.sql", "select 1");

        await vm.OpenScriptFilesAsync([file]);
        var first = vm.Workspace.SelectedTab!;
        vm.Workspace.NewTab();
        await vm.OpenScriptFilesAsync([Path.Combine(Path.GetDirectoryName(file)!, ".", "report.sql")]);

        Assert.Same(first, vm.Workspace.SelectedTab);
        Assert.Single(vm.Workspace.AllTabs, t => t.ScriptPath == file);
    }

    [Fact]
    public async Task A_file_open_in_a_parked_project_brings_that_project_back()
    {
        var vm = NewVm();
        await SeedAsync(vm, Dir("a"), "a");
        var file = Outside("report.sql", "select 1");
        await vm.OpenScriptFilesAsync([file]);
        var tab = vm.Workspace.SelectedTab!;
        await SeedAsync(vm, Dir("b"), "b");

        await vm.OpenScriptFilesAsync([file]);

        Assert.Equal(Path.GetFullPath(Dir("a")), vm.ProjectDirectory);
        Assert.Same(tab, vm.Workspace.SelectedTab);
    }

    [Fact]
    public async Task A_missing_file_opens_nothing_and_says_so()
    {
        var vm = NewVm();
        await SeedAsync(vm, Dir("a"), "a");
        var before = vm.Workspace.Tabs.Count;

        await vm.OpenScriptFilesAsync([Path.Combine(_root, "nope.sql")]);

        Assert.Equal(before, vm.Workspace.Tabs.Count);
        Assert.Contains("no such file", vm.StatusText);
    }

    /// <summary>The tab comes back after a restart, at its own path rather than a `../..` walk from the
    /// project that would break the moment either folder moved.</summary>
    [Fact]
    public async Task An_outside_file_survives_a_restart_by_its_absolute_path()
    {
        var vm = NewVm();
        await SeedAsync(vm, Dir("a"), "a");
        var file = Outside("report.sql", "select 1");
        await vm.OpenScriptFilesAsync([file]);
        vm.SaveWorkspace();

        var session = await new JsonSessionStore().LoadAsync(Path.GetFullPath(Dir("a")), default);
        Assert.Contains(session!.OpenEditors, e => e.ScriptPath == file);

        var again = NewVm();
        await again.OpenProjectAsync(Dir("a"));
        Assert.Contains(again.Workspace.Tabs, t => t.ScriptPath == file);
    }

    [Fact]
    public void A_project_script_stays_relative_in_the_session_and_an_outside_one_is_absolute()
    {
        var project = Path.Combine(_root, "p");
        Assert.Equal(Path.Combine("scripts", "a.sql"),
            ShellViewModel.SessionPath(project, Path.Combine(project, "scripts", "a.sql")));
        Assert.Equal(Path.Combine(_root, "x.sql"), ShellViewModel.SessionPath(project, Path.Combine(_root, "x.sql")));
    }

    // ---- copying into the project ----------------------------------------------------------------

    [Fact]
    public async Task Copying_into_the_project_writes_the_buffer_there_and_leaves_the_original_alone()
    {
        var vm = NewVm();
        await SeedAsync(vm, Dir("a"), "a");
        var file = Outside("report.sql", "select 1");
        await vm.OpenScriptFilesAsync([file]);
        var tab = vm.Workspace.SelectedTab!;
        tab.Text = "select 1 -- edited";

        var copy = await vm.CopyTabIntoProjectAsync(tab);

        Assert.Equal(Path.Combine(Path.GetFullPath(Dir("a")), "scripts", "report.sql"), copy);
        Assert.Equal("select 1 -- edited", File.ReadAllText(copy!));
        Assert.Equal("select 1", File.ReadAllText(file));          // the original is untouched
        Assert.Equal(copy, tab.ScriptPath);                          // and the tab edits the copy now
        Assert.False(tab.IsModified);
        Assert.False(vm.IsOutsideProject(tab));
    }

    [Fact]
    public async Task A_copy_never_overwrites_a_script_already_in_the_project()
    {
        var vm = NewVm();
        await SeedAsync(vm, Dir("a"), "a");
        var existing = Path.Combine(Path.GetFullPath(Dir("a")), "scripts", "report.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "-- the project's own");
        await vm.OpenScriptFilesAsync([Outside("report.sql", "select 1")]);

        var copy = await vm.CopyTabIntoProjectAsync(vm.Workspace.SelectedTab!);

        Assert.Equal("report (2).sql", Path.GetFileName(copy));
        Assert.Equal("-- the project's own", File.ReadAllText(existing));
    }

    [Fact]
    public void Free_names_count_up_from_two()
    {
        Assert.Equal("a.sql", ExternalScripts.FreeName("a.sql", _ => false));
        Assert.Equal("a (3).sql", ExternalScripts.FreeName("a.sql", n => n is "a.sql" or "a (2).sql"));
    }

    // ---- moving to another project ---------------------------------------------------------------

    [Fact]
    public async Task Moving_a_tab_takes_it_to_the_other_project_on_that_projects_connection()
    {
        var provider = new FakeProvider();
        var vm = NewVm(provider);
        var b = await SeedAsync(vm, Dir("b"), "b");
        await SeedAsync(vm, Dir("a"), "a");
        var file = Outside("report.sql", "select 1");
        await vm.OpenScriptFilesAsync([file]);
        var tab = vm.Workspace.SelectedTab!;
        await vm.Execution.ExecuteAsync("select 1");
        Assert.NotEmpty(tab.Results);
        tab.Text = "select 1 -- typed before the move";

        Assert.True(await vm.MoveTabToProjectAsync(tab, Dir("b")));

        Assert.Equal(Path.GetFullPath(Dir("b")), vm.ProjectDirectory);
        Assert.Same(tab, vm.Workspace.SelectedTab);
        Assert.Equal(Path.GetFullPath(Dir("b")), tab.ProjectDirectory);
        Assert.Equal(b.Id, tab.ConnectionId);
        Assert.Empty(tab.Results);                                    // they were the other server's
        Assert.Equal("select 1 -- typed before the move", tab.Text);  // the buffer travels
        Assert.Equal(file, tab.ScriptPath);                           // the file does not

        // Project a no longer holds it, and will not bring it back from its session either.
        await vm.OpenProjectAsync(Dir("a"));
        Assert.DoesNotContain(tab, vm.Workspace.Tabs);
        var aSession = await new JsonSessionStore().LoadAsync(Path.GetFullPath(Dir("a")), default);
        Assert.DoesNotContain(aSession!.OpenEditors, e => e.ScriptPath == file);
    }

    [Fact]
    public async Task A_tab_with_a_query_running_does_not_move()
    {
        var gated = new GatedExecutor();
        var vm = NewVm(new FakeProvider { Executor = gated });
        await SeedAsync(vm, Dir("b"), "b");
        await SeedAsync(vm, Dir("a"), "a");
        await vm.OpenScriptFilesAsync([Outside("report.sql", "select 1")]);
        var tab = vm.Workspace.SelectedTab!;

        var run = vm.Execution.ExecuteAsync("select pg_sleep(30)");
        await WaitUntil(() => gated.Started == 1);

        Assert.False(await vm.MoveTabToProjectAsync(tab, Dir("b")));
        Assert.Equal(Path.GetFullPath(Dir("a")), vm.ProjectDirectory);
        Assert.Contains(tab, vm.Workspace.Tabs);
        Assert.Contains("still running", vm.StatusText);

        gated.Release("select pg_sleep(30)");
        await run;
    }

    [Fact]
    public async Task Moving_onto_a_tab_that_already_shows_the_file_keeps_that_tab_with_the_edits()
    {
        var vm = NewVm();
        await SeedAsync(vm, Dir("b"), "b");
        var file = Outside("report.sql", "select 1");
        await vm.OpenScriptFilesAsync([file]);
        var inB = vm.Workspace.SelectedTab!;
        await SeedAsync(vm, Dir("a"), "a");
        // Opening it again would just switch back to b, so put a second tab on it in a by hand.
        var inA = vm.Workspace.NewTab("select 1", file);
        inA.Text = "select 1 -- edited in a";

        Assert.True(await vm.MoveTabToProjectAsync(inA, Dir("b")));

        Assert.Same(inB, vm.Workspace.SelectedTab);
        Assert.Single(vm.Workspace.AllTabs, t => t.ScriptPath == file);
        Assert.Equal("select 1 -- edited in a", inB.Text);
    }

    [Fact]
    public async Task Scratch_tabs_and_a_tabs_own_project_are_not_move_targets()
    {
        var vm = NewVm();
        await SeedAsync(vm, Dir("b"), "b");
        await SeedAsync(vm, Dir("a"), "a");
        var scratch = vm.Workspace.NewTab("select 1");
        await vm.OpenScriptFilesAsync([Outside("report.sql", "select 1")]);
        var named = vm.Workspace.SelectedTab!;

        Assert.Empty(vm.ProjectsToMoveTo(scratch));
        Assert.Equal([Path.GetFullPath(Dir("b"))], vm.ProjectsToMoveTo(named).Select(p => Path.GetFullPath(p.Directory)));
    }

    // ---- launch arguments ------------------------------------------------------------------------

    [Fact]
    public void Launch_arguments_name_scripts_by_extension_resolved_against_the_working_directory()
    {
        var cwd = Path.Combine(_root, "cwd");

        var scripts = StartupScripts.From(["--demo", "a.sql", "notes.txt", "sub/B.SQL"], cwd);

        Assert.Equal([Path.Combine(cwd, "a.sql"), Path.Combine(cwd, "sub", "B.SQL")], scripts);
        Assert.Empty(StartupScripts.From(null));
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }
}
