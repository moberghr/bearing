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
/// Removing a project deliberately. Pruning a folder that has <em>gone</em> already self-heals
/// (<see cref="RecentProjectsPruneTests"/>); this is the other half — a project that is still there and the
/// user wants rid of, either forgotten or deleted outright.
/// </summary>
public class ProjectRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-remove", Guid.NewGuid().ToString("N"));
    private readonly FakeSecretStore _secrets = new();

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private string RecentPath => Path.Combine(_root, "recent.json");
    private FileRecentProjects Recent => new(RecentPath);

    private ShellViewModel NewVm() => new(
        new ProviderRegistry(),
        new JsonProjectStore(),
        new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(RecentPath),
        _secrets,
        settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));

    /// <summary>A real project on disk that the shell has never opened, so it is closed but listed.</summary>
    private async Task<string> ClosedProject(string name)
    {
        var dir = Path.Combine(_root, name);
        await new JsonProjectStore().CreateAsync(dir, name, CancellationToken.None);
        await Recent.AddAsync(dir, CancellationToken.None);
        return Path.GetFullPath(dir);
    }

    /// <summary>A shell with <paramref name="name"/> open, plus a second listed-but-closed project.</summary>
    private async Task<(ShellViewModel Vm, string Closed)> Shell(string name = "open")
    {
        var closed = await ClosedProject("closed");
        var vm = NewVm();
        await vm.InitializeAsync(Path.Combine(_root, name));
        return (vm, closed);
    }

    [Fact]
    public async Task Removing_from_the_list_forgets_the_project_and_leaves_every_file_alone()
    {
        var (vm, closed) = await Shell();

        Assert.True(await vm.RemoveRecentProjectAsync(closed, deleteFromDisk: false));

        Assert.DoesNotContain(vm.RecentProjects, p => p.Directory == closed);
        Assert.DoesNotContain(closed, await Recent.ListAsync(CancellationToken.None));
        Assert.True(Directory.Exists(closed));                                   // forgotten, not deleted
        Assert.True(File.Exists(Path.Combine(closed, "project.json")));
    }

    [Fact]
    public async Task Deleting_takes_the_folder_and_the_list_entry()
    {
        var (vm, closed) = await Shell();
        await File.WriteAllTextAsync(Path.Combine(closed, "scripts", "a.sql"), "select 1;");

        Assert.True(await vm.RemoveRecentProjectAsync(closed, deleteFromDisk: true));

        Assert.False(Directory.Exists(closed));
        Assert.DoesNotContain(vm.RecentProjects, p => p.Directory == closed);
        Assert.DoesNotContain(closed, await Recent.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_open_project_is_refused_rather_than_deleted_under_its_own_tabs()
    {
        var (vm, _) = await Shell();
        var open = vm.ProjectDirectory!;

        Assert.False(await vm.RemoveRecentProjectAsync(open, deleteFromDisk: true));

        Assert.True(Directory.Exists(open));
        Assert.Contains(vm.RecentProjects, p => p.Directory == open);
        Assert.Contains("switch to another project", vm.StatusText);
    }

    [Fact]
    public async Task A_parked_project_counts_as_open_too()
    {
        // Switching parks the outgoing project's tabs rather than closing them, so its files are still live.
        var (vm, _) = await Shell("first");
        var first = vm.ProjectDirectory!;
        await vm.OpenProjectAsync(Path.Combine(_root, "second"));
        Assert.NotEqual(first, vm.ProjectDirectory);

        Assert.False(await vm.RemoveRecentProjectAsync(first, deleteFromDisk: false));
        Assert.True(Directory.Exists(first));
    }

    [Fact]
    public async Task Only_closed_projects_are_offered_for_removal()
    {
        var (vm, closed) = await Shell();

        Assert.Equal(new[] { closed }, vm.RemovableProjects.Select(p => p.Directory));
    }

    [Fact]
    public async Task Deleting_something_that_is_no_longer_a_project_is_refused_not_wiped()
    {
        // The stored entry can outlive the project: the folder is still there but its manifest is gone. A
        // recursive delete on that is a delete of whatever the folder has become.
        var (vm, closed) = await Shell();
        File.Delete(Path.Combine(closed, "project.json"));
        await File.WriteAllTextAsync(Path.Combine(closed, "someone-elses-file.txt"), "keep me");

        Assert.False(await vm.RemoveRecentProjectAsync(closed, deleteFromDisk: true));

        Assert.True(File.Exists(Path.Combine(closed, "someone-elses-file.txt")));
        Assert.Contains("Could not delete", vm.StatusText);
        // Still listed: the user asked for a delete, didn't get one, and must not be told it's gone.
        Assert.Contains(closed, await Recent.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Forgetting_still_works_for_a_folder_that_is_no_longer_a_project()
    {
        var (vm, closed) = await Shell();
        File.Delete(Path.Combine(closed, "project.json"));

        Assert.True(await vm.RemoveRecentProjectAsync(closed, deleteFromDisk: false));
        Assert.DoesNotContain(closed, await Recent.ListAsync(CancellationToken.None));
        Assert.True(Directory.Exists(closed));
    }
}
