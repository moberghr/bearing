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
/// Drag feedback in the Scripts tree: what is in flight, and where it would land. Moving a script between
/// folders worked, but nothing on screen said either, so the only way to find out was to drop and look — and
/// a drag cursor isn't available to say it, since the platform owns the pointer for the duration of a drag.
/// What the marks look like is eyeball-QA (§4.3); this covers the state that drives them, and in particular
/// that only ever one row is marked as each.
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

    private static async Task<ScriptItem> Script(ShellViewModel vm, string name)
    {
        await File.WriteAllTextAsync(Path.Combine(vm.ScriptsDirectory!, name), "select 1;");
        vm.Scripts.RefreshScripts();
        return vm.Scripts.Scripts.Single(s => s.Name == name);
    }

    [Fact]
    public async Task Hovering_a_folder_marks_only_that_folder()
    {
        var vm = await Project();
        var reports = Folder(vm, "Reports");
        var archive = Folder(vm, "Archive");

        vm.Scripts.MarkDropTarget(reports);

        Assert.True(reports.IsDropTarget);
        Assert.False(archive.IsDropTarget);
    }

    [Fact]
    public async Task Moving_to_another_folder_un_marks_the_previous_one()
    {
        // DragOver fires continuously as the pointer moves, so the previous target has to be released or the
        // tree ends up with a trail of highlighted rows.
        var vm = await Project();
        var reports = Folder(vm, "Reports");
        var archive = Folder(vm, "Archive");
        vm.Scripts.MarkDropTarget(reports);

        vm.Scripts.MarkDropTarget(archive);

        Assert.False(reports.IsDropTarget);
        Assert.True(archive.IsDropTarget);
    }

    [Fact]
    public async Task Leaving_the_folders_behind_marks_nothing()
    {
        // Dropping on anything that isn't a folder row moves the script to the scripts root, and no
        // highlight is how that reads — an outline round the whole pane was tried and was just noise.
        var vm = await Project();
        var reports = Folder(vm, "Reports");
        vm.Scripts.MarkDropTarget(reports);

        vm.Scripts.MarkDropTarget(null);

        Assert.False(reports.IsDropTarget);
    }

    [Fact]
    public async Task Ending_the_drag_clears_the_highlight()
    {
        var vm = await Project();
        var reports = Folder(vm, "Reports");
        vm.Scripts.MarkDropTarget(reports);

        vm.Scripts.ClearDropTarget();

        Assert.False(reports.IsDropTarget);
    }

    // ---- what is in flight ----

    [Fact]
    public async Task The_dragged_script_is_marked_so_it_can_be_dimmed()
    {
        var vm = await Project();
        var script = await Script(vm, "moving.sql");

        vm.Scripts.MarkDragging(script);

        Assert.True(script.IsDragging);
    }

    [Fact]
    public async Task Only_one_script_is_ever_in_flight()
    {
        var vm = await Project();
        var first = await Script(vm, "first.sql");
        var second = await Script(vm, "second.sql");
        vm.Scripts.MarkDragging(first);

        vm.Scripts.MarkDragging(second);

        Assert.False(first.IsDragging);
        Assert.True(second.IsDragging);
    }

    [Fact]
    public async Task Ending_the_drag_un_marks_the_script()
    {
        var vm = await Project();
        var script = await Script(vm, "moving.sql");
        vm.Scripts.MarkDragging(script);

        vm.Scripts.MarkDragging(null);

        Assert.False(script.IsDragging);
    }

    [Fact]
    public async Task A_tree_refresh_drops_the_highlight_rather_than_leaving_it_on_a_replaced_node()
    {
        var vm = await Project();
        var reports = Folder(vm, "Reports");
        vm.Scripts.MarkDropTarget(reports);

        vm.Scripts.RefreshScripts();   // a completed move refreshes, and every node is rebuilt

        Assert.False(reports.IsDropTarget);
        Assert.False(Folder(vm, "Reports").IsDropTarget);
    }

    [Fact]
    public async Task A_tree_refresh_also_drops_the_in_flight_mark()
    {
        // A completed move refreshes the tree, so the row that was dragged is replaced by a new instance.
        var vm = await Project();
        var script = await Script(vm, "moved.sql");
        vm.Scripts.MarkDragging(script);

        vm.Scripts.RefreshScripts();

        Assert.False(script.IsDragging);
        Assert.False(vm.Scripts.Scripts.Single(s => s.Name == "moved.sql").IsDragging);
    }
}
