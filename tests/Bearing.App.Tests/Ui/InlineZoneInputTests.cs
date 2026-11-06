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
        ZoneWithEditableGrid(string name)
    {
        var shell = await ShellHarness.ShowAsync(name);
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
}
