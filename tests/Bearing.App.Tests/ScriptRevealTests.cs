using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Data;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// "Reveal in Scripts" — the tab context menu's answer to "which file is this tab?". Covers the pure tree
/// walk and the view-model state it drives (panel, expansion, selection); what the TreeView then paints is
/// eyeball-QA (§4.3).
/// </summary>
public class ScriptRevealTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-reveal", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private ShellViewModel NewVm() => new(
        new ProviderRegistry(), new JsonProjectStore(), new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(Path.Combine(_root, "recent.json")),
        new FakeSecretStore());

    private async Task<ShellViewModel> Project([System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var vm = NewVm();
        await vm.InitializeAsync(Path.Combine(_root, name));
        return vm;
    }

    private static async Task<string> Script(string dir, string name, string text = "select 1;")
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    // ---- the pure walk -------------------------------------------------------------------------

    private static ScriptFolderViewModel Folder(string path, params object[] children)
    {
        var folder = new ScriptFolderViewModel(Path.GetFileName(path), path) { IsExpanded = false };
        foreach (var c in children) folder.Children.Add(c);
        return folder;
    }

    [Fact]
    public void The_walk_returns_the_folders_above_a_file_outermost_first()
    {
        var leaf = new ScriptItem("jan.sql", Join("p", "scripts", "Reports", "Monthly", "jan.sql"));
        var monthly = Folder(Join("p", "scripts", "Reports", "Monthly"), leaf);
        var reports = Folder(Join("p", "scripts", "Reports"), monthly);
        var roots = new object[] { reports, new ScriptItem("root.sql", Join("p", "scripts", "root.sql")) };

        var chain = ScriptTreeReveal.PathTo(roots, leaf.FullPath);

        Assert.Equal(new object[] { reports, monthly, leaf }, chain);
    }

    [Fact]
    public void A_root_level_file_is_its_own_whole_chain()
    {
        var item = new ScriptItem("root.sql", Join("p", "scripts", "root.sql"));
        Assert.Equal(new object[] { item }, ScriptTreeReveal.PathTo(new object[] { item }, item.FullPath));
    }

    [Fact]
    public void A_folder_path_resolves_to_the_folder_itself()
    {
        var reports = Folder(Join("p", "scripts", "Reports"));
        Assert.Equal(new object[] { reports }, ScriptTreeReveal.PathTo(new object[] { reports }, reports.FullPath));
    }

    [Fact]
    public void A_path_the_tree_doesnt_hold_walks_out_empty()
    {
        var reports = Folder(Join("p", "scripts", "Reports"), new ScriptItem("a.sql", Join("p", "scripts", "Reports", "a.sql")));
        Assert.Empty(ScriptTreeReveal.PathTo(new object[] { reports }, Join("p", "scripts", "elsewhere.sql")));
    }

    [Fact]
    public void A_dead_end_folder_is_popped_before_trying_the_next_one()
    {
        // The branch that doesn't contain the file must not be left in the chain.
        var wrong = Folder(Join("p", "scripts", "Wrong"), new ScriptItem("x.sql", Join("p", "scripts", "Wrong", "x.sql")));
        var right = Folder(Join("p", "scripts", "Right"), new ScriptItem("y.sql", Join("p", "scripts", "Right", "y.sql")));

        var chain = ScriptTreeReveal.PathTo(new object[] { wrong, right }, Join("p", "scripts", "Right", "y.sql"));

        Assert.Equal(2, chain.Count);
        Assert.Same(right, chain[0]);
    }

    private static string Join(params string[] parts) => Path.GetFullPath(Path.Combine(parts));

    // ---- through the view model ----------------------------------------------------------------

    [Fact]
    public async Task Revealing_shows_the_panel_expands_the_folders_and_selects_the_file()
    {
        var vm = await Project();
        var path = await Script(Path.Combine(vm.ScriptsDirectory!, "Reports", "Monthly"), "jan.sql");
        vm.Scripts.RefreshScripts();
        vm.ActivePanel = SidePanel.Schema;
        vm.SidePaneOpen = false;

        Assert.True(vm.RevealScript(path));

        Assert.Equal(SidePanel.Scripts, vm.ActivePanel);
        Assert.True(vm.SidePaneOpen);
        Assert.Equal(path, Assert.IsType<ScriptItem>(vm.Scripts.SelectedNode).FullPath);
        Assert.All(Folders(vm), f => Assert.True(f.IsExpanded));
    }

    [Fact]
    public async Task Revealing_a_scratch_file_expands_the_collapsed_scratch_folder()
    {
        // The scratch folder is collapsed by default, so revealing into it would otherwise select a node
        // nobody can see — and a scratch tab is exactly the case where "which file is this?" is asked.
        var vm = await Project();
        var path = await Script(Path.Combine(vm.ScriptsDirectory!, "scratch"), "2026-08-19-01.sql");
        vm.Scripts.RefreshScripts();
        var scratch = vm.Scripts.ScriptNodes.OfType<ScriptFolderViewModel>().Single(f => f.IsScratch);
        Assert.False(scratch.IsExpanded);

        Assert.True(vm.RevealScript(path));

        Assert.True(scratch.IsExpanded);
        Assert.Equal(path, Assert.IsType<ScriptItem>(vm.Scripts.SelectedNode).FullPath);
    }

    [Fact]
    public async Task Revealing_clears_a_filter_that_is_hiding_the_file()
    {
        var vm = await Project();
        var path = await Script(vm.ScriptsDirectory!, "target.sql");
        vm.Scripts.ScriptFilter = "something else";
        Assert.Empty(vm.Scripts.ScriptNodes.OfType<ScriptItem>());

        Assert.True(vm.RevealScript(path));

        Assert.Equal("", vm.Scripts.ScriptFilter);
        Assert.Equal(path, Assert.IsType<ScriptItem>(vm.Scripts.SelectedNode).FullPath);
    }

    [Fact]
    public async Task Revealing_picks_up_a_file_the_tree_has_not_seen_yet()
    {
        // Files can appear behind the tree's back (written outside the app), and reveal is the one moment
        // where "the tree is stale" would read as "the file isn't there".
        var vm = await Project();
        var path = await Script(vm.ScriptsDirectory!, "appeared.sql");

        Assert.True(vm.RevealScript(path));
        Assert.Equal(path, Assert.IsType<ScriptItem>(vm.Scripts.SelectedNode).FullPath);
    }

    [Fact]
    public async Task Revealing_a_file_outside_the_project_reports_rather_than_failing_silently()
    {
        var vm = await Project();
        var outside = Path.Combine(_root, "not-in-the-project.sql");

        Assert.False(vm.RevealScript(outside));

        Assert.Contains("not-in-the-project.sql", vm.StatusText);
        Assert.Equal(SidePanel.Scripts, vm.ActivePanel);   // the panel still opens; there's just nothing to select
    }

    [Fact]
    public async Task The_selection_survives_the_wholesale_tree_rebuild()
    {
        // RefreshScripts rebuilds every node, so a reveal that didn't re-resolve by path would be undone by
        // the next file to appear.
        var vm = await Project();
        var path = await Script(Path.Combine(vm.ScriptsDirectory!, "Reports"), "keep.sql");
        vm.Scripts.RefreshScripts();
        vm.RevealScript(path);

        await Script(vm.ScriptsDirectory!, "newcomer.sql");
        vm.Scripts.RefreshScripts();

        Assert.Equal(path, Assert.IsType<ScriptItem>(vm.Scripts.SelectedNode).FullPath);
    }

    [Fact]
    public async Task A_selection_whose_file_has_gone_is_dropped_rather_than_kept_stale()
    {
        var vm = await Project();
        var path = await Script(vm.ScriptsDirectory!, "doomed.sql");
        vm.Scripts.RefreshScripts();
        vm.RevealScript(path);

        File.Delete(path);
        vm.Scripts.RefreshScripts();

        Assert.Null(vm.Scripts.SelectedNode);
    }

    private static ScriptFolderViewModel[] Folders(ShellViewModel vm)
    {
        var found = new System.Collections.Generic.List<ScriptFolderViewModel>();
        void Walk(System.Collections.Generic.IEnumerable<object> nodes)
        {
            foreach (var n in nodes.OfType<ScriptFolderViewModel>())
            {
                if (n.IsScratch) continue;   // pinned and deliberately collapsed unless revealed into
                found.Add(n);
                Walk(n.Children);
            }
        }
        Walk(vm.Scripts.ScriptNodes);
        return found.ToArray();
    }
}
