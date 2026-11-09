using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Bearing.App.Controls;
using Bearing.App.ViewModels;
using Bearing.App.Views;
using Bearing.Core.Workspace;
using Bearing.Demo;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Focus and keys in an inline zone (docs/specs/2026-11-06-inline-results.md §8): the grid takes the keyboard,
/// nothing it receives reaches the SQL text, Esc goes back to the editor, the focus commands find the caret's
/// zone, grid commands act on the focused grid, and Run in a zone re-runs its statement.
/// </summary>
[Collection(UiTestCollection.Name)]
public class InlineZoneInputTests
{
    private readonly UiTestSession _ui;

    public InlineZoneInputTests(UiTestSession ui) => _ui = ui;

    /// <summary>A left click on a realized cell. Hit-testing reads the last rendered scene, and a zone's visuals
    /// are new since then — so render once first, as the real app would have long before anyone could click.</summary>
    internal static void ClickCell(ShellHarness shell, InlineZoneView zone, object?[] row, int column)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        shell.Pump();
        var cell = ResultsHarness.RequireCell(zone, row, column);
        var point = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), shell.Window)!.Value;
        shell.Window.MouseDown(point, MouseButton.Left);
        shell.Window.MouseUp(point, MouseButton.Left);
        shell.Pump();
    }

    private static void Press(ShellHarness shell, Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        shell.Window.KeyPress(key, modifiers, physical, null);
        shell.Window.KeyRelease(key, modifiers, physical, null);
        shell.Pump();
    }

    private static async Task<(ShellHarness Shell, TextEditor Editor, EditorTabViewModel Tab, InlineZoneView Zone, ResultSetViewModel Set)>
        ZoneWithEditableGrid(string name, FakeDialogs? dialogs = null)
    {
        var shell = await ShellHarness.ShowAsync(name, dialogs: dialogs);
        var (editor, tab) = InlineResultsTests.Setup(shell);
        var set = InlineZoneGridTests.Editable();
        InlineZoneGridTests.Place(shell, tab, set);
        return (shell, editor, tab, Assert.Single(InlineResultsTests.Zones(editor)), set);
    }

    [Fact]
    public Task Clicking_a_cell_gives_the_zones_grid_the_keyboard_and_leaves_the_caret() => _ui.Run(async () =>
    {
        var (shell, editor, _, zone, set) = await ZoneWithEditableGrid(nameof(Clicking_a_cell_gives_the_zones_grid_the_keyboard_and_leaves_the_caret));
        using var _ = shell;
        editor.CaretOffset = 3;

        ClickCell(shell, zone, set.Rows[1], 1);

        Assert.True(Assert.Single(zone.Results.Grids).IsKeyboardFocusWithin);
        Assert.Equal(3, editor.CaretOffset);
        Assert.True(editor.TextArea.Selection.IsEmpty);
    });

    [Fact]
    public Task Keys_and_typing_in_a_zone_never_change_the_sql() => _ui.Run(async () =>
    {
        var (shell, editor, _, zone, set) = await ZoneWithEditableGrid(nameof(Keys_and_typing_in_a_zone_never_change_the_sql));
        using var _ = shell;
        var text = editor.Text;
        ClickCell(shell, zone, set.Rows[0], 1);
        Assert.True(Assert.Single(zone.Results.Grids).IsKeyboardFocusWithin);   // the handler ran on a focused grid

        shell.Window.KeyTextInput("xyz");
        Press(shell, Key.Down, PhysicalKey.ArrowDown);
        Press(shell, Key.Back, PhysicalKey.Backspace);
        Press(shell, Key.Delete, PhysicalKey.Delete);
        Press(shell, Key.Enter, PhysicalKey.Enter);       // opens the cell editor…
        shell.Window.KeyTextInput("typed in the cell");
        Press(shell, Key.Back, PhysicalKey.Backspace);    // …whose Backspace is the cell's, not the script's
        Press(shell, Key.Enter, PhysicalKey.Enter);
        shell.Pump();

        Assert.Equal(text, editor.Text);
        Assert.True(set.HasPendingChanges);   // the keys did reach the grid
    });

    [Fact]
    public Task Escape_gives_the_keyboard_back_to_the_editor_at_the_statements_end() => _ui.Run(async () =>
    {
        var (shell, editor, tab, zone, set) = await ZoneWithEditableGrid(nameof(Escape_gives_the_keyboard_back_to_the_editor_at_the_statements_end));
        using var _ = shell;
        editor.CaretOffset = 14;
        ClickCell(shell, zone, set.Rows[0], 1);

        Press(shell, Key.Escape, PhysicalKey.Escape);

        Assert.True(editor.TextArea.IsFocused);
        Assert.Equal(tab.InlineZones[0].Offset, editor.CaretOffset);
    });

    [Fact]
    public Task Escape_in_a_cell_editor_cancels_the_edit_and_keeps_the_zone() => _ui.Run(async () =>
    {
        var (shell, editor, _, zone, set) = await ZoneWithEditableGrid(nameof(Escape_in_a_cell_editor_cancels_the_edit_and_keeps_the_zone));
        using var _ = shell;
        ClickCell(shell, zone, set.Rows[0], 1);
        Press(shell, Key.Enter, PhysicalKey.Enter);
        Assert.IsType<TextBox>(TopLevel.GetTopLevel(zone)!.FocusManager!.GetFocusedElement());

        Press(shell, Key.Escape, PhysicalKey.Escape);

        Assert.False(editor.TextArea.IsFocused);
        Assert.True(zone.IsKeyboardFocusWithin);
        Assert.False(set.HasPendingChanges);
    });

    [Fact]
    public Task Focus_results_takes_the_keyboard_to_the_carets_zone() => _ui.Run(async () =>
    {
        var (shell, editor, _, zone, _) = await ZoneWithEditableGrid(nameof(Focus_results_takes_the_keyboard_to_the_carets_zone));
        using var _ = shell;
        editor.CaretOffset = 3;
        shell.Pump();

        Press(shell, Key.D0, PhysicalKey.Digit0, RawInputModifiers.Control | RawInputModifiers.Shift);

        Assert.True(zone.IsKeyboardFocusWithin);
    });

    [Fact]
    public Task F6_cycles_from_the_editor_to_the_zone_and_on() => _ui.Run(async () =>
    {
        var (shell, editor, _, zone, _) = await ZoneWithEditableGrid(nameof(F6_cycles_from_the_editor_to_the_zone_and_on));
        using var _ = shell;
        editor.CaretOffset = 3;
        shell.Pump();

        Press(shell, Key.F6, PhysicalKey.F6);
        Assert.True(zone.IsKeyboardFocusWithin);

        Press(shell, Key.F6, PhysicalKey.F6);
        Assert.False(zone.IsKeyboardFocusWithin);   // on to the next region, not back into the same zone
    });

    [Fact]
    public Task Grid_commands_act_on_the_focused_zone() => _ui.Run(async () =>
    {
        var (shell, editor, tab, zone, set) = await ZoneWithEditableGrid(nameof(Grid_commands_act_on_the_focused_zone));
        using var _ = shell;
        var pane = InlineResultsTests.PaneResults(shell);
        var paneSet = InlineZoneGridTests.Editable();
        tab.SetFreshResults([paneSet]);
        pane.Results = tab.Results;
        shell.Pump();
        ClickCell(shell, zone, set.Rows[0], 1);

        Press(shell, Key.A, PhysicalKey.A, RawInputModifiers.Control);

        Assert.True(zone.Results.Selection.HasSelection(set));
        Assert.Equal(set.Rows.Count * set.Columns.Count, zone.Results.Selection.Model.Cells.Count);
        Assert.False(pane.Selection.HasSelection(paneSet));
    });

    [Fact]
    public Task Run_with_focus_in_a_zone_reruns_that_zones_statement() => _ui.Run(async () =>
    {
        var providers = new DemoProvider(DemoExecutor.Default(sql => StatementSplitter.Split(sql).Select(s => s.Text).ToList()));
        using var shell = await ShellHarness.ShowAsync(nameof(Run_with_focus_in_a_zone_reruns_that_zones_statement), providers);
        await shell.Vm.StartDemoAsync(shell.ProjectDirectory, DemoMode.WelcomeScript);
        var (editor, tab) = InlineResultsTests.Setup(shell, "select id from shop.payment;\n\nselect id from shop.payment;\n");
        var second = "select id from shop.payment;\n\n".Length;
        editor.CaretOffset = second + 3;
        Press(shell, Key.F5, PhysicalKey.F5);
        await WaitFor(shell, () => tab.InlineZones.Count == 1);
        var before = Assert.Single(tab.InlineZones);
        var view = Assert.Single(InlineResultsTests.Zones(editor));
        editor.CaretOffset = 3;                                  // the caret is on the first statement…
        ClickCell(shell, view, before.Sets[0].Rows[0], 0);       // …but the keyboard is in the second's zone

        Press(shell, Key.F5, PhysicalKey.F5);
        await WaitFor(shell, () => !ReferenceEquals(tab.InlineZones.Single().Sets, before.Sets));

        var after = Assert.Single(tab.InlineZones);   // nothing placed under the first statement
        Assert.NotSame(before, after);
        Assert.True(after.Offset > second);
        Assert.True(Assert.Single(InlineResultsTests.Zones(editor)).IsKeyboardFocusWithin);   // focus followed it
    });

    internal static async Task WaitFor(ShellHarness shell, System.Func<bool> done)
    {
        for (var i = 0; i < 150 && !done(); i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
            shell.Pump();
        }
        shell.Pump();
    }

    /// <summary>A value still in a zone's cell editor is an unsaved edit (§9.15): closing the tab commits it
    /// before the guard counts, so the user is asked rather than the value silently dropped.</summary>
    [Fact]
    public Task Closing_the_tab_with_a_value_still_in_a_cell_editor_asks_first() => _ui.Run(async () =>
    {
        var dialogs = new FakeDialogs { DiscardEditsAnswer = false };
        var (shell, _, tab, zone, set) = await ZoneWithEditableGrid(
            nameof(Closing_the_tab_with_a_value_still_in_a_cell_editor_asks_first), dialogs);
        using var _ = shell;
        shell.Vm.Workspace.NewTab();   // a second tab, so the close isn't refused for being the last
        shell.Vm.Workspace.SelectedTab = tab;
        shell.Pump();
        ClickCell(shell, zone, set.Rows[0], 1);
        Press(shell, Key.Enter, PhysicalKey.Enter);
        shell.Window.KeyTextInput("typed");
        Assert.IsType<TextBox>(TopLevel.GetTopLevel(zone)!.FocusManager!.GetFocusedElement());
        Assert.False(set.HasPendingChanges);   // still in the cell editor, not yet pending

        Assert.False(await shell.Vm.Workspace.CloseTabAsync(tab));

        Assert.Contains(dialogs.DiscardEditsPrompts, p => p.Action.StartsWith("close"));
        Assert.True(set.HasPendingChanges);
        Assert.Contains(tab, shell.Vm.Workspace.Tabs);
    });

    /// <summary>Quit with nothing running and no transaction, only a value still in a cell editor: the window
    /// commits it and holds the close to ask (it asks through its own dialog service, so what is asserted is
    /// that the close was held and the value became pending — before, nothing counted and the app closed).</summary>
    [Fact]
    public Task Quitting_with_only_a_value_in_a_cell_editor_is_held_to_ask() => _ui.Run(async () =>
    {
        var (shell, _, _, zone, set) = await ZoneWithEditableGrid(nameof(Quitting_with_only_a_value_in_a_cell_editor_is_held_to_ask));
        using var _ = shell;
        ClickCell(shell, zone, set.Rows[0], 1);
        Press(shell, Key.Enter, PhysicalKey.Enter);
        shell.Window.KeyTextInput("typed");
        Assert.False(set.HasPendingChanges);

        shell.Window.Close();
        shell.Pump();

        Assert.True(set.HasPendingChanges);
        Assert.True(QuitGuard.NeedsConfirmation(shell.Vm));
        Assert.True(shell.Window.IsVisible);
        set.RevertPending();   // let the harness close it
    });

    [Fact]
    public Task Ctrl_wheel_in_a_zone_does_not_zoom_the_sql() => _ui.Run(async () =>
    {
        var (shell, editor, tab, zone, set) = await ZoneWithEditableGrid(nameof(Ctrl_wheel_in_a_zone_does_not_zoom_the_sql));
        using var _ = shell;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        shell.Pump();
        var cell = ResultsHarness.RequireCell(zone, set.Rows[0], 1);
        var inZone = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), shell.Window)!.Value;
        var inText = editor.TextArea.TranslatePoint(new Point(40, 4), shell.Window)!.Value;

        shell.Window.MouseWheel(inZone, new Vector(0, 1), RawInputModifiers.Control);
        shell.Pump();
        Assert.Equal(0, tab.FontZoomSteps);

        shell.Window.MouseWheel(inText, new Vector(0, 1), RawInputModifiers.Control);   // the gesture itself works
        shell.Pump();
        Assert.NotEqual(0, tab.FontZoomSteps);
    });

    [Fact]
    public Task An_alt_tap_in_a_zone_toggles_the_menu_bar() => _ui.Run(async () =>
    {
        var (shell, _, _, zone, set) = await ZoneWithEditableGrid(nameof(An_alt_tap_in_a_zone_toggles_the_menu_bar));
        using var _ = shell;
        ClickCell(shell, zone, set.Rows[0], 1);
        Assert.True(zone.IsKeyboardFocusWithin);
        var before = shell.Vm.IsMenuVisible;
        Assert.False(shell.Vm.IsMenuPinned && before);   // pinned, a tap has nothing to toggle

        Press(shell, Key.LeftAlt, PhysicalKey.AltLeft);

        Assert.NotEqual(before, shell.Vm.IsMenuVisible);
    });

    /// <summary>A menu access key (Alt+V) from a zone's grid opens its menu, as it does from the pane's grid: the
    /// zone stops its keys from reaching the editor's text area, but not the window's access-key handler.</summary>
    [Fact]
    public Task Alt_access_keys_in_a_zone_reach_the_menu() => _ui.Run(async () =>
    {
        var (shell, _, _, zone, set) = await ZoneWithEditableGrid(nameof(Alt_access_keys_in_a_zone_reach_the_menu));
        using var _ = shell;
        ClickCell(shell, zone, set.Rows[0], 1);
        Assert.True(zone.IsKeyboardFocusWithin);
        shell.Vm.IsMenuVisible = true;   // after the click, which hides an unpinned menu bar
        shell.Pump();
        var view = shell.Window.GetVisualDescendants().OfType<MenuItem>().Single(m => Equals(m.Header, "_View"));
        Assert.False(view.IsSubMenuOpen);

        shell.Window.KeyPress(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
        shell.Window.KeyPress(Key.V, RawInputModifiers.Alt, PhysicalKey.V, "v");
        shell.Window.KeyRelease(Key.V, RawInputModifiers.Alt, PhysicalKey.V, "v");
        shell.Window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
        shell.Pump();

        Assert.True(view.IsSubMenuOpen);
    });

    /// <summary>An Alt chord the window binds (Alt+P, tab.togglePin) runs once from a zone: the zone leaves
    /// Alt+letter to the window rather than offering it to the global commands itself as well.</summary>
    [Fact]
    public Task An_alt_chord_in_a_zone_runs_its_global_command_once() => _ui.Run(async () =>
    {
        var (shell, _, tab, zone, set) = await ZoneWithEditableGrid(nameof(An_alt_chord_in_a_zone_runs_its_global_command_once));
        using var _ = shell;
        ClickCell(shell, zone, set.Rows[0], 1);
        Assert.True(zone.IsKeyboardFocusWithin);
        var pinned = tab.IsPinned;

        Press(shell, Key.P, PhysicalKey.P, RawInputModifiers.Alt);

        Assert.NotEqual(pinned, tab.IsPinned);
    });

    /// <summary>In a zone's cell editor Alt+letter is text (macOS Option composes characters), not a menu access
    /// key: the menu stays shut.</summary>
    [Fact]
    public Task Alt_letter_in_a_zone_cell_editor_does_not_open_a_menu() => _ui.Run(async () =>
    {
        var (shell, _, _, zone, set) = await ZoneWithEditableGrid(nameof(Alt_letter_in_a_zone_cell_editor_does_not_open_a_menu));
        using var _ = shell;
        ClickCell(shell, zone, set.Rows[0], 1);
        Press(shell, Key.Enter, PhysicalKey.Enter);   // opens the cell editor
        Assert.IsType<TextBox>(shell.Window.FocusManager?.GetFocusedElement());
        shell.Vm.IsMenuVisible = true;
        shell.Pump();
        var view = shell.Window.GetVisualDescendants().OfType<MenuItem>().Single(m => Equals(m.Header, "_View"));

        shell.Window.KeyPress(Key.V, RawInputModifiers.Alt, PhysicalKey.V, "v");
        shell.Window.KeyRelease(Key.V, RawInputModifiers.Alt, PhysicalKey.V, "v");
        shell.Pump();

        Assert.False(view.IsSubMenuOpen);
        Press(shell, Key.Escape, PhysicalKey.Escape);   // cancel the cell edit
    });

    /// <summary>grid.back's key (Alt+Left) in a zone while the tab's frames are being changed is refused with the
    /// same status line as the Back button — not silently passed on.</summary>
    [Fact]
    public Task Back_key_in_a_zone_says_why_it_waits() => _ui.Run(async () =>
    {
        var (shell, _, tab, zone, set) = await ZoneWithEditableGrid(nameof(Back_key_in_a_zone_says_why_it_waits));
        using var _ = shell;
        var navigated = InlineZoneGridTests.Editable();
        zone.Zone.Push([navigated]);
        zone.Zone.NotifyChanged();
        shell.Pump();
        ClickCell(shell, zone, navigated.Rows[0], 1);
        Assert.True(zone.IsKeyboardFocusWithin);
        shell.Vm.StatusText = "";
        tab.BeginRun(RunKind.FkLookup);
        try
        {
            Press(shell, Key.Left, PhysicalKey.ArrowLeft, RawInputModifiers.Alt);   // grid.back

            Assert.Equal("Wait for the foreign-key lookup to finish before going back.", shell.Vm.StatusText);
            Assert.Same(navigated, Assert.Single(zone.Zone.Sets));
        }
        finally
        {
            tab.EndRun();
        }
        Press(shell, Key.Left, PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        Assert.Same(set, Assert.Single(zone.Zone.Sets));
    });
}
