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
/// The recent-projects list used to keep offering folders that no longer exist: resume already skipped them,
/// but the switcher still listed them, and clicking one recreated an empty project at that path.
/// </summary>
public class RecentProjectsPruneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-recent", Guid.NewGuid().ToString("N"));

    /// <summary>One store for the whole test, so a secret saved through one view model still
    /// resolves through the next — the on-disk store this replaced was shared the same way.</summary>
    private readonly FakeSecretStore _secrets = new();

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private string RecentPath => Path.Combine(_root, "recent.json");

    private ShellViewModel NewVm() => new(
        new ProviderRegistry(),
        new JsonProjectStore(),
        new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(RecentPath),
        _secrets,
        settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));

    [Fact]
    public async Task A_missing_project_directory_is_dropped_from_the_list_and_from_the_store()
    {
        var recent = new FileRecentProjects(RecentPath);
        var gone = Path.Combine(_root, "deleted-project");
        var live = Path.Combine(_root, "live-project");
        await recent.AddAsync(gone, CancellationToken.None);      // never created on disk
        Directory.CreateDirectory(live);

        var vm = NewVm();
        await vm.InitializeAsync(live);   // opening a project rebuilds the recent list

        // The dead entry is neither offered …
        Assert.DoesNotContain(vm.RecentProjects, p => p.Directory == Path.GetFullPath(gone));
        Assert.Contains(vm.RecentProjects, p => p.Directory == Path.GetFullPath(live));

        // … nor left in the file to be re-checked on every refresh.
        var stored = await recent.ListAsync(CancellationToken.None);
        Assert.DoesNotContain(Path.GetFullPath(gone), stored);
        Assert.Contains(Path.GetFullPath(live), stored);
    }

    [Fact]
    public async Task An_existing_project_that_simply_has_no_manifest_is_still_listed()
    {
        // Pruning is on directory existence only: a folder that exists but isn't a project yet keeps its
        // entry (it resolves to the folder name), so a project on a temporarily-unmounted path or one whose
        // manifest is being rewritten isn't silently forgotten.
        var bare = Path.Combine(_root, "bare-folder");
        Directory.CreateDirectory(bare);
        var recent = new FileRecentProjects(RecentPath);
        await recent.AddAsync(bare, CancellationToken.None);

        var live = Path.Combine(_root, "live");
        Directory.CreateDirectory(live);
        var vm = NewVm();
        await vm.InitializeAsync(live);

        Assert.Contains(vm.RecentProjects, p => p.Directory == Path.GetFullPath(bare));
        Assert.Equal("bare-folder", vm.RecentProjects.First(p => p.Directory == Path.GetFullPath(bare)).Name);
    }
}
