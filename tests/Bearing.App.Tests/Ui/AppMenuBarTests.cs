using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Bearing.App.Views;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The macOS menu bar's key equivalents. AppKit hands a matched key to the menu instead of the window, so the
/// bar replays it (<see cref="KeyReplay"/>) and the window's own pipeline decides — these pin that a replayed
/// key does what the keystroke did before the bar existed, and nothing more. Real AppKit dispatch can't run
/// headless; what's asserted is the replay on the real window.
/// </summary>
[Collection(UiTestCollection.Name)]
public class AppMenuBarTests
{
    private readonly UiTestSession _ui;

    public AppMenuBarTests(UiTestSession ui) => _ui = ui;

    private static readonly KeyGesture CmdSlash = new(Key.OemQuestion, KeyModifiers.Meta);

    [Fact]
    public Task A_replayed_editor_key_acts_in_the_focused_editor() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_replayed_editor_key_acts_in_the_focused_editor));
        var (editor, _) = InlineResultsTests.Setup(shell, "select 1;");

        Assert.True(KeyReplay.Raise(shell.Window, CmdSlash));
        shell.Pump();

        Assert.StartsWith("--", editor.Text);
    });

    /// <summary>Cmd+/ is an Editor-scope binding. With a result grid focused it did nothing before the bar;
    /// replayed from the bar it must still do nothing — not comment the editor's line.</summary>
    [Fact]
    public Task A_replayed_editor_key_does_nothing_from_a_grid() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_replayed_editor_key_does_nothing_from_a_grid));
        var (editor, tab) = InlineResultsTests.Setup(shell);
        var set = InlineZoneGridTests.Editable();
        InlineZoneGridTests.Place(shell, tab, set);
        var zone = Assert.Single(InlineResultsTests.Zones(editor));
        InlineZoneInputTests.ClickCell(shell, zone, set.Rows[0], 1);
        Assert.True(zone.IsKeyboardFocusWithin);
        var before = editor.Text;

        KeyReplay.Raise(shell.Window, CmdSlash);
        shell.Pump();

        Assert.Equal(before, editor.Text);
    });

    /// <summary>F2 is Rename Tab in the bar, and the grid's own Edit-cell key. From a grid the grid wins, as
    /// it did when the key reached the window directly.</summary>
    [Fact]
    public Task A_replayed_F2_in_a_grid_edits_the_cell_rather_than_renaming_the_tab() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_replayed_F2_in_a_grid_edits_the_cell_rather_than_renaming_the_tab));
        var (editor, tab) = InlineResultsTests.Setup(shell);
        var set = InlineZoneGridTests.Editable();
        InlineZoneGridTests.Place(shell, tab, set);
        var zone = Assert.Single(InlineResultsTests.Zones(editor));
        InlineZoneInputTests.ClickCell(shell, zone, set.Rows[0], 1);

        Assert.True(KeyReplay.Raise(shell.Window, new KeyGesture(Key.F2)));
        shell.Pump();

        Assert.False(tab.IsRenaming);
        Assert.Contains(zone.GetVisualDescendants().OfType<TextBox>(), box => box.IsFocused);
    });

    /// <summary>With the native bar on, it is the menu: the in-window bar is gone and an Alt tap reveals
    /// nothing (a transient bar would otherwise own Escape and swallow global keys while invisible).</summary>
    [Fact]
    public Task With_the_native_bar_the_in_window_bar_and_its_alt_reveal_are_gone() => _ui.Run(async () =>
    {
        AppMenuBar.Enabled = true;
        try
        {
            using var shell = await ShellHarness.ShowAsync(nameof(With_the_native_bar_the_in_window_bar_and_its_alt_reveal_are_gone));
            Assert.DoesNotContain(shell.Window.GetVisualDescendants().OfType<Menu>(), m => m.Name == "MainMenu");
            var before = shell.Vm.IsMenuVisible;

            shell.Window.KeyPress(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
            shell.Window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
            shell.Pump();

            Assert.Equal(before, shell.Vm.IsMenuVisible);
        }
        finally
        {
            AppMenuBar.Enabled = false;
        }
    });
}
