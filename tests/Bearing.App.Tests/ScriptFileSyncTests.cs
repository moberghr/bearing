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

    /// <summary>A reload replaces the text the tab's inline results hung under — for a tab in the background
    /// too, which the window never sees reloaded — so they go with it.</summary>
    [Fact]
    public async Task A_reload_drops_the_tabs_inline_results()
    {
        var (vm, tab, path) = await Open(new FakeDialogs());
        var scope = Bearing.App.Results.InlineResultPlacement.Scope(Bearing.Sql.PostgresDialect.Instance, "select 1;", 0, false);
        tab.ApplyInlineRun(scope, [new InlineResultZone(9, Array.Empty<ResultSetViewModel>(), null)]);

        File.WriteAllText(path, "select 2; -- from elsewhere");
        await Sync(vm).CheckAllAsync();

        Assert.Empty(tab.InlineZones);
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

    /// <summary>Save As repoints a tab without touching the tab collection; the new file's directory must be
    /// watched straight away, not at the next window activation.</summary>
    [Fact]
    public async Task A_tab_saved_somewhere_new_has_that_directory_watched()
    {
        var (vm, tab, _) = await Open(new FakeDialogs());
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        Assert.True(await vm.Workspace.SaveScriptAsync(tab, Path.Combine(elsewhere, "copy.sql"), tab.Text));

        Assert.Contains(elsewhere, Sync(vm).WatchedDirectories);
    }

    /// <summary>
    /// A watcher event checks the file it named, not every open tab's — Bearing's own autosaves fire the
    /// watcher too, and re-reading every file after each one is a lot of reading on a network drive. The other
    /// tab's change (made before anything was watching) waits for the full sweep the window's activation does.
    /// </summary>
    [Fact]
    public async Task A_watcher_event_checks_only_the_file_it_named()
    {
        var (vm, first, firstPath) = await Open(new FakeDialogs());
        var second = vm.Workspace.Tabs[1];
        var secondPath = Path.Combine(vm.ScriptsDirectory!, "other.sql");
        Assert.True(await vm.Workspace.SaveScriptAsync(second, secondPath, "select 'b';"));
        File.WriteAllText(secondPath, "select 'b2';");   // unseen: nothing below is watching yet

        // A sync of its own, so its posts run here instead of on a UI thread these tests don't pump.
        using var sync = new ScriptFileSync(vm.Context, null, post: a => a(), debounce: TimeSpan.FromMilliseconds(20));
        File.WriteAllText(firstPath, "select 2;");

        for (var i = 0; i < 100 && first.Text != "select 2;"; i++) await Task.Delay(50);
        await Task.Delay(200);   // let that run finish
        Assert.Equal("select 2;", first.Text);
        Assert.Equal("select 'b';", second.Text);

        await sync.CheckAllAsync();
        Assert.Equal("select 'b2';", second.Text);
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

    /// <summary>
    /// Autosave runs at checkpoints — closing the tab, switching project — that may be about to put up a
    /// dialog of their own, so it never asks. It used to post the question, which then stacked on the close
    /// prompt and could outlive the tab. The next check puts it, once.
    /// </summary>
    [Fact]
    public async Task A_conflict_autosave_finds_is_asked_by_the_next_check_not_by_autosave()
    {
        var dialogs = new FakeDialogs { DiskConflictAnswer = DiskConflictChoice.Decide };
        var (vm, tab, path) = await Open(dialogs, AutosaveMode.OnEdit);
        await vm.Workspace.Autosave.FlushAsync(tab);
        File.WriteAllText(path, "select 1; -- theirs");
        tab.Text = "select 1; -- mine";

        await vm.Workspace.Autosave.FlushAsync(tab);
        Assert.True(tab.HasDiskConflict);
        Assert.Empty(dialogs.DiskConflictPrompts);

        await Sync(vm).CheckAllAsync();
        Assert.Equal(["report.sql"], dialogs.DiskConflictPrompts);

        await Sync(vm).CheckAllAsync();   // "decide later" holds
        Assert.Single(dialogs.DiskConflictPrompts);
    }

    /// <summary>
    /// The tab is closed (its edits discarded) while the conflict question is still up. Answering "overwrite"
    /// afterwards used to write the discarded buffer over the change the user was being asked about.
    /// </summary>
    [Fact]
    public async Task An_answer_that_arrives_after_the_tab_closed_writes_nothing()
    {
        var answer = new TaskCompletionSource<DiskConflictChoice>();
        var dialogs = new FakeDialogs(CloseChoice.Discard) { DiskConflictPending = answer.Task };
        var (vm, tab, path) = await Open(dialogs);
        tab.Text = "select 1; -- mine";
        File.WriteAllText(path, "select 1; -- theirs");

        var check = Sync(vm).CheckAllAsync();
        for (var i = 0; i < 100 && dialogs.DiskConflictPrompts.Count == 0; i++) await Task.Delay(20);
        Assert.Equal(["report.sql"], dialogs.DiskConflictPrompts);   // the question is up
        Assert.True(await vm.Workspace.CloseTabAsync(tab));

        answer.SetResult(DiskConflictChoice.Overwrite);
        await check;

        Assert.Equal("select 1; -- theirs", File.ReadAllText(path));
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
