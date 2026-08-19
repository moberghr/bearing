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
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Scratch buffers are backed by real files under <c>scripts/scratch/</c> (roadmap: scratch scripts
/// phase 2) — created lazily on first content, autosaved, promoted out of the folder when named, and
/// shown pinned-first in the scripts tree.
/// </summary>
public class ScratchFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-scratch", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private ShellViewModel NewVm(IDialogService? dialogs = null, AutosaveMode mode = AutosaveMode.OnEdit) => new(
        new ProviderRegistry(),
        new JsonProjectStore(),
        new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(Path.Combine(_root, "recent.json")),
        dialogs: dialogs ?? new FakeDialogs(),
        settings: SettingsService.InMemory(new AppSettings { AutosaveMode = mode }));

    private async Task<ShellViewModel> Project([System.Runtime.CompilerServices.CallerMemberName] string name = "",
        IDialogService? dialogs = null, AutosaveMode mode = AutosaveMode.OnEdit)
    {
        var vm = NewVm(dialogs, mode);
        await vm.InitializeAsync(Path.Combine(_root, name));
        return vm;
    }

    private static string ScratchDir(ShellViewModel vm) => Path.Combine(vm.ScriptsDirectory!, "scratch");

    /// <summary>Autosave is debounced; give the write a moment to land.</summary>
    private static async Task<string> WaitForPath(EditorTabViewModel tab)
    {
        for (var i = 0; i < 60 && tab.ScriptPath is null; i++) await Task.Delay(50);
        Assert.NotNull(tab.ScriptPath);
        return tab.ScriptPath!;
    }

    // ---- file creation ----

    [Fact]
    public async Task Typing_in_a_scratch_tab_creates_a_dated_file_in_the_scratch_folder()
    {
        var vm = await Project();
        var tab = vm.Workspace.SelectedTab!;
        Assert.True(tab.IsScratch);
        Assert.Null(tab.ScriptPath);

        tab.Text = "select 1;";
        var path = await WaitForPath(tab);

        Assert.Equal(Path.GetFullPath(ScratchDir(vm)), Path.GetFullPath(Path.GetDirectoryName(path)!));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}-\d{2}\.sql$", Path.GetFileName(path));
        Assert.Equal("select 1;", await File.ReadAllTextAsync(path));
        Assert.True(tab.IsScratch);   // having a file doesn't stop it being scratch
    }

    [Fact]
    public async Task An_untouched_tab_never_gets_a_file()
    {
        var vm = await Project();
        var tab = vm.Workspace.SelectedTab!;
        tab.Text = "   \n\t ";   // whitespace is not content

        await Task.Delay(400);
        Assert.Null(tab.ScriptPath);
        // Nothing to clean up later precisely because nothing was created.
        Assert.False(Directory.Exists(ScratchDir(vm)) && Directory.EnumerateFiles(ScratchDir(vm)).Any());
    }

    [Fact]
    public async Task Continuing_to_type_updates_the_same_file()
    {
        var vm = await Project();
        var tab = vm.Workspace.SelectedTab!;
        tab.Text = "select 1;";
        var path = await WaitForPath(tab);

        tab.Text = "select 1; select 2;";
        await vm.Workspace.FlushScratchAsync();

        Assert.Equal(path, tab.ScriptPath);   // no second file
        Assert.Equal("select 1; select 2;", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.EnumerateFiles(ScratchDir(vm)));
    }

    [Fact]
    public async Task Two_scratch_tabs_get_different_files()
    {
        var vm = await Project();
        var first = vm.Workspace.SelectedTab!;
        var second = vm.Workspace.NewTab();
        first.Text = "select 1;";
        second.Text = "select 2;";
        await vm.Workspace.FlushScratchAsync();

        Assert.NotEqual(first.ScriptPath, second.ScriptPath);
        Assert.Equal(2, Directory.EnumerateFiles(ScratchDir(vm)).Count());
        Assert.Equal("select 2;", await File.ReadAllTextAsync(second.ScriptPath!));
    }

    // ---- scratch is never "dirty", and closing it loses nothing ----

    [Fact]
    public async Task A_backed_scratch_tab_is_not_dirty_and_closes_without_a_prompt()
    {
        var dialogs = new FakeDialogs(CloseChoice.Cancel);   // would block the close if it were asked
        var vm = await Project(dialogs: dialogs);
        vm.Workspace.NewTab();
        var tab = vm.Workspace.Tabs[0];
        tab.Text = "select 1;";
        var path = await WaitForPath(tab);

        Assert.False(tab.IsDirty);
        Assert.False(tab.HasUnsavedWork);

        Assert.True(await vm.Workspace.CloseTabAsync(tab));
        Assert.Empty(dialogs.ClosePrompts);
        Assert.Equal("select 1;", await File.ReadAllTextAsync(path));   // the file outlives the tab
    }

    [Fact]
    public async Task Closing_flushes_a_pending_write_rather_than_prompting()
    {
        // The debounce is still pending when the close happens — the text must reach disk, not a dialog.
        var dialogs = new FakeDialogs(CloseChoice.Cancel);
        var vm = await Project(dialogs: dialogs);
        vm.Workspace.NewTab();
        var tab = vm.Workspace.Tabs[0];
        tab.Text = "select 'pending';";

        Assert.True(await vm.Workspace.CloseTabAsync(tab));
        Assert.Empty(dialogs.ClosePrompts);
        var file = Assert.Single(Directory.EnumerateFiles(ScratchDir(vm)));
        Assert.Equal("select 'pending';", await File.ReadAllTextAsync(file));
    }

    // ---- promotion ----

    [Fact]
    public async Task Naming_a_scratch_tab_moves_its_file_out_to_the_scripts_root()
    {
        var vm = await Project();
        var tab = vm.Workspace.SelectedTab!;
        tab.Text = "select * from film;";
        var scratchPath = await WaitForPath(tab);

        await vm.Workspace.RenameTabAsync(tab, "film report");

        Assert.False(tab.IsScratch);
        Assert.False(File.Exists(scratchPath));                       // moved, not copied
        var promoted = Path.Combine(vm.ScriptsDirectory!, "film report.sql");
        Assert.Equal(Path.GetFullPath(promoted), Path.GetFullPath(tab.ScriptPath!));
        Assert.Equal("select * from film;", await File.ReadAllTextAsync(promoted));
        Assert.Equal("film report.sql", tab.Header);                   // header switches to the filename
    }

    [Fact]
    public async Task A_promoted_tab_follows_the_named_script_rules()
    {
        // Autosave off, so "no longer scratch" is observable: a scratch buffer is written at checkpoints
        // whatever the mode, whereas a promoted script obeys the mode like any other named file.
        var vm = await Project(mode: AutosaveMode.Off);
        var tab = vm.Workspace.SelectedTab!;
        tab.Text = "select 1;";
        await vm.Workspace.FlushScratchAsync();   // scratch reaches disk even with autosave off
        Assert.NotNull(tab.ScriptPath);
        await vm.Workspace.RenameTabAsync(tab, "named");
        var path = tab.ScriptPath!;

        tab.Text = "select 1; -- edited";
        await vm.Workspace.FlushScratchAsync();

        Assert.False(tab.IsScratch);
        Assert.True(tab.IsDirty);                                      // named scripts save explicitly now
        Assert.True(tab.HasUnsavedWork);
        Assert.Equal("select 1;", await File.ReadAllTextAsync(path));   // the checkpoint skipped it
    }

    [Fact]
    public async Task Renaming_an_empty_scratch_tab_just_relabels_it()
    {
        var vm = await Project();
        var tab = vm.Workspace.SelectedTab!;

        await vm.Workspace.RenameTabAsync(tab, "later");

        Assert.True(tab.IsScratch);          // nothing on disk to promote
        Assert.Null(tab.ScriptPath);
        Assert.Equal("later", tab.Header);
    }

    // ---- scripts tree ----

    [Fact]
    public async Task Scratch_folder_is_pinned_first_and_flagged()
    {
        var vm = await Project();
        Directory.CreateDirectory(Path.Combine(vm.ScriptsDirectory!, "aaa-reports"));
        var tab = vm.Workspace.SelectedTab!;
        tab.Text = "select 1;";
        await WaitForPath(tab);
        vm.Scripts.RefreshScripts();

        var folders = vm.Scripts.ScriptNodes.OfType<ScriptFolderViewModel>().ToList();
        Assert.Equal(2, folders.Count);
        Assert.True(folders[0].IsScratch);                 // ahead of "aaa-reports" despite the alphabet
        Assert.Equal("scratch", folders[0].Name);
        Assert.False(folders[0].IsExpanded);               // collapsed = out of the way
        Assert.Equal(1, folders[0].Count);
        Assert.False(folders[1].IsScratch);
    }

    [Fact]
    public async Task Scratch_files_appear_in_the_tree_like_any_other_script()
    {
        var vm = await Project();
        var tab = vm.Workspace.SelectedTab!;
        tab.Text = "select 1;";
        var path = await WaitForPath(tab);
        vm.Scripts.RefreshScripts();

        Assert.Contains(vm.Scripts.Scripts, s => string.Equals(s.FullPath, path, StringComparison.Ordinal));
    }

    // ---- session round-trip ----

    [Fact]
    public async Task A_scratch_tab_comes_back_pointing_at_its_file_and_named_after_it()
    {
        var vm = await Project();
        var scratch = vm.Workspace.SelectedTab!;
        scratch.Text = "select 'still scratch';";
        var scratchPath = await WaitForPath(scratch);
        vm.SaveWorkspace();

        var vm2 = NewVm();
        await vm2.InitializeAsync(vm.ProjectDirectory!);

        var restored = Assert.Single(vm2.Workspace.Tabs);
        Assert.Equal(scratchPath, restored.ScriptPath);
        Assert.True(restored.IsScratch);                  // re-derived from where the file lives
        Assert.False(restored.IsDirty);
        Assert.Equal(Path.GetFileName(scratchPath), restored.Header);   // the file, not a "Scratch N" label
        Assert.Equal("select 'still scratch';", restored.Text);
    }

    [Fact]
    public async Task A_promoted_tab_comes_back_as_a_named_script_not_scratch()
    {
        var vm = await Project();
        var tab = vm.Workspace.SelectedTab!;
        tab.Text = "select 'promoted';";
        await WaitForPath(tab);
        await vm.Workspace.RenameTabAsync(tab, "keeper");
        vm.SaveWorkspace();

        var vm2 = NewVm();
        await vm2.InitializeAsync(vm.ProjectDirectory!);

        var restored = Assert.Single(vm2.Workspace.Tabs);
        Assert.False(restored.IsScratch);                 // it lives outside the scratch folder now
        Assert.Equal("keeper.sql", restored.Header);
    }

    // ---- the tab is named after its file (#1) ----

    [Fact]
    public async Task A_scratch_tabs_header_becomes_its_file_name_when_autosave_creates_the_file()
    {
        // The label and the file name used to be unrelated: the tab said "Scratch 1" while the file was
        // 2026-08-19-01.sql, and nothing on screen connected the two.
        var vm = await Project();
        var tab = vm.Workspace.SelectedTab!;
        Assert.Equal("Scratch 1", tab.Header);            // the placeholder, while there is no file

        tab.Text = "select 1;";
        var path = await WaitForPath(tab);

        Assert.Equal(Path.GetFileName(path), tab.Header);
        Assert.True(tab.IsScratch);                        // still scratch — it's the *name* that changed
    }

    [Fact]
    public async Task Opening_a_saved_script_does_not_consume_a_scratch_number()
    {
        // The counter used to advance on every NewTab, so opening one script left a permanent hole in the
        // numbering ("Scratch 1", then "Scratch 3").
        var vm = await Project();
        var script = Path.Combine(vm.ScriptsDirectory!, "named.sql");
        await File.WriteAllTextAsync(script, "select 1;");

        await vm.Workspace.OpenScriptInNewTabAsync(script);
        var next = vm.Workspace.NewTab();

        Assert.Equal("Scratch 2", next.Header);
    }

    [Fact]
    public async Task Naming_an_empty_scratch_tab_names_the_file_it_later_gets()
    {
        // Renaming an empty scratch has no file to promote, so it keeps the typed label. That label must
        // then be what the file is called, or the first keystroke would replace it with a dated name.
        var vm = await Project();
        var tab = vm.Workspace.SelectedTab!;
        await vm.Workspace.RenameTabAsync(tab, "morning check");
        Assert.Equal("morning check", tab.Header);

        tab.Text = "select 1;";
        var path = await WaitForPath(tab);

        Assert.Equal(Path.Combine(vm.ScriptsDirectory!, "morning check.sql"), path);
        Assert.False(tab.IsScratch);                       // named + on disk = a curated script
        Assert.Equal("morning check.sql", tab.Header);
        Assert.Equal("select 1;", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_named_empty_scratch_tab_falls_back_to_a_dated_name_when_its_name_is_taken()
    {
        var vm = await Project();
        await File.WriteAllTextAsync(Path.Combine(vm.ScriptsDirectory!, "taken.sql"), "-- someone else's");
        var tab = vm.Workspace.SelectedTab!;
        await vm.Workspace.RenameTabAsync(tab, "taken");

        tab.Text = "select 1;";
        var path = await WaitForPath(tab);

        // The write lands rather than being lost to the clash, and the existing script is untouched.
        Assert.Equal(Path.GetFullPath(ScratchDir(vm)), Path.GetFullPath(Path.GetDirectoryName(path)!));
        Assert.True(tab.IsScratch);
        Assert.Equal("-- someone else's", await File.ReadAllTextAsync(Path.Combine(vm.ScriptsDirectory!, "taken.sql")));
    }

    [Fact]
    public async Task A_typed_label_survives_a_session_round_trip_but_a_placeholder_is_regenerated()
    {
        var vm = await Project();
        await vm.Workspace.RenameTabAsync(vm.Workspace.SelectedTab!, "later");
        vm.Workspace.NewTab();                             // an untouched placeholder tab alongside it
        vm.SaveWorkspace();

        var vm2 = NewVm();
        await vm2.InitializeAsync(vm.ProjectDirectory!);

        Assert.Equal("later", vm2.Workspace.Tabs[0].Header);
        Assert.Equal("Scratch 2", vm2.Workspace.Tabs[1].Header);   // renumbered from scratch, not persisted
    }

    [Fact]
    public async Task A_placeholder_label_from_an_older_session_never_names_a_file()
    {
        // Sessions written before the header was derived from the file name persisted "Scratch N" too.
        var vm = await Project();
        var dir = vm.ProjectDirectory!;
        var session = new SessionState
        {
            OpenEditors = new() { new OpenEditor { ScratchName = "Scratch 4" } },
        };
        new JsonSessionStore().Save(dir, session);

        var vm2 = NewVm();
        await vm2.InitializeAsync(dir);
        var tab = Assert.Single(vm2.Workspace.Tabs);
        tab.Text = "select 1;";
        var path = await WaitForPath(tab);

        Assert.Equal(Path.GetFullPath(ScratchDir(vm2)), Path.GetFullPath(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task A_pre_phase2_session_with_inlined_text_still_restores()
    {
        // Sessions written before scratch had files carry the text inline with no ScriptPath.
        var vm = await Project();
        var dir = vm.ProjectDirectory!;
        var session = new Bearing.Core.Workspace.SessionState
        {
            OpenEditors = new() { new Bearing.Core.Workspace.OpenEditor { ScratchText = "select 'legacy';", ScratchName = "Old work" } },
        };
        new JsonSessionStore().Save(dir, session);

        var vm2 = NewVm();
        await vm2.InitializeAsync(dir);

        var tab = Assert.Single(vm2.Workspace.Tabs);
        Assert.True(tab.IsScratch);
        Assert.Equal("select 'legacy';", tab.Text);
        Assert.Equal("Old work", tab.Header);

        // ...and it gets a real file on the next edit, migrating itself forward. The label was the user's,
        // so the file is named after it and the tab is promoted rather than getting a dated scratch name.
        tab.Text = "select 'legacy'; -- now backed";
        var path = await WaitForPath(tab);
        Assert.Equal(Path.Combine(vm2.ScriptsDirectory!, "Old work.sql"), path);
        Assert.False(tab.IsScratch);
    }
}
