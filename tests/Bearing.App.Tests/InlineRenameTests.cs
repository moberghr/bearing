using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.ViewModels;
using Bearing.Data;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Renaming in place. A modal prompt for a one-word edit on a name already on screen was the whole problem,
/// so the label becomes a box on the row itself — for a tab header and for a script in the tree. The keys
/// (Enter commits, Esc cancels, clicking away commits) are wired in the two code-behinds and are eyeball-QA
/// (§4.3); what's pinned here is the state they drive, and above all what the box is <em>seeded</em> with,
/// since that is the text the user edits rather than retypes.
/// </summary>
public class InlineRenameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-rename", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private async Task<ShellViewModel> Project([System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var vm = new ShellViewModel(
            new ProviderRegistry(), new JsonProjectStore(), new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore());
        await vm.InitializeAsync(Path.Combine(_root, name));
        return vm;
    }

    // ---- tab headers ----

    [Fact]
    public void A_file_backed_tab_is_seeded_with_its_file_name_without_the_extension()
    {
        var tab = new EditorTabViewModel("Scratch 1", scriptPath: Path.Combine("p", "scripts", "weekly report.sql"));

        tab.BeginRename();

        Assert.True(tab.IsRenaming);
        Assert.Equal("weekly report", tab.RenameDraft);   // the .sql is not the user's to retype
    }

    [Fact]
    public void A_tab_with_no_file_is_seeded_with_its_placeholder_label()
    {
        var tab = new EditorTabViewModel("Scratch 3", isScratch: true);

        tab.BeginRename();

        Assert.Equal("Scratch 3", tab.RenameDraft);
    }

    [Fact]
    public void Reopening_the_editor_re_seeds_from_the_current_name_not_the_last_edit()
    {
        // An abandoned edit must not come back the next time the box opens.
        var tab = new EditorTabViewModel("Scratch 1", scriptPath: Path.Combine("p", "a.sql"));
        tab.BeginRename();
        tab.RenameDraft = "half-typed";
        tab.IsRenaming = false;      // Esc

        tab.BeginRename();

        Assert.Equal("a", tab.RenameDraft);
    }

    [Fact]
    public async Task Committing_a_scratch_tabs_new_name_still_promotes_it()
    {
        // The inline box replaced the dialog, not the behaviour behind it.
        var vm = await Project();
        var tab = vm.Workspace.SelectedTab!;
        tab.Text = "select 1;";
        for (var i = 0; i < 60 && tab.ScriptPath is null; i++) await Task.Delay(50);

        tab.BeginRename();
        await vm.Workspace.RenameTabAsync(tab, tab.RenameDraft + " renamed");

        Assert.False(tab.IsScratch);
        Assert.Equal(Path.Combine(vm.ScriptsDirectory!, Path.GetFileName(tab.ScriptPath!)), tab.ScriptPath);
        Assert.EndsWith(" renamed.sql", tab.Header);
    }

    // ---- script rows ----

    [Fact]
    public void A_script_row_is_seeded_with_its_name_without_the_extension()
    {
        var script = new ScriptItem("monthly.sql", Path.Combine("p", "scripts", "monthly.sql"));

        script.BeginRename();

        Assert.True(script.IsRenaming);
        Assert.Equal("monthly", script.RenameDraft);
    }

    [Fact]
    public async Task A_renamed_script_row_is_gone_after_the_refresh_that_follows()
    {
        // The tree is rebuilt wholesale by the rename, so the editing row is replaced rather than left
        // sitting there in edit mode under a stale name.
        var vm = await Project();
        var path = Path.Combine(vm.ScriptsDirectory!, "before.sql");
        await File.WriteAllTextAsync(path, "select 1;");
        vm.Scripts.RefreshScripts();
        var row = vm.Scripts.Scripts.Single(s => s.Name == "before.sql");
        row.BeginRename();

        await vm.Scripts.RenameScriptAsync(row.FullPath, "after");

        Assert.DoesNotContain(vm.Scripts.Scripts, s => s.Name == "before.sql");
        var renamed = Assert.Single(vm.Scripts.Scripts, s => s.Name == "after.sql");
        Assert.False(renamed.IsRenaming);
        Assert.Equal("after", renamed.RenameDraft);   // seeded ready for the next edit
    }

    [Fact]
    public async Task A_name_that_clashes_leaves_the_file_alone_and_says_so()
    {
        var vm = await Project();
        await File.WriteAllTextAsync(Path.Combine(vm.ScriptsDirectory!, "taken.sql"), "-- theirs");
        var path = Path.Combine(vm.ScriptsDirectory!, "mine.sql");
        await File.WriteAllTextAsync(path, "-- mine");
        vm.Scripts.RefreshScripts();

        await vm.Scripts.RenameScriptAsync(path, "taken");

        Assert.True(File.Exists(path));
        Assert.Equal("-- theirs", await File.ReadAllTextAsync(Path.Combine(vm.ScriptsDirectory!, "taken.sql")));
        Assert.Contains("already exists", vm.StatusText);
    }
}
