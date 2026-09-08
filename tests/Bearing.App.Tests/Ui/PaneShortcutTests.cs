using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The pane toggles as a keystroke actually travelling through the shell. <c>KeybindingTests</c> proves the
/// keymap resolves Ctrl+M to view.toggleSidePane; it cannot prove the keystroke ever gets there — the
/// editor's tunnel handler and AvaloniaEdit's own bindings both sit between the key and the window's bubble
/// path, and a gesture either of them claims would resolve perfectly and still do nothing.
/// </summary>
[Collection(UiTestCollection.Name)]
public class PaneShortcutTests
{
    private readonly UiTestSession _ui;

    public PaneShortcutTests(UiTestSession ui) => _ui = ui;

    [Theory]
    [InlineData(Key.B, PhysicalKey.B)]   // the editor convention
    [InlineData(Key.M, PhysicalKey.M)]   // the DBeaver/Eclipse reflex
    public Task The_side_pane_collapses_from_the_editor(Key key, PhysicalKey physical) => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync($"side-pane-{key}");
        FocusEditor(shell);
        Assert.True(shell.Vm.SidePaneOpen);

        shell.Window.KeyPress(key, RawInputModifiers.Control, physical, null);
        shell.Pump();
        Assert.False(shell.Vm.SidePaneOpen);

        shell.Window.KeyPress(key, RawInputModifiers.Control, physical, null);
        shell.Pump();
        Assert.True(shell.Vm.SidePaneOpen);
    });

    private static void FocusEditor(ShellHarness shell)
    {
        var editor = shell.Window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "Editor");
        editor.TextArea.Focus();
        shell.Pump();
        Assert.True(editor.TextArea.IsFocused, "the editor never took focus");
    }
}
