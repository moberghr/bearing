using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Bearing.App.Input;
using Bearing.App.ViewModels;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// An open cell editor and the pending set (#147, #148).
/// <para>
/// The claim each of these holds is "the save saw the value that was on screen", which nothing but a
/// realized grid can answer: <c>DataGrid.CellEditEnding</c> is the only route into
/// <see cref="ResultSetViewModel.SetCell"/>, and which gestures raise it is the DataGrid's behaviour rather
/// than ours (§4.3/§4.5). Before the fix, every one of these tests measured the *previous* value.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class CellEditCommitTests
{
    private readonly UiTestSession _ui;

    public CellEditCommitTests(UiTestSession ui) => _ui = ui;

    /// <summary>The report this came from: type, click ✓ Save without pressing Enter, and the save ran with
    /// the value the cell held *before* the typing — so a re-edit after a failed save looked stuck on the
    /// first one.</summary>
    [Fact]
    public Task Clicking_Save_commits_the_open_editor_first() => _ui.Run(() =>
    {
        var (result, rows) = ResultsHarness.WideEditableResult(columns: 4, rows: 6);
        var (window, view) = ResultsHarness.Show(result);
        object? seenBySave = null;
        view.SaveChanges = _ => { seenBySave = rows[1][1]; return Task.CompletedTask; };

        // An earlier pending change, so the commit group (and its Save button) is on screen at all.
        result.SetCell(rows[0], 1, "first-edit");
        ResultsHarness.Pump(window);

        TypeInCell(window, view, result, rows[1], column: 1, text: "typed-then-saved");
        Press(window, SaveButton(view));
        ResultsHarness.Pump(window);

        Assert.Equal("typed-then-saved", rows[1][1]);
        Assert.Equal("typed-then-saved", seenBySave);
        window.Close();
    });

    /// <summary>Focus leaving the grid commits, the way a spreadsheet does. Without it the edit hung in a
    /// TextBox nothing reads: not in the buffer, not in the pending count, not in a Save.</summary>
    [Fact]
    public Task Focus_leaving_the_grid_commits_the_open_editor() => _ui.Run(() =>
    {
        var (result, rows) = ResultsHarness.WideEditableResult(columns: 4, rows: 6);
        var (window, view) = ResultsHarness.Show(result);

        TypeInCell(window, view, result, rows[1], column: 1, text: "typed-then-clicked-away");
        Assert.Equal(0, result.PendingCount);                 // still only in the editor

        // Pressed, not clicked: the press is the focus move being measured, and releasing would run ＋ Add's
        // own Click — which moves the cell cursor onto the new row and so commits the edit by a different
        // route entirely. A test written with a full click passes without the fix and measures nothing.
        PressOnly(window, Button(view, "Add"));
        ResultsHarness.Pump(window);

        Assert.Equal("typed-then-clicked-away", rows[1][1]);
        Assert.Equal(1, result.PendingCount);
        window.Close();
    });

    /// <summary>Ctrl+S inside a cell editor is the row's save. It used to leave the grid unhandled, and the
    /// window answered it with <c>file.save</c> — writing the script to disk instead.</summary>
    [Fact]
    public Task Ctrl_S_in_a_cell_editor_saves_the_row_and_does_not_reach_the_window() => _ui.Run(() =>
    {
        var (result, rows) = ResultsHarness.WideEditableResult(columns: 4, rows: 6);
        var (window, view) = ResultsHarness.Show(result);
        object? seenBySave = null;
        var saves = 0;
        view.SaveChanges = _ => { saves++; seenBySave = rows[1][1]; return Task.CompletedTask; };
        Dispatch(view);

        var reachedWindowUnhandled = false;
        window.AddHandler(InputElement.KeyDownEvent,
            (object? _, KeyEventArgs e) => { if (e.Key == Key.S && !e.Handled) reachedWindowUnhandled = true; },
            RoutingStrategies.Bubble, handledEventsToo: true);

        TypeInCell(window, view, result, rows[1], column: 1, text: "typed-then-ctrl-s");
        window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
        ResultsHarness.Pump(window);

        Assert.Equal(1, saves);
        Assert.Equal("typed-then-ctrl-s", seenBySave);
        Assert.False(reachedWindowUnhandled);                 // …so file.save never ran
        window.Close();
    });

    /// <summary>Escape is still the way out. The commit-on-focus-loss must not turn an abandoned edit into a
    /// written one — Escape cancels before focus goes anywhere.</summary>
    [Fact]
    public Task Escape_in_a_cell_editor_still_cancels_the_edit() => _ui.Run(() =>
    {
        var (result, rows) = ResultsHarness.WideEditableResult(columns: 4, rows: 6);
        var (window, view) = ResultsHarness.Show(result);
        Dispatch(view);

        TypeInCell(window, view, result, rows[1], column: 1, text: "typed-then-escaped");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "");
        ResultsHarness.Pump(window);

        Assert.Equal("r2c1-value", rows[1][1]);
        Assert.Equal(0, result.PendingCount);

        // And the cancelled edit must not come back when focus later leaves the grid.
        Press(window, Button(view, "Add"));
        ResultsHarness.Pump(window);
        Assert.Equal("r2c1-value", rows[1][1]);
        window.Close();
    });

    /// <summary>Only the three commit commands are taken from a focused editor. Ctrl+C there is the text's,
    /// and taking it would be a worse bug than the one being fixed.</summary>
    [Fact]
    public Task Ctrl_C_in_a_cell_editor_still_belongs_to_the_editor() => _ui.Run(() =>
    {
        var (result, rows) = ResultsHarness.WideEditableResult(columns: 4, rows: 6);
        var (window, view) = ResultsHarness.Show(result);
        Dispatch(view);

        TypeInCell(window, view, result, rows[1], column: 1, text: "still-being-typed");
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        ResultsHarness.Pump(window);

        // Nothing committed: the editor kept the key, so the cell is still being edited.
        Assert.Equal("r2c1-value", rows[1][1]);
        Assert.NotNull(Editor(view));
        window.Close();
    });

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>Open the cell's editor and put <paramref name="text"/> in it, asserting the editor is really
    /// there first — a test that types into nothing passes while testing nothing (§4.5).</summary>
    private static void TypeInCell(
        Window window, Bearing.App.Controls.ResultView view, ResultSetViewModel result,
        object?[] row, int column, string text)
    {
        var grid = ResultsHarness.Grid(view);
        grid.Focus();
        view.Selection.MoveActive(grid, result, row, column, extend: false);
        view.Selection.BeginEditActive(grid, result);
        ResultsHarness.Pump(window);

        var box = Editor(view);
        Assert.NotNull(box);
        box!.Text = text;
        ResultsHarness.Pump(window);
    }

    private static TextBox? Editor(Bearing.App.Controls.ResultView view)
        => ResultsHarness.Grid(view).GetVisualDescendants().OfType<TextBox>().FirstOrDefault();

    private static void Dispatch(Bearing.App.Controls.ResultView view)
    {
        var registry = new CommandRegistry();
        view.RegisterGridCommands(registry);
        view.CommandDispatcher = new KeyDispatcher(KeymapDefaults.Build(), registry);
    }

    private static Button SaveButton(Bearing.App.Controls.ResultView view)
        => view.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "✓ Save");

    /// <summary>A meta-row button, found by the label its content panel holds (<c>ResultChrome.SubtleButton</c>
    /// splits the caption into one TextBlock per word).</summary>
    private static Button Button(Bearing.App.Controls.ResultView view, string word)
        => view.GetVisualDescendants().OfType<Button>()
            .First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == word));

    private static void Press(Window window, Visual target)
    {
        var centre = Centre(window, target);
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
    }

    /// <summary>The press without the release — a focus move with none of the button's own behaviour.</summary>
    private static void PressOnly(Window window, Visual target)
        => window.MouseDown(Centre(window, target), MouseButton.Left);

    private static Point Centre(Window window, Visual target)
        => target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
           ?? new Point(0, 0);
}
