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
/// Where a dragged script will land. Moving a script between folders worked, but nothing on screen said
/// which folder would take it, so the only way to find out was to drop and look. What the highlight looks
/// like is eyeball-QA (§4.3); this covers the state that drives it, and in particular that exactly one
/// target is ever marked.
/// </summary>
public class DropTargetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-drop", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private async Task<ShellViewModel> Project([System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var vm = new ShellViewModel(
            new ProviderRegistry(), new JsonProjectStore(), new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore());
        await vm.InitializeAsync(Path.Combine(_root, name));
        Directory.CreateDirectory(Path.Combine(vm.ScriptsDirectory!, "Reports"));
        Directory.CreateDirectory(Path.Combine(vm.ScriptsDirectory!, "Archive"));
        vm.Scripts.RefreshScripts();
        return vm;
    }

    private static ScriptFolderViewModel Folder(ShellViewModel vm, string name)
        => vm.Scripts.ScriptNodes.OfType<ScriptFolderViewModel>().Single(f => f.Name == name);

    [Fact]
    public async Task Hovering_a_folder_marks_only_that_folder()
    {
        var vm = await Project();
        var reports = Folder(vm, "Reports");
        var archive = Folder(vm, "Archive");

        vm.Scripts.MarkDropTarget(reports, root: false);

        Assert.True(reports.IsDropTarget);
        Assert.False(archive.IsDropTarget);
        Assert.False(vm.Scripts.IsRootDropTarget);
    }

    [Fact]
    public async Task Moving_to_another_folder_un_marks_the_previous_one()
    {
        // DragOver fires continuously as the pointer moves, so the previous target has to be released or the
        // tree ends up with a trail of highlighted rows.
        var vm = await Project();
        var reports = Folder(vm, "Reports");
        var archive = Folder(vm, "Archive");
        vm.Scripts.MarkDropTarget(reports, root: false);

        vm.Scripts.MarkDropTarget(archive, root: false);

        Assert.False(reports.IsDropTarget);
        Assert.True(archive.IsDropTarget);
    }

    [Fact]
    public async Task Over_the_tree_but_not_a_folder_marks_the_root_instead()
    {
        // Dropping on anything that isn't a folder row moves the script to the scripts root — a real
        // outcome, so it gets its own mark rather than reading as "nothing will happen".
        var vm = await Project();
        var reports = Folder(vm, "Reports");
        vm.Scripts.MarkDropTarget(reports, root: false);

        vm.Scripts.MarkDropTarget(null, root: true);

        Assert.False(reports.IsDropTarget);
        Assert.True(vm.Scripts.IsRootDropTarget);
    }

    [Fact]
    public async Task Ending_the_drag_clears_everything()
    {
        var vm = await Project();
        var reports = Folder(vm, "Reports");
        vm.Scripts.MarkDropTarget(reports, root: true);

        vm.Scripts.ClearDropTarget();

        Assert.False(reports.IsDropTarget);
        Assert.False(vm.Scripts.IsRootDropTarget);
    }

    [Fact]
    public async Task A_tree_refresh_drops_the_highlight_rather_than_leaving_it_on_a_replaced_node()
    {
        var vm = await Project();
        var reports = Folder(vm, "Reports");
        vm.Scripts.MarkDropTarget(reports, root: false);

        vm.Scripts.RefreshScripts();   // a completed move refreshes, and every node is rebuilt

        Assert.False(reports.IsDropTarget);
        Assert.False(Folder(vm, "Reports").IsDropTarget);
        Assert.False(vm.Scripts.IsRootDropTarget);
    }
}
