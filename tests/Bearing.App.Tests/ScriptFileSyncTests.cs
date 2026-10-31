using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Services;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Persistence;
using Bearing.Testing;
using Xunit;
using static Bearing.App.Workspace.ScriptFileSync;

namespace Bearing.App.Tests;

/// <summary>
/// A script changed on disk by something other than Bearing. Before, every write was blind — autosave, Save
/// and the shutdown flush put the tab's buffer over whatever the file held — and a restored session brought
/// back the old text over a file edited while Bearing was closed. Now a clean tab reloads, and a tab with
/// edits of its own is flagged, asked about, and never written until someone chooses.
/// </summary>
public class ScriptFileSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-filesync", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private ShellViewModel NewVm(FakeDialogs dialogs, AutosaveMode mode = AutosaveMode.Off) => new(
        new ProviderRegistry(),
        new JsonProjectStore(),
        new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(Path.Combine(_root, "recent.json")),
        new FakeSecretStore(),
        dialogs: dialogs,
        settings: SettingsService.InMemory(new AppSettings { AutosaveMode = mode }));

    private async Task<(ShellViewModel Vm, EditorTabViewModel Tab, string Path)> Open(
        FakeDialogs dialogs, AutosaveMode mode = AutosaveMode.Off, string text = "select 1;")
    {
        var vm = NewVm(dialogs, mode);
        await vm.InitializeAsync(Path.Combine(_root, "project"));
        vm.Workspace.NewTab();   // a spare, so nothing auto-replaces the tab under test
        var tab = vm.Workspace.Tabs[0];
        var path = Path.Combine(vm.ScriptsDirectory!, "report.sql");
        Assert.True(await vm.Workspace.SaveScriptAsync(tab, path, text));
        return (vm, tab, path);
    }

    private static ScriptFileSync Sync(ShellViewModel vm) => vm.Context.FileSync!;

    // ---- the decision ------------------------------------------------------------------------------

    [Theory]
    [InlineData("a", "a", "a", DiskState.Unchanged)]
    [InlineData("a", "a", "a+mine", DiskState.Unchanged)]          // our edits, file untouched: write them
    [InlineData("b", "a", "b", DiskState.MatchesBuffer)]           // someone wrote what we have
    [InlineData("b", "a", "a", DiskState.ChangedClean)]            // reload
    [InlineData("b", "a", "a+mine", DiskState.Conflict)]           // both sides moved
    [InlineData(null, "a", "a", DiskState.Missing)]
    public void Classify_compares_disk_with_the_baseline_and_the_buffer(
        string? disk, string baseline, string text, DiskState expected)
        => Assert.Equal(expected, Classify(disk, baseline, text));

    // ---- noticing -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_clean_tab_reloads_when_its_file_changes()
    {
        var dialogs = new FakeDialogs();
        var (vm, tab, path) = await Open(dialogs);

        File.WriteAllText(path, "select 2; -- from elsewhere");
        await Sync(vm).CheckAllAsync();

        Assert.Equal("select 2; -- from elsewhere", tab.Text);
        Assert.False(tab.IsModified);
        Assert.False(tab.HasDiskConflict);
        Assert.Empty(dialogs.DiskConflictPrompts);                 // nothing to lose, nothing to ask
        Assert.Contains("Reloaded report.sql", vm.StatusText);
    }

    [Fact]
    public async Task A_check_after_our_own_save_finds_nothing()
    {
        var dialogs = new FakeDialogs();
        var (vm, tab, path) = await Open(dialogs);
        tab.Text = "select 3;";
        await vm.Workspace.SaveScriptAsync(tab, path, tab.Text);

        await Sync(vm).CheckAllAsync();

        Assert.Equal("select 3;", tab.Text);
        Assert.False(tab.HasDiskConflict);
        Assert.Empty(dialogs.DiskConflictPrompts);
    }

    [Fact]
    public async Task Edits_on_both_sides_ask_and_deciding_later_keeps_both()
    {
        var dialogs = new FakeDialogs { DiskConflictAnswer = DiskConflictChoice.Decide };
        var (vm, tab, path) = await Open(dialogs);
        tab.Text = "select 1; -- mine";
        File.WriteAllText(path, "select 1; -- theirs");

        await Sync(vm).CheckAllAsync();

        Assert.Equal(["report.sql"], dialogs.DiskConflictPrompts);
        Assert.True(tab.HasDiskConflict);
        Assert.Equal("select 1; -- mine", tab.Text);
        Assert.Equal("select 1; -- theirs", File.ReadAllText(path));

        // Asked once: coming back to the window again must not repeat the question.
        await Sync(vm).CheckAllAsync();
        Assert.Single(dialogs.DiskConflictPrompts);
    }

    [Fact]
    public async Task Choosing_reload_takes_the_file()
    {
        var dialogs = new FakeDialogs { DiskConflictAnswer = DiskConflictChoice.Reload };
        var (vm, tab, path) = await Open(dialogs);
        tab.Text = "select 1; -- mine";
        File.WriteAllText(path, "select 1; -- theirs");

        await Sync(vm).CheckAllAsync();

        Assert.Equal("select 1; -- theirs", tab.Text);
        Assert.False(tab.IsModified);
        Assert.False(tab.HasDiskConflict);
    }

    [Fact]
    public async Task Choosing_overwrite_writes_the_tab()
    {
        var dialogs = new FakeDialogs { DiskConflictAnswer = DiskConflictChoice.Overwrite };
        var (vm, tab, path) = await Open(dialogs);
        tab.Text = "select 1; -- mine";
        File.WriteAllText(path, "select 1; -- theirs");

        await Sync(vm).CheckAllAsync();

        Assert.Equal("select 1; -- mine", File.ReadAllText(path));
        Assert.False(tab.IsModified);
        Assert.False(tab.HasDiskConflict);
    }

    [Fact]
    public async Task A_vanished_file_is_reported_and_the_tab_keeps_its_text()
    {
        var dialogs = new FakeDialogs();
        var (vm, tab, path) = await Open(dialogs);
        File.Delete(path);

        await Sync(vm).CheckAllAsync();

        Assert.Equal("select 1;", tab.Text);
        Assert.Contains("no longer on disk", vm.StatusText);
        Assert.Empty(dialogs.DiskConflictPrompts);
    }

    [Fact]
    public async Task The_directory_of_an_open_file_is_watched()
    {
        var (vm, _, path) = await Open(new FakeDialogs());
        Sync(vm).SyncWatchers();
        Assert.Contains(Path.GetDirectoryName(path)!, Sync(vm).WatchedDirectories);
    }

    // ---- writes stand aside -----------------------------------------------------------------------

    [Fact]
    public async Task Autosave_does_not_write_over_a_file_changed_under_the_tab()
    {
        var dialogs = new FakeDialogs { DiskConflictAnswer = DiskConflictChoice.Decide };
        var (vm, tab, path) = await Open(dialogs, AutosaveMode.OnEdit);
        await vm.Workspace.Autosave.FlushAsync(tab);
        File.WriteAllText(path, "select 1; -- theirs");

        tab.Text = "select 1; -- mine";
        await vm.Workspace.Autosave.FlushAsync(tab);

        Assert.Equal("select 1; -- theirs", File.ReadAllText(path));
        Assert.True(tab.HasDiskConflict);
        Assert.True(tab.IsDirty);   // the close prompt still guards the edits
    }

    [Fact]
    public async Task Autosave_reloads_a_clean_tab_instead_of_reverting_the_file()
    {
        var (vm, tab, path) = await Open(new FakeDialogs(), AutosaveMode.OnEdit);
        File.WriteAllText(path, "select 2; -- theirs");

        await vm.Workspace.Autosave.FlushAsync(tab);

        Assert.Equal("select 2; -- theirs", File.ReadAllText(path));
        Assert.Equal("select 2; -- theirs", tab.Text);
    }

    [Fact]
    public async Task The_shutdown_flush_skips_a_file_changed_under_the_tab()
    {
        var (vm, tab, path) = await Open(new FakeDialogs(), AutosaveMode.OnEdit);
        await vm.Workspace.Autosave.FlushAsync(tab);
        File.WriteAllText(path, "select 1; -- theirs");
        tab.Text = "select 1; -- mine";

        vm.Workspace.Autosave.FlushExistingBlocking();

        Assert.Equal("select 1; -- theirs", File.ReadAllText(path));
    }

    [Fact]
    public async Task Save_asks_before_overwriting_and_writes_nothing_when_declined()
    {
        var dialogs = new FakeDialogs { DiskConflictAnswer = DiskConflictChoice.Decide };
        var (vm, tab, path) = await Open(dialogs);
        tab.Text = "select 1; -- mine";
        File.WriteAllText(path, "select 1; -- theirs");

        var saved = await vm.Workspace.SaveScriptAsync(tab, path, tab.Text);

        Assert.False(saved);
        Assert.Equal(["report.sql"], dialogs.DiskConflictPrompts);
        Assert.Equal("select 1; -- theirs", File.ReadAllText(path));
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public async Task Save_writes_when_the_user_chooses_overwrite()
    {
        var dialogs = new FakeDialogs { DiskConflictAnswer = DiskConflictChoice.Overwrite };
        var (vm, tab, path) = await Open(dialogs);
        tab.Text = "select 1; -- mine";
        File.WriteAllText(path, "select 1; -- theirs");

        Assert.True(await vm.Workspace.SaveScriptAsync(tab, path, tab.Text));

        Assert.Equal("select 1; -- mine", File.ReadAllText(path));
        Assert.False(tab.IsDirty);
        Assert.False(tab.HasDiskConflict);
    }

    [Fact]
    public async Task A_declined_save_on_close_keeps_the_tab()
    {
        var dialogs = new FakeDialogs(CloseChoice.Save) { DiskConflictAnswer = DiskConflictChoice.Decide };
        var (vm, tab, path) = await Open(dialogs);
        tab.Text = "select 1; -- mine";
        File.WriteAllText(path, "select 1; -- theirs");

        var closed = await vm.Workspace.CloseTabAsync(tab);

        Assert.False(closed);
        Assert.Contains(tab, vm.Workspace.Tabs);
        Assert.Equal("select 1; -- theirs", File.ReadAllText(path));
    }

    // ---- across a restart ---------------------------------------------------------------------------

    [Fact]
    public void A_restored_buffer_follows_the_baseline_hash()
    {
        var h = Hash("base");
        Assert.Equal(("mine", false), WorkspaceViewModel.RestoredBuffer("mine", h, "base"));      // untouched file
        Assert.Equal(("theirs", false), WorkspaceViewModel.RestoredBuffer("base", h, "theirs"));  // was clean
        Assert.Equal(("mine", true), WorkspaceViewModel.RestoredBuffer("mine", h, "theirs"));     // both moved
        Assert.Equal(("mine", false), WorkspaceViewModel.RestoredBuffer("mine", null, "theirs")); // older session
        Assert.Equal(("disk", false), WorkspaceViewModel.RestoredBuffer(null, h, "disk"));
    }

    [Fact]
    public async Task A_clean_tab_comes_back_with_what_the_file_says_now()
    {
        var dialogs = new FakeDialogs();
        var (vm, _, path) = await Open(dialogs);
        vm.SaveWorkspace();
        File.WriteAllText(path, "select 9; -- edited while closed");

        var next = NewVm(dialogs);
        await next.InitializeAsync(Path.Combine(_root, "project"));

        var tab = next.Workspace.Tabs.Single(t => t.ScriptPath == path);
        Assert.Equal("select 9; -- edited while closed", tab.Text);
        Assert.False(tab.IsModified);
    }

    [Fact]
    public async Task Unsaved_edits_against_a_file_changed_while_closed_come_back_flagged()
    {
        var dialogs = new FakeDialogs();
        var (vm, tab, path) = await Open(dialogs);
        tab.Text = "select 1; -- mine, unsaved";
        vm.SaveWorkspace();
        File.WriteAllText(path, "select 1; -- theirs");

        var next = NewVm(dialogs);
        await next.InitializeAsync(Path.Combine(_root, "project"));

        var restored = next.Workspace.Tabs.Single(t => t.ScriptPath == path);
        Assert.Equal("select 1; -- mine, unsaved", restored.Text);
        Assert.True(restored.HasDiskConflict);
        Assert.Equal("select 1; -- theirs", File.ReadAllText(path));
    }

    [Fact]
    public async Task Unsaved_edits_against_an_untouched_file_come_back_unflagged()
    {
        var dialogs = new FakeDialogs();
        var (vm, tab, path) = await Open(dialogs);
        tab.Text = "select 1; -- mine, unsaved";
        vm.SaveWorkspace();

        var next = NewVm(dialogs);
        await next.InitializeAsync(Path.Combine(_root, "project"));

        var restored = next.Workspace.Tabs.Single(t => t.ScriptPath == path);
        Assert.Equal("select 1; -- mine, unsaved", restored.Text);
        Assert.True(restored.IsModified);
        Assert.False(restored.HasDiskConflict);
    }
}
