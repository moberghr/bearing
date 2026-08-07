using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Services;
using Bearing.App.ViewModels;
using Bearing.Data;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Closing a tab must never silently drop work (roadmap: scratch scripts phase 1). Covers the gate
/// (<see cref="EditorTabViewModel.HasUnsavedWork"/>) and each of the three outcomes on both kinds of tab.
/// </summary>
public class CloseTabPromptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-close", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private ShellViewModel NewVm(IDialogService dialogs) => new(
        new ProviderRegistry(),
        new JsonProjectStore(),
        new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(Path.Combine(_root, "recent.json")),
        dialogs: dialogs);

    private async Task<ShellViewModel> Project(IDialogService dialogs, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var vm = NewVm(dialogs);
        await vm.InitializeAsync(Path.Combine(_root, name));
        return vm;
    }

    // ---- the gate ----

    [Fact]
    public async Task Empty_scratch_tab_closes_without_a_prompt()
    {
        var dialogs = new FakeDialogs();
        var vm = await Project(dialogs);
        var extra = vm.Workspace.NewTab();
        extra.Text = "   \n  ";   // whitespace only — closing an untouched tab must stay one keystroke

        Assert.False(extra.HasUnsavedWork);
        Assert.True(await vm.Workspace.CloseTabAsync(extra));
        Assert.Empty(dialogs.ClosePrompts);
        Assert.DoesNotContain(extra, vm.Workspace.Tabs);
    }

    [Fact]
    public async Task Saved_script_with_no_edits_closes_without_a_prompt()
    {
        var dialogs = new FakeDialogs();
        var vm = await Project(dialogs);
        vm.Workspace.NewTab();  // keep a second tab so the closed one isn't auto-replaced
        var tab = vm.Workspace.Tabs[0];
        await vm.Workspace.SaveScriptAsync(tab, Path.Combine(vm.ScriptsDirectory!, "clean.sql"), "select 1;");

        Assert.False(tab.HasUnsavedWork);
        Assert.True(await vm.Workspace.CloseTabAsync(tab));
        Assert.Empty(dialogs.ClosePrompts);
    }

    // ---- scratch ----
    //
    // Since phase 2 a scratch tab is backed by a real file, so closing one loses nothing and must NOT
    // prompt — CloseTabAsync flushes the pending write instead. That path is covered in ScratchFileTests.
    // What remains here is the backstop: a scratch buffer that could not reach a file at all (no project,
    // or a failed write) still has to be caught, because its text exists nowhere else.

    [Fact]
    public async Task Scratch_that_never_reached_a_file_still_prompts()
    {
        var dialogs = new FakeDialogs(CloseChoice.Cancel);
        var vm = NewVm(dialogs);          // deliberately no project → autosave has nowhere to write
        var tab = vm.Workspace.NewTab();
        tab.Text = "select 42;";

        Assert.Null(tab.ScriptPath);
        Assert.True(tab.HasUnsavedWork);
        Assert.False(await vm.Workspace.CloseTabAsync(tab));
        Assert.Equal(tab.Header, Assert.Single(dialogs.ClosePrompts));   // prompt named the tab
        Assert.Contains(tab, vm.Workspace.Tabs);
        Assert.Equal("select 42;", tab.Text);
    }

    [Fact]
    public async Task Saving_unbacked_scratch_writes_the_picked_file_then_closes()
    {
        var vm0 = await Project(new FakeDialogs());
        var dest = Path.Combine(vm0.ScriptsDirectory!, "picked.sql");

        var dialogs = new FakeDialogs(CloseChoice.Save, saveAsPath: dest);
        var vm = NewVm(dialogs);          // no project, so the tab has no scratch file
        var tab = vm.Workspace.NewTab();
        tab.Text = "select 42;";

        Assert.True(await vm.Workspace.CloseTabAsync(tab));
        Assert.Equal(1, dialogs.SavePickerCalls);            // no path of its own, so it must ask
        Assert.Equal("select 42;", await File.ReadAllTextAsync(dest));
        Assert.DoesNotContain(tab, vm.Workspace.Tabs);
    }

    [Fact]
    public async Task Dismissing_the_save_destination_aborts_the_close()
    {
        // Save chosen, but the file picker is dismissed — the text has nowhere to go, so the tab must stay.
        var dialogs = new FakeDialogs(CloseChoice.Save, saveAsPath: null);
        var vm = NewVm(dialogs);
        var tab = vm.Workspace.NewTab();
        tab.Text = "select 42;";

        Assert.False(await vm.Workspace.CloseTabAsync(tab));
        Assert.Equal(1, dialogs.SavePickerCalls);
        Assert.Contains(tab, vm.Workspace.Tabs);
        Assert.Equal("select 42;", tab.Text);
    }

    // ---- file-backed tab with unsaved edits (the case the roadmap item originally missed) ----

    [Fact]
    public async Task Dirty_saved_script_prompts_and_Save_writes_to_its_own_path_without_a_picker()
    {
        var dialogs = new FakeDialogs(CloseChoice.Save);
        var vm = await Project(dialogs);
        vm.Workspace.NewTab();
        var tab = vm.Workspace.Tabs[0];
        var path = Path.Combine(vm.ScriptsDirectory!, "report.sql");
        await vm.Workspace.SaveScriptAsync(tab, path, "select 1;");

        tab.Text = "select 1; -- WIP";
        Assert.True(tab.IsDirty);
        Assert.True(tab.HasUnsavedWork);

        Assert.True(await vm.Workspace.CloseTabAsync(tab));
        Assert.Single(dialogs.ClosePrompts);
        Assert.Equal(0, dialogs.SavePickerCalls);            // it already has a destination
        Assert.Equal("select 1; -- WIP", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Discarding_a_dirty_saved_script_leaves_the_file_untouched()
    {
        var dialogs = new FakeDialogs(CloseChoice.Discard);
        var vm = await Project(dialogs);
        vm.Workspace.NewTab();
        var tab = vm.Workspace.Tabs[0];
        var path = Path.Combine(vm.ScriptsDirectory!, "report.sql");
        await vm.Workspace.SaveScriptAsync(tab, path, "select 1;");
        tab.Text = "select 1; -- WIP";

        Assert.True(await vm.Workspace.CloseTabAsync(tab));
        Assert.Equal("select 1;", await File.ReadAllTextAsync(path));  // disk keeps the last saved version
    }

    // ---- invariants ----

    [Fact]
    public async Task Closing_the_last_tab_still_leaves_one_open()
    {
        var dialogs = new FakeDialogs(CloseChoice.Discard);
        var vm = await Project(dialogs);
        var only = Assert.Single(vm.Workspace.Tabs);
        only.Text = "select 1;";

        Assert.True(await vm.Workspace.CloseTabAsync(only));
        var replacement = Assert.Single(vm.Workspace.Tabs);
        Assert.NotSame(only, replacement);
        Assert.Equal("", replacement.Text);
    }

    [Fact]
    public async Task Closing_a_background_tab_saves_that_tab_not_the_selected_one()
    {
        var dialogs = new FakeDialogs(CloseChoice.Save);
        var vm = await Project(dialogs);
        var background = vm.Workspace.Tabs[0];
        var backgroundPath = Path.Combine(vm.ScriptsDirectory!, "background.sql");
        await vm.Workspace.SaveScriptAsync(background, backgroundPath, "select 'bg';");
        background.Text = "select 'bg edited';";

        var selected = vm.Workspace.NewTab();
        selected.Text = "select 'front';";
        Assert.Same(selected, vm.Workspace.SelectedTab);

        Assert.True(await vm.Workspace.CloseTabAsync(background));
        Assert.Equal("select 'bg edited';", await File.ReadAllTextAsync(backgroundPath));
        Assert.Same(selected, vm.Workspace.SelectedTab);            // selection undisturbed
        Assert.Equal("select 'front';", selected.Text);             // and untouched
    }

    [Fact]
    public async Task A_tab_already_removed_is_not_prompted_for_again()
    {
        var dialogs = new FakeDialogs(CloseChoice.Discard);
        var vm = await Project(dialogs);
        vm.Workspace.NewTab();
        var tab = vm.Workspace.Tabs[0];
        await vm.Workspace.SaveScriptAsync(tab, Path.Combine(vm.ScriptsDirectory!, "r.sql"), "select 1;");
        tab.Text = "select 1; -- WIP";

        Assert.True(await vm.Workspace.CloseTabAsync(tab));
        Assert.False(await vm.Workspace.CloseTabAsync(tab));   // double-click on ✕ must not re-ask
        Assert.Single(dialogs.ClosePrompts);
    }
}
