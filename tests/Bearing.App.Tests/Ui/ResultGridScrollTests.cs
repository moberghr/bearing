using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// What moves the results viewport, and what must not (#60).
/// <para>
/// <b>The jump #60 describes is real, and it was ours.</b> Scroll a result right, then click anything in it
/// — a cell, a column header, the row-number gutter, the corner — and the whole grid snapped back to column
/// zero. The cause was the grid's own <c>GotFocus</c> seed: <c>SeedActive</c> named the result's
/// <i>absolute</i> first cell and <c>MoveActive</c> scrolled to it, so the click that first focused a grid
/// dragged the viewport to the top-left before the click's own selection was applied. Only a cell click had
/// a corrective (<c>ResultCellFactory.KeepClickedCellInView</c>), and even that only pulled the clicked cell
/// <i>minimally</i> back into view — a no-op whenever the cell was already visible at offset zero, which is
/// exactly the case the user sees.
/// </para>
/// <para>
/// It fires only on the <b>first</b> click into a grid that does not already hold the cursor, which is why
/// it read as intermittent: click twice and the second click is clean. Reproduced here by scrolling with
/// <c>ScrollIntoView</c> and then clicking a cell that is <i>not</i> the one scrolled to — the older test
/// below clicks the very cell it scrolled to, whose restore happens to land back on the same offset, and
/// that is what hid this.
/// </para>
/// <para>
/// The seed now names the first cell already on screen and does not scroll, so focus arriving is no longer
/// a viewport event. Committing an edit was never the cause, contrary to #60's first two suspects; those
/// guards are kept below.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class ResultGridScrollTests
{
    private readonly UiTestSession _ui;

    public ResultGridScrollTests(UiTestSession ui) => _ui = ui;

    /// <summary>A click on a cell while scrolled right and down leaves the viewport where it was. This is the
    /// regression guard for <c>KeepClickedCellInView</c>: delete that corrective and both offsets go to 0.</summary>
    [Fact]
    public Task Clicking_a_cell_while_scrolled_keeps_the_viewport_still() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        var target = rows[35];
        grid.ScrollIntoView(target, grid.Columns[10]);
        ResultsHarness.Pump(window);

        var (beforeH, beforeV) = Offsets(grid);
        Assert.True(beforeH > 0 && beforeV > 0, $"fixture must scroll both ways, got h={beforeH} v={beforeV}");
        var before = ResultsHarness.PositionIn(ResultsHarness.RequireCell(view, target, 10), grid);

        Click(window, ResultsHarness.RequireCell(view, target, 10));
        ResultsHarness.Pump(window);

        Assert.Equal((beforeH, beforeV), Offsets(grid));
        Assert.Equal(before, ResultsHarness.PositionIn(ResultsHarness.RequireCell(view, target, 10), grid));
        window.Close();
    });

    /// <summary>The regression #60 actually describes: scrolled right, click a cell that is <i>not</i> the
    /// one scrolled to. Before the fix both offsets went to zero and stayed there — the clicked cell was
    /// already visible at offset zero, so the corrective had nothing to correct.</summary>
    [Fact]
    public Task Clicking_a_cell_left_of_the_scrolled_one_keeps_the_viewport_still() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        var target = rows[20];
        grid.ScrollIntoView(target, grid.Columns[12]);
        ResultsHarness.Pump(window);
        var before = Offsets(grid);
        Assert.True(before.Horizontal > 0, $"fixture must scroll right, got {before.Horizontal}");

        // Visible, but well inside the viewport rather than at its right edge.
        var cell = ResultsHarness.RequireCell(view, target, 9);
        var at = ResultsHarness.PositionIn(cell, grid);
        Click(window, cell);
        ResultsHarness.Pump(window);

        Assert.Equal(before, Offsets(grid));
        Assert.Equal(at, ResultsHarness.PositionIn(ResultsHarness.RequireCell(view, target, 9), grid));
        window.Close();
    });

    /// <summary>A press is not a navigation. Clicking the sliver of a column clipped by the left edge of
    /// the viewport used to slide the whole result sideways to reveal that column in full — the last thing
    /// <c>KeepClickedCellInView</c> still did once the seed stopped jumping. Nothing the user clicks needs
    /// revealing: they could see it well enough to click it.</summary>
    [Fact]
    public Task Clicking_a_cell_clipped_by_the_left_edge_does_not_reveal_it() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        var target = rows[20];
        grid.ScrollIntoView(target, grid.Columns[12]);
        ResultsHarness.Pump(window);
        var before = Offsets(grid);

        // The left-most cell still on screen is the one the viewport cuts in half.
        var clipped = grid.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Tag is ValueTuple<object?[], int> t && ReferenceEquals(t.Item1, target))
            .Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0)
            .Select(b => (Cell: b, X: b.TranslatePoint(default, grid)?.X ?? 0))
            .OrderBy(t => t.X)
            .First();
        Assert.True(clipped.X < 0, $"fixture must clip a cell at the left edge, got x={clipped.X}");

        // Aimed at the visible sliver, near the cell's right-hand side.
        var at = clipped.Cell.TranslatePoint(
                     new Point(clipped.Cell.Bounds.Width * 0.95, clipped.Cell.Bounds.Height / 2), window)
                 ?? throw new InvalidOperationException("cell is not connected to the window");
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        ResultsHarness.Pump(window);

        Assert.Equal(before, Offsets(grid));
        var col = ((ValueTuple<object?[], int>)clipped.Cell.Tag!).Item2;
        Assert.Contains((target, col), view.Selection.Model.Cells);  // …and the press still selected it
        window.Close();
    });

    /// <summary>The same for the three presses that never had a corrective at all: a column header, the
    /// row-number gutter and the corner. Each of them focuses the grid, and focus is what used to scroll.</summary>
    [Theory]
    [InlineData(GridPress.ColumnHeader)]
    [InlineData(GridPress.RowGutter)]
    [InlineData(GridPress.Corner)]
    public Task Clicking_the_grids_chrome_while_scrolled_keeps_the_viewport_still(GridPress press) => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        grid.ScrollIntoView(rows[20], grid.Columns[12]);
        ResultsHarness.Pump(window);
        var before = Offsets(grid);
        Assert.True(before.Horizontal > 0, $"fixture must scroll right, got {before.Horizontal}");

        Click(window, ChromeTarget(view, grid, press));
        ResultsHarness.Pump(window);

        Assert.Equal(before, Offsets(grid));
        window.Close();
    });

    /// <summary>…and the press still did its job. A guard on the guard: "the viewport did not move" would
    /// also pass if the click had missed everything.</summary>
    [Fact]
    public Task A_column_header_click_while_scrolled_still_selects_the_column() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        grid.ScrollIntoView(rows[20], grid.Columns[12]);
        ResultsHarness.Pump(window);

        var header = (DataGridColumnHeader)ChromeTarget(view, grid, GridPress.ColumnHeader);
        Click(window, header);
        ResultsHarness.Pump(window);

        var column = GridSelectionController.ColumnIndexOfHeader(grid, header);
        Assert.NotNull(column);
        Assert.Equal(rs.Rows.Count, view.Selection.Model.Cells.Count);
        Assert.All(view.Selection.Model.Cells, c => Assert.Equal(column, c.Col));
        window.Close();
    });

    /// <summary>
    /// The quick-stats bar appearing does not move the viewport — the second cause the deleted
    /// <c>KeepClickedCellInView</c> was posted at <c>Loaded</c> priority to correct.
    /// <para>
    /// The bar really does re-measure the grid: it costs about 30px of height, measured. What it does not do
    /// is change either scroll offset, so the cell you pressed stays exactly where it was. The trade-off
    /// that remains is occlusion rather than movement — press a cell in the bottom 30px, extend the
    /// selection, and the bar now covers it — and holding still is the better of the two, per the rule this
    /// change is built on.
    /// </para>
    /// <para>
    /// This needs <see cref="ResultsHarness.WideNumericResult"/>: the editable fixture's only number is its
    /// primary key, and <c>MeasureValues</c> excludes PK and FK columns, so no selection in it can make the
    /// bar appear at all. A first version of this test used it and silently asserted nothing.
    /// </para></summary>
    [Fact]
    public Task The_stats_bar_appearing_does_not_move_the_viewport() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideNumericResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        grid.ScrollIntoView(rows[30], grid.Columns[12]);
        ResultsHarness.Pump(window);
        var before = Offsets(grid);
        Assert.True(before.Horizontal > 0 && before.Vertical > 0, $"fixture must scroll both ways, got {before}");

        Click(window, ResultsHarness.RequireCell(view, rows[28], 9));
        ResultsHarness.Pump(window);
        Assert.False(ResultsHarness.StatsBarVisible(view), "one cell must not raise the bar");

        // A second measure cell is what the bar waits for.
        ShiftClick(window, ResultsHarness.RequireCell(view, rows[29], 9));
        ResultsHarness.Pump(window);

        Assert.True(ResultsHarness.StatsBarVisible(view), "two measure cells must raise the bar");
        Assert.Equal(2, view.Selection.Model.Cells.Count);
        Assert.Equal(before, Offsets(grid));
        window.Close();
    });

    /// <summary>
    /// A press on the grid's chrome leaves the cell cursor <b>on screen</b>, so the next arrow key moves
    /// from what the user is looking at.
    /// <para>
    /// Found in review, and it was the sting in the tail of the seed fix: the band's origin is the result's
    /// first row (a column header) or first column (the row gutter), and while focusing used to scroll to
    /// the top-left that origin was on screen by accident. With the viewport correctly held still it is not,
    /// and one Down afterwards yanked the grid from offset 386 to 27 — the same jump, one keystroke later.
    /// </para>
    /// <para>
    /// The band's <i>anchor</i> is still the origin, so a later Shift+click extends from where the band
    /// starts; only the cursor is moved into view. That split is why <c>SelectBand</c> takes both.
    /// </para></summary>
    [Theory]
    [InlineData(GridPress.ColumnHeader)]
    [InlineData(GridPress.RowGutter)]
    [InlineData(GridPress.Corner)]
    public Task A_chrome_press_leaves_the_cursor_on_screen(GridPress press) => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        grid.ScrollIntoView(rows[35], grid.Columns[12]);
        ResultsHarness.Pump(window);
        var before = Offsets(grid);

        Click(window, ChromeTarget(view, grid, press));
        ResultsHarness.Pump(window);

        var active = view.Selection.Model.Active;
        Assert.NotNull(active);
        // Realized is the assertable form of "on screen": the DataGrid builds no visual for a cell outside
        // the viewport (§4.5).
        Assert.NotNull(ResultsHarness.Cell(view, active!.Value.Row, active.Value.Col));

        // …and the first arrow key therefore moves within the viewport instead of travelling to it.
        grid.Focus();
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, "");
        ResultsHarness.Pump(window);
        Assert.Equal(before, Offsets(grid));
        window.Close();
    });

    /// <summary>Focus arriving with no cursor yet (F6 into the results, or the first click) seeds one — and
    /// the seeded cell is on screen, which is the whole reason the seed exists. Before the fix it was the
    /// result's first cell, and the grid scrolled to it to make that true.</summary>
    [Fact]
    public Task Focusing_a_scrolled_grid_seeds_the_cursor_in_view_without_scrolling() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        grid.ScrollIntoView(rows[35], grid.Columns[12]);
        ResultsHarness.Pump(window);
        var before = Offsets(grid);
        Assert.True(before.Horizontal > 0 && before.Vertical > 0, $"fixture must scroll both ways, got {before}");

        Assert.Null(view.Selection.Model.Active);
        grid.Focus();
        ResultsHarness.Pump(window);

        Assert.Equal(before, Offsets(grid));
        var active = view.Selection.Model.Active;
        Assert.NotNull(active);
        Assert.NotEqual(0, active!.Value.Col);      // the result's first column is off to the left by now
        Assert.NotSame(rows[0], active.Value.Row);  // …and its first row is above the fold

        // …and it is a cell the user can actually see.
        var at = ResultsHarness.PositionIn(
            ResultsHarness.RequireCell(view, active.Value.Row, active.Value.Col), grid);
        Assert.InRange(at.X, 0, grid.Bounds.Width);
        Assert.InRange(at.Y, 0, grid.Bounds.Height);
        window.Close();
    });

    /// <summary>The first arrow key into a grid seeds the same way, so the keyboard route does not jump
    /// either — it moves from the cell you are looking at.</summary>
    [Fact]
    public Task The_first_arrow_key_into_a_scrolled_grid_does_not_jump() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        grid.ScrollIntoView(rows[20], grid.Columns[12]);
        ResultsHarness.Pump(window);
        var before = Offsets(grid);

        view.Selection.Clear();                  // as if focus had never landed here
        var arrow = new KeyEventArgs { Key = Key.Right, RoutedEvent = InputElement.KeyDownEvent };
        Assert.True(view.Selection.HandleNavigation(grid, rs, arrow), "the arrow was not handled");
        ResultsHarness.Pump(window);

        Assert.Equal(before, Offsets(grid));
        Assert.NotNull(view.Selection.Model.Active);
        window.Close();
    });

    /// <summary>
    /// One Tab is one scroll. Avalonia's <c>DataGrid_KeyUp</c> answers a Tab <b>release</b> with
    /// <c>ScrollSlotIntoView(…, forceHorizontalScroll: true)</c> aimed at the DataGrid's <i>own</i> current
    /// cell — which is not our cursor, so it is wherever the last click left it. Tabbing therefore scrolled
    /// twice per keystroke and disagreed with itself: measured 304 on the press, 153 after the key down,
    /// then 56 on the release.
    /// <para>
    /// Note that <c>KeyPress</c> is key-<i>down</i> only, which is why no earlier test could see this: the
    /// release has to be sent explicitly.
    /// </para></summary>
    [Fact]
    public Task Tabbing_scrolls_once_per_keystroke_not_twice() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        // Put the cursor — and the DataGrid's own current cell — on the left, then scroll away from both.
        grid.ScrollIntoView(rows[5], grid.Columns[1]);
        ResultsHarness.Pump(window);
        Click(window, ResultsHarness.RequireCell(view, rows[5], 1));
        ResultsHarness.Pump(window);
        grid.ScrollIntoView(rows[5], grid.Columns[12]);
        ResultsHarness.Pump(window);

        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
        ResultsHarness.Pump(window);
        var afterKeyDown = Offsets(grid);

        window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
        ResultsHarness.Pump(window);

        // The key down moved the cursor one field and the viewport followed it — that is ours, and correct.
        // The release must add nothing.
        Assert.Equal(afterKeyDown, Offsets(grid));
        window.Close();
    });

    /// <summary>
    /// Committing an edit in the sliver of a column clipped by the viewport edge does not reveal it. The twin
    /// of <c>KeepClickedCellInView</c>, removed for the same reason and measured doing the same thing: 304 →
    /// 250, the jump #60 reports, from a commit instead of a click.
    /// </summary>
    [Fact]
    public Task Committing_an_edit_on_a_clipped_cell_does_not_reveal_it() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        var target = rows[20];
        grid.ScrollIntoView(target, grid.Columns[12]);
        ResultsHarness.Pump(window);

        var clipped = grid.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Tag is ValueTuple<object?[], int> t && ReferenceEquals(t.Item1, target))
            .Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0)
            .Select(b => (Cell: b, X: b.TranslatePoint(default, grid)?.X ?? 0))
            .OrderBy(t => t.X)
            .First();
        Assert.True(clipped.X < 0, $"fixture must clip a cell at the left edge, got x={clipped.X}");
        var col = ((ValueTuple<object?[], int>)clipped.Cell.Tag!).Item2;

        Click(window, clipped.Cell);
        ResultsHarness.Pump(window);
        var before = Offsets(grid);

        grid.SelectedItem = target;
        grid.CurrentColumn = grid.Columns[col];
        Assert.True(grid.BeginEdit(), "the grid refused to begin editing");
        ResultsHarness.Pump(window);
        var editor = grid.GetVisualDescendants().OfType<TextBox>().SingleOrDefault();
        Assert.NotNull(editor);
        editor!.Text = "edited";
        Assert.True(grid.CommitEdit(), "the grid refused to commit");
        ResultsHarness.Pump(window);

        Assert.Equal("edited", target[col]);          // the edit landed…
        Assert.Equal(before, Offsets(grid));          // …and the pane did not move to show it
        window.Close();
    });

    /// <summary>
    /// The one exception, pinned so it is not "tidied" away later: <b>beginning</b> an edit does reveal the
    /// cursor, because <c>grid.BeginEdit()</c> needs a realized cell to put an editor in.
    /// <para>
    /// Measured both ways. With the <c>ScrollIntoView</c> in <c>BeginEditActive</c> removed, this exact
    /// gesture leaves the viewport alone and opens <i>no editor at all</i> — F2 silently does nothing. So
    /// revealing the cell you asked to edit is part of executing the command, not the pane moving on its
    /// own, and it is the only keystroke still allowed to scroll (§9.10a).
    /// </para>
    /// <para>
    /// The cursor can be off screen at all only because the wheel and the scrollbar move the viewport
    /// without moving it — every other route keeps the two together.
    /// </para></summary>
    [Fact]
    public Task Beginning_an_edit_reveals_the_cursor_because_the_editor_needs_a_realized_cell() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        Click(window, ResultsHarness.RequireCell(view, rows[2], 1));
        ResultsHarness.Pump(window);
        Assert.Equal((rows[2], 1), view.Selection.Model.Active);

        // Scrolled away from the cursor, as a wheel or a scrollbar drag does.
        grid.ScrollIntoView(rows[35], grid.Columns[12]);
        ResultsHarness.Pump(window);
        Assert.Null(ResultsHarness.Cell(view, rows[2], 1));   // the cursor's cell has no visual at all now

        view.Selection.BeginEditActive(grid, rs);
        ResultsHarness.Pump(window);

        var editor = grid.GetVisualDescendants().OfType<TextBox>().SingleOrDefault();
        Assert.NotNull(editor);
        Assert.Equal("r3c1-value", editor!.Text);

        // …and the editor is inside the viewport, which is the point. Asserted on the editor rather than on
        // the cell: an editing cell has no tagged display border — the grid swaps it for the editing element
        // — so ResultsHarness.Cell is legitimately null here.
        var at = ResultsHarness.PositionIn(editor, grid);
        Assert.InRange(at.X, 0, grid.Bounds.Width);
        Assert.InRange(at.Y, 0, grid.Bounds.Height);
        window.Close();
    });

    /// <summary>Which piece of grid chrome a press is aimed at.</summary>
    public enum GridPress { ColumnHeader, RowGutter, Corner }

    /// <summary>The realized visual for one of those, picked so the press lands inside the viewport.</summary>
    private static Control ChromeTarget(ResultView view, DataGrid grid, GridPress press) => press switch
    {
        GridPress.Corner => view.GetVisualDescendants().OfType<Control>()
            .First(c => c.Name == "PART_TopLeftCornerHeader"),
        GridPress.RowGutter => view.GetVisualDescendants().OfType<DataGridRowHeader>()
            .First(h => h.IsVisible && h.Bounds.Width > 0),
        _ => view.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible && h.Bounds.Width > 0 && h.Name != "PART_TopLeftCornerHeader")
            .Select(h => (Header: h, X: h.TranslatePoint(default, grid)?.X ?? -1))
            .Where(t => t.X > 20 && t.X < grid.Bounds.Width - 20)
            .OrderBy(t => t.X)
            .Select(t => (Control)t.Header)
            .First(),
    };

    /// <summary>Committing an inline edit leaves the edited cell where it was. The one pixel of tolerance is
    /// the meta row growing as the Discard/Save group appears on the first pending edit — measured, and the
    /// reason #60 suggests reserving that group's height.</summary>
    [Fact]
    public Task Committing_an_edit_leaves_the_edited_cell_in_place() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        var target = rows[35];
        grid.ScrollIntoView(target, grid.Columns[10]);
        ResultsHarness.Pump(window);
        Click(window, ResultsHarness.RequireCell(view, target, 10));
        ResultsHarness.Pump(window);

        var (beforeH, _) = Offsets(grid);
        var before = ResultsHarness.PositionIn(ResultsHarness.RequireCell(view, target, 10), grid);

        Assert.True(grid.BeginEdit(), "the grid refused to begin editing");
        ResultsHarness.Pump(window);
        var editor = grid.GetVisualDescendants().OfType<TextBox>().SingleOrDefault();
        Assert.NotNull(editor);
        Assert.Equal("r36c10-value", editor.Text);
        editor.Text = "edited";
        Assert.True(grid.CommitEdit(), "the grid refused to commit");
        ResultsHarness.Pump(window);

        // The edit landed on the result set, pending rather than written.
        Assert.Equal("edited", target[10]);
        Assert.True(rs.HasPendingChanges);

        // …and the cell it landed on is still realized, in exactly the same place. No tolerance any more:
        // the one pixel that used to be here was the commit group appearing and re-measuring the grid, and
        // the meta row now reserves its height (ResultChrome.MetaRowContentHeight).
        var after = ResultsHarness.PositionIn(ResultsHarness.RequireCell(view, target, 10), grid);
        Assert.Equal(beforeH, Offsets(grid).Horizontal);
        Assert.Equal(before, after);
        window.Close();
    });

    /// <summary>The first pending edit does not change the grid's height. The commit group
    /// (● N pending · Discard · Save) is a pixel taller than the row's other buttons, so revealing it grew
    /// the meta row and re-measured the grid beneath it — which is the reflow #60 asks to be reserved
    /// away.</summary>
    [Fact]
    public Task The_first_pending_edit_does_not_reflow_the_grid() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        Assert.False(rs.HasPendingChanges);
        var before = grid.Bounds.Height;

        rs.SetCell(rows[0], 1, "edited");
        ResultsHarness.Pump(window);

        Assert.True(rs.HasPendingChanges, "the fixture must have made the commit group appear");
        Assert.Equal(before, grid.Bounds.Height);
        window.Close();
    });

    /// <summary>…and neither does discarding them again, so the reserve holds in both directions.</summary>
    [Fact]
    public Task Clearing_the_pending_edits_does_not_reflow_the_grid_either() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult();
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);

        rs.SetCell(rows[0], 1, "edited");
        ResultsHarness.Pump(window);
        var dirty = grid.Bounds.Height;

        rs.ClearPending();
        ResultsHarness.Pump(window);

        Assert.False(rs.HasPendingChanges);
        Assert.Equal(dirty, grid.Bounds.Height);
        window.Close();
    });

    /// <summary>The grid scrolls itself rather than sitting in a ScrollViewer, so its offsets are the
    /// scrollbars' values.</summary>
    private static (double Horizontal, double Vertical) Offsets(DataGrid grid)
    {
        var bars = grid.GetVisualDescendants().OfType<ScrollBar>().ToList();
        return (bars.First(b => b.Orientation == Orientation.Horizontal).Value,
                bars.First(b => b.Orientation == Orientation.Vertical).Value);
    }

    /// <summary>A real left click in the middle of a realized cell, through the windowing platform — so the
    /// cell's own PointerPressed handler runs, correctives and all.</summary>
    /// <summary>The same press with Shift held — extending a selection, which is what raises the stats
    /// bar. The modifier goes on the press and the release both, as the app reads it off the event.</summary>
    private static void ShiftClick(Window window, Control cell)
    {
        var point = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException("cell is not connected to the window");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.Shift);
    }

    private static void Click(Window window, Control cell)
    {
        var point = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException("cell is not connected to the window");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }
}
