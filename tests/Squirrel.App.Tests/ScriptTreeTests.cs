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

    private ShellViewModel NewVm() => new(
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
        vm.Scripts.ScriptFilter = "alpha";
        var folder = Assert.IsType<ScriptFolderViewModel>(Assert.Single(vm.Scripts.ScriptNodes)); // only Reports matches
        Assert.Equal("Reports", folder.Name);
        Assert.Equal("alpha.sql", Assert.Single(folder.Children.OfType<ScriptItem>()).Name);

        vm.Scripts.ScriptFilter = "";
        Assert.Equal(2, vm.Scripts.ScriptNodes.Count);                                  // Reports folder + root.sql
        var reports = vm.Scripts.ScriptNodes.OfType<ScriptFolderViewModel>().Single();
        Assert.Equal(2, reports.Count);
        Assert.Contains(vm.Scripts.ScriptNodes.OfType<ScriptItem>(), s => s.Name == "root.sql");
        Assert.Equal(3, vm.Scripts.Scripts.Count);                                      // flat list has all three
    }

    [Fact]
    public async Task Nested_subfolders_are_shown_recursively_and_empty_folders_kept()
    {
        var dir = Path.Combine(_root, "proj");
        var vm = NewVm();
        await vm.InitializeAsync(dir);
        var scripts = vm.ScriptsDirectory!;

        Directory.CreateDirectory(Path.Combine(scripts, "Reports", "Monthly"));
        Directory.CreateDirectory(Path.Combine(scripts, "Empty"));
        await File.WriteAllTextAsync(Path.Combine(scripts, "Reports", "top.sql"), "select 1;");
        await File.WriteAllTextAsync(Path.Combine(scripts, "Reports", "Monthly", "jan.sql"), "select 2;");

        vm.Scripts.ScriptFilter = "x"; vm.Scripts.ScriptFilter = ""; // force refresh

        var reports = vm.Scripts.ScriptNodes.OfType<ScriptFolderViewModel>().Single(f => f.Name == "Reports");
        Assert.Equal(2, reports.Count);                                        // top.sql + Monthly/jan.sql
        var monthly = reports.Children.OfType<ScriptFolderViewModel>().Single(f => f.Name == "Monthly");
        Assert.Equal("jan.sql", Assert.Single(monthly.Children.OfType<ScriptItem>()).Name);
        Assert.Contains(vm.Scripts.ScriptNodes.OfType<ScriptFolderViewModel>(), f => f.Name == "Empty"); // empty folder kept
    }

    [Fact]
    public async Task MoveScript_relocates_the_file_on_disk()
    {
        var dir = Path.Combine(_root, "proj");
        var vm = NewVm();
        await vm.InitializeAsync(dir);
        var scripts = vm.ScriptsDirectory!;
        Directory.CreateDirectory(Path.Combine(scripts, "Target"));
        var src = Path.Combine(scripts, "loose.sql");
        await File.WriteAllTextAsync(src, "select 1;");

        vm.Scripts.MoveScript(src, Path.Combine(scripts, "Target"));

        Assert.False(File.Exists(src));
        Assert.True(File.Exists(Path.Combine(scripts, "Target", "loose.sql")));
    }
}
