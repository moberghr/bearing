using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Bearing.App.Services;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// A script reloaded from disk must reach the editor, not just the tab. The editor reads a tab's text only
/// when it is bound to it, so a reload of the tab on screen used to leave the old text showing while the tab
/// believed it held the new — and the next keystroke wrote the old text back over the file. Only the real
/// window can show this: every view-model test agreed with the broken code.
/// </summary>
[Collection(UiTestCollection.Name)]
public class ScriptReloadUiTests
{
    private readonly UiTestSession _ui;

    public ScriptReloadUiTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task A_clean_tab_reloaded_from_disk_shows_the_new_text() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_clean_tab_reloaded_from_disk_shows_the_new_text));
        var (editor, path) = await OpenScript(shell, "select 1;");

        File.WriteAllText(path, "select 2; -- from elsewhere");
        await shell.Vm.Context.FileSync!.CheckAllAsync();
        shell.Pump();

        Assert.Equal("select 2; -- from elsewhere", editor.Text);
        Assert.Equal(editor.Text, shell.Vm.Workspace.SelectedTab!.Text);
        Assert.False(shell.Vm.Workspace.SelectedTab.IsModified);
    });

    /// <summary>"Reload from disk" in the conflict dialog: the editor must drop the edits it is showing, or
    /// the next keystroke carries them back over the file — the overwrite the user just declined.</summary>
    [Fact]
    public Task Choosing_reload_in_a_conflict_replaces_the_edits_on_screen() => _ui.Run(async () =>
    {
        var dialogs = new FakeDialogs { DiskConflictAnswer = DiskConflictChoice.Reload };
        using var shell = await ShellHarness.ShowAsync(
            nameof(Choosing_reload_in_a_conflict_replaces_the_edits_on_screen), dialogs: dialogs);
        var (editor, path) = await OpenScript(shell, "select 1;");

        editor.Text = "select 1; -- mine";
        File.WriteAllText(path, "select 1; -- theirs");
        await shell.Vm.Context.FileSync!.CheckAllAsync();
        shell.Pump();

        Assert.Equal(["report.sql"], dialogs.DiskConflictPrompts);
        Assert.Equal("select 1; -- theirs", editor.Text);
        Assert.False(shell.Vm.Workspace.SelectedTab!.IsModified);
    });

    /// <summary>Put <paramref name="text"/> in the selected tab through the editor and save it to a file.</summary>
    private static async Task<(TextEditor Editor, string Path)> OpenScript(ShellHarness shell, string text)
    {
        var editor = shell.Window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "Editor");
        var tab = shell.Vm.Workspace.SelectedTab!;
        editor.Text = text;
        Assert.Equal(text, tab.Text);   // the editor → tab half works, so the assertions below test the other

        var path = Path.Combine(shell.Vm.ScriptsDirectory!, "report.sql");
        Assert.True(await shell.Vm.Workspace.SaveScriptAsync(tab, path, text));
        shell.Pump();
        Assert.Equal(text, editor.Text);
        return (editor, path);
    }
}
