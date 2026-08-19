using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Removing the project you're in. Pruning a folder that has <em>gone</em> already self-heals
/// (<see cref="RecentProjectsPruneTests"/>); this is the other half — a project that is still there and the
/// user is done with, either forgotten or deleted outright. There is always a project on screen, so a
/// removal is also a switch.
/// </summary>
public class ProjectRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-remove", Guid.NewGuid().ToString("N"));
    private readonly FakeSecretStore _secrets = new();

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private string RecentPath => Path.Combine(_root, "recent.json");
    private FileRecentProjects Recent => new(RecentPath);
    private string Fallback => Path.Combine(_root, "fallback");

    private ShellViewModel NewVm() => new(
        new ProviderRegistry(),
        new JsonProjectStore(),
        new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(RecentPath),
        _secrets,
        settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));

    /// <summary>A real project on disk, listed as recent but never opened by the shell.</summary>
    private async Task<string> ListedProject(string name)
    {
        var dir = Path.Combine(_root, name);
        await new JsonProjectStore().CreateAsync(dir, name, CancellationToken.None);
        await Recent.AddAsync(dir, CancellationToken.None);
        return Path.GetFullPath(dir);
    }

    /// <summary>A shell resumed the way the app does it (so a fallback project directory is known), sitting in
    /// <c>current</c>, with <c>other</c> listed behind it as the successor.</summary>
    private async Task<(ShellViewModel Vm, string Current, string Other)> Shell()
    {
        var other = await ListedProject("other");
        var current = await ListedProject("current");   // added last, so it's the most recent
        var vm = NewVm();
        await vm.ResumeLastProjectAsync(Fallback);
        Assert.Equal(current, vm.ProjectDirectory);
        return (vm, current, other);
    }

    [Fact]
    public async Task Forgetting_drops_the_entry_switches_away_and_leaves_every_file_alone()
    {
        var (vm, current, other) = await Shell();

        Assert.True(await vm.RemoveCurrentProjectAsync(deleteFromDisk: false));

        Assert.Equal(other, vm.ProjectDirectory);                                 // switched, not left blank
        Assert.DoesNotContain(vm.RecentProjects, p => p.Directory == current);
        Assert.DoesNotContain(current, await Recent.ListAsync(CancellationToken.None));
        Assert.True(Directory.Exists(current));                                    // forgotten, not deleted
        Assert.True(File.Exists(Path.Combine(current, "project.json")));
    }

    [Fact]
    public async Task Forgetting_saves_the_session_so_reopening_the_folder_resumes()
    {
        var (vm, current, _) = await Shell();
        vm.Workspace.SelectedTab!.Text = "select 'still here';";

        Assert.True(await vm.RemoveCurrentProjectAsync(deleteFromDisk: false));

        var session = await new JsonSessionStore().LoadAsync(current, CancellationToken.None);
        Assert.Contains(session!.OpenEditors, e => e.ScratchText == "select 'still here';");
    }

    [Fact]
    public async Task Deleting_takes_the_folder_the_entry_and_the_open_tabs()
    {
        var (vm, current, other) = await Shell();
        await File.WriteAllTextAsync(Path.Combine(current, "scripts", "a.sql"), "select 1;");

        Assert.True(await vm.RemoveCurrentProjectAsync(deleteFromDisk: true));

        Assert.False(Directory.Exists(current));
        Assert.Equal(other, vm.ProjectDirectory);
        Assert.DoesNotContain(current, await Recent.ListAsync(CancellationToken.None));
        // The tabs on screen belong to the project we landed in, not the one that's gone.
        Assert.All(vm.Workspace.Tabs, t => Assert.Equal(other, t.ProjectDirectory));
    }

    [Fact]
    public async Task Deleting_discards_a_pending_autosave_rather_than_recreating_the_folder()
    {
        // The debounced write is the one thing that can put a file back inside a folder being deleted.
        var recent = Recent;
        var other = await ListedProject("other");
        var current = await ListedProject("current");
        var vm = new ShellViewModel(
            new ProviderRegistry(), new JsonProjectStore(), new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(RecentPath), _secrets,
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.OnEdit }));
        await vm.ResumeLastProjectAsync(Fallback);
        vm.Workspace.SelectedTab!.Text = "select 'pending';";   // schedules a debounced scratch write

        Assert.True(await vm.RemoveCurrentProjectAsync(deleteFromDisk: true));
        await Task.Delay(900);   // longer than the autosave debounce

        Assert.False(Directory.Exists(current));
        Assert.Equal(other, vm.ProjectDirectory);
        Assert.DoesNotContain(current, await recent.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task With_nothing_else_to_switch_to_the_removal_is_refused()
    {
        // The app always has a project on screen, so "remove the only one" has nowhere to land.
        var only = Path.Combine(_root, "only");
        var vm = NewVm();
        await vm.InitializeAsync(only);   // no ResumeLastProjectAsync → no fallback known either

        Assert.False(await vm.RemoveCurrentProjectAsync(deleteFromDisk: true));

        Assert.Equal(Path.GetFullPath(only), vm.ProjectDirectory);
        Assert.True(Directory.Exists(only));
        Assert.Contains("only project", vm.StatusText);
    }

    [Fact]
    public async Task The_startup_default_is_the_last_resort_successor()
    {
        var current = await ListedProject("current");
        var vm = NewVm();
        await vm.ResumeLastProjectAsync(Fallback);
        Assert.Equal(current, vm.ProjectDirectory);

        Assert.True(await vm.RemoveCurrentProjectAsync(deleteFromDisk: true));

        Assert.Equal(Path.GetFullPath(Fallback), vm.ProjectDirectory);   // created on the way in
        Assert.False(Directory.Exists(current));
    }

    [Fact]
    public async Task A_folder_that_is_no_longer_a_project_is_refused_not_wiped()
    {
        // A recursive delete on a folder whose manifest has gone is a delete of whatever it has become.
        var (vm, current, _) = await Shell();
        File.Delete(Path.Combine(current, "project.json"));
        await File.WriteAllTextAsync(Path.Combine(current, "someone-elses-file.txt"), "keep me");

        Assert.False(await vm.RemoveCurrentProjectAsync(deleteFromDisk: true));

        Assert.True(File.Exists(Path.Combine(current, "someone-elses-file.txt")));
        Assert.Contains("Could not delete", vm.StatusText);
        // Nothing was closed or forgotten either: a failed delete leaves the project entirely usable.
        Assert.Equal(current, vm.ProjectDirectory);
        Assert.Contains(current, await Recent.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_forgets_the_stored_passwords_of_connections_that_can_never_be_used_again()
    {
        var (vm, current, _) = await Shell();
        var connection = new Bearing.Core.Data.ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "gone",
            ProviderId = "postgres",
            Host = "localhost",
            Database = "db",
            User = "u",
        };
        await vm.Connections.AddOrUpdateConnectionAsync(connection, "secret");
        Assert.NotNull(await _secrets.GetPasswordAsync(connection.Id, CancellationToken.None));

        Assert.True(await vm.RemoveCurrentProjectAsync(deleteFromDisk: true));

        Assert.Null(await _secrets.GetPasswordAsync(connection.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Forgetting_keeps_the_stored_passwords_the_project_may_still_be_reopened()
    {
        var (vm, _, _) = await Shell();
        var connection = new Bearing.Core.Data.ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "kept",
            ProviderId = "postgres",
            Host = "localhost",
            Database = "db",
            User = "u",
        };
        await vm.Connections.AddOrUpdateConnectionAsync(connection, "secret");

        Assert.True(await vm.RemoveCurrentProjectAsync(deleteFromDisk: false));

        Assert.Equal("secret", await _secrets.GetPasswordAsync(connection.Id, CancellationToken.None));
    }
}
