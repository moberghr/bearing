using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.ViewModels;
using Bearing.Data;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Where the project browser opens. Projects are directories sitting next to each other, so "the projects
/// folder" is the parent of the one you're in — the picker used to start at whatever the platform considers
/// home, which is never where the projects are.
/// </summary>
public class ProjectBrowseDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-browse", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private ShellViewModel NewVm() => new(
        new ProviderRegistry(), new JsonProjectStore(), new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(Path.Combine(_root, "recent.json")),
        new FakeSecretStore());

    [Fact]
    public async Task It_is_the_folder_holding_the_open_project()
    {
        var projects = Path.Combine(_root, "my-projects");
        var vm = NewVm();
        await vm.InitializeAsync(Path.Combine(projects, "alpha"));

        Assert.Equal(Path.GetFullPath(projects), vm.ProjectBrowseDirectory);
    }

    [Fact]
    public async Task It_follows_a_project_switch()
    {
        var vm = NewVm();
        await vm.InitializeAsync(Path.Combine(_root, "first-place", "alpha"));

        var elsewhere = Path.Combine(_root, "second-place");
        await vm.OpenProjectAsync(Path.Combine(elsewhere, "beta"));

        Assert.Equal(Path.GetFullPath(elsewhere), vm.ProjectBrowseDirectory);
    }

    [Fact]
    public async Task Before_a_project_is_open_it_is_the_startup_defaults_folder()
    {
        // Nothing has been opened yet, but the app already knows where its default project lives.
        var defaults = Path.Combine(_root, "app-data", "projects");
        Directory.CreateDirectory(defaults);
        var recent = new FileRecentProjects(Path.Combine(_root, "recent.json"));
        await recent.AddAsync(Path.Combine(_root, "gone"), CancellationToken.None);   // never created

        var vm = NewVm();
        await vm.ResumeLastProjectAsync(Path.Combine(defaults, "default"));

        Assert.Equal(Path.GetFullPath(defaults), vm.ProjectBrowseDirectory);
    }

    [Fact]
    public void With_nothing_known_at_all_the_picker_is_left_to_choose()
    {
        Assert.Null(NewVm().ProjectBrowseDirectory);
    }
}
