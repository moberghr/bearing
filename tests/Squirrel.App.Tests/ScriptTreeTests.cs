using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Squirrel.App.ViewModels;
using Squirrel.Data;
using Squirrel.Persistence;
using Xunit;

namespace Squirrel.App.Tests;

/// <summary>Scripts side-panel folder tree: subdirectories become folders, root files stay ungrouped,
/// and the filter narrows both. Pure filesystem — no database required.</summary>
public class ScriptTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "squirrel-scripts", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private MainWindowViewModel NewVm() => new(
        new ProviderRegistry(), new JsonProjectStore(), new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(Path.Combine(_root, "recent.json")),
        new FileFallbackSecretStore(Path.Combine(_root, "secrets")));

    [Fact]
    public async Task Scripts_group_into_folders_and_filter()
    {
        var dir = Path.Combine(_root, "proj");
        var vm = NewVm();
        await vm.InitializeAsync(dir);

        var scripts = vm.ScriptsDirectory!;
        Directory.CreateDirectory(Path.Combine(scripts, "Reports"));
        await File.WriteAllTextAsync(Path.Combine(scripts, "Reports", "alpha.sql"), "select 1;");
        await File.WriteAllTextAsync(Path.Combine(scripts, "Reports", "beta.sql"), "select 2;");
        await File.WriteAllTextAsync(Path.Combine(scripts, "root.sql"), "select 3;");

        // Trigger a refresh (filter setter re-reads the tree).
        vm.ScriptFilter = "alpha";
        var folder = Assert.IsType<ScriptFolderViewModel>(Assert.Single(vm.ScriptNodes)); // only Reports matches
        Assert.Equal("Reports", folder.Name);
        Assert.Equal("alpha.sql", Assert.Single(folder.Scripts).Name);

        vm.ScriptFilter = "";
        Assert.Equal(2, vm.ScriptNodes.Count);                                  // Reports folder + root.sql
        var reports = vm.ScriptNodes.OfType<ScriptFolderViewModel>().Single();
        Assert.Equal(2, reports.Count);
        Assert.Contains(vm.ScriptNodes.OfType<ScriptItem>(), s => s.Name == "root.sql");
        Assert.Equal(3, vm.Scripts.Count);                                      // flat list has all three
    }
}
