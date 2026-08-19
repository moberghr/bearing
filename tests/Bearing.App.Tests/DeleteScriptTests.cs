using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Services;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Deleting a script — from a tab's context menu or the Scripts tree. Both go through
/// <see cref="WorkspaceViewModel.DeleteScript"/>, because a file and the tab showing it have to go together:
/// the tab would otherwise point at nothing, and its autosave would put the file straight back.
/// </summary>
public class DeleteScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-delete", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private async Task<ShellViewModel> Project(AutosaveMode mode = AutosaveMode.OnEdit,
        [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var vm = new ShellViewModel(
            new ProviderRegistry(), new JsonProjectStore(), new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = mode }));
        await vm.InitializeAsync(Path.Combine(_root, name));
        return vm;
    }

    private static async Task<string> Script(ShellViewModel vm, string name, string text = "select 1;")
    {
        var path = Path.Combine(vm.ScriptsDirectory!, name);
        await File.WriteAllTextAsync(path, text);
        vm.Scripts.RefreshScripts();
        return path;
    }

    [Fact]
    public async Task Deleting_removes_the_file_the_tab_and_the_tree_node()
    {
        var vm = await Project();
        var path = await Script(vm, "doomed.sql");
        await vm.Workspace.OpenScriptInNewTabAsync(path);

        Assert.True(vm.Workspace.DeleteScript(path));

        Assert.False(File.Exists(path));
        Assert.DoesNotContain(vm.Workspace.Tabs, t => t.ScriptPath == path);
        Assert.DoesNotContain(vm.Scripts.Scripts, s => s.FullPath == path);
    }

    [Fact]
    public async Task A_pending_autosave_does_not_put_the_file_back()
    {
        // The debounced write is armed by the keystroke that made the tab dirty; deleting the file without
        // disarming it recreates the file a moment later.
        var vm = await Project();
        var path = await Script(vm, "typing.sql");
        await vm.Workspace.OpenScriptInNewTabAsync(path);
        vm.Workspace.SelectedTab!.Text = "select 2;";   // schedules the write

        Assert.True(vm.Workspace.DeleteScript(path));
        await Task.Delay(900);                          // longer than the autosave debounce

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Deleting_the_last_tab_leaves_a_fresh_one_rather_than_none()
    {
        var vm = await Project();
        var path = await Script(vm, "only.sql");
        await vm.Workspace.LoadScriptIntoSelectedAsync(path);   // the one open tab now backs the file

        Assert.True(vm.Workspace.DeleteScript(path));

        var tab = Assert.Single(vm.Workspace.Tabs);
        Assert.Null(tab.ScriptPath);
        Assert.True(tab.IsScratch);
    }

    [Fact]
    public async Task Deleting_does_not_prompt_about_unsaved_work()
    {
        // The close prompt exists to offer a save; here the file it would save to is what's being removed.
        var dialogs = new FakeDialogs(CloseChoice.Cancel);
        var vm = new ShellViewModel(
            new ProviderRegistry(), new JsonProjectStore(), new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(), dialogs: dialogs,
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));
        await vm.InitializeAsync(Path.Combine(_root, "noprompt"));
        var path = await Script(vm, "dirty.sql");
        await vm.Workspace.OpenScriptInNewTabAsync(path);
        vm.Workspace.SelectedTab!.Text = "select 'unsaved';";
        Assert.True(vm.Workspace.SelectedTab!.IsDirty);

        Assert.True(vm.Workspace.DeleteScript(path));

        Assert.Empty(dialogs.ClosePrompts);          // a Cancel answer would otherwise have kept the tab
        Assert.DoesNotContain(vm.Workspace.Tabs, t => t.ScriptPath == path);
    }

    [Fact]
    public async Task A_failed_delete_keeps_the_tab_open()
    {
        var vm = await Project();
        var path = await Script(vm, "locked.sql");
        await vm.Workspace.OpenScriptInNewTabAsync(path);
        // A directory at the path is the portable way to make File.Delete throw.
        File.Delete(path);
        Directory.CreateDirectory(path);

        Assert.False(vm.Workspace.DeleteScript(path));

        Assert.Contains(vm.Workspace.Tabs, t => t.ScriptPath == path);
        Assert.Contains("Could not delete", vm.StatusText);
    }

    [Fact]
    public async Task Deleting_a_file_no_tab_is_showing_just_removes_it()
    {
        var vm = await Project();
        var path = await Script(vm, "unopened.sql");
        var tabsBefore = vm.Workspace.Tabs.Count;

        Assert.True(vm.Workspace.DeleteScript(path));

        Assert.False(File.Exists(path));
        Assert.Equal(tabsBefore, vm.Workspace.Tabs.Count);
    }
}
