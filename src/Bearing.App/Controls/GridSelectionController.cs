using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Bearing.App.Formatting;
using Bearing.App.Results;
using Bearing.App.Services;
using Bearing.App.ViewModels;
using Avalonia.Input.Platform;
using Avalonia;

namespace Bearing.App.Controls;

/// <summary>What a press inside a results grid landed on, as far as selection is concerned. Returned by
/// <see cref="GridSelectionController.TrySelectFromHeader"/> so the view can react to a column selection
/// being partial without re-doing the hit test.</summary>
public enum GridPressTarget
{
    /// <summary>Grid chrome with no meaning for the selection — the scrollbar, the corner, empty space
    /// below the last row. The caller clears the selection on this one.</summary>
    None,

    /// <summary>A cell, which selects itself (see <c>ResultCellFactory.MakeSelectable</c>).</summary>
    Cell,

    /// <summary>The row-number gutter: the whole row is now selected.</summary>
    RowHeader,

    /// <summary>A column header: that column is now selected, over the loaded rows.</summary>
    ColumnHeader,
}

/// <summary>
/// Drives spreadsheet-style cell selection in the results grids: pointer clicks and drags, the keyboard cell
/// cursor, and the actions that read the selection (copy as TSV, mark rows deleted, begin editing).
/// <para>
/// Owns the <see cref="GridSelectionModel"/> state and is the only writer of it; the arithmetic lives in
/// <see cref="GridSelectionOps"/>. Everything visual reacts through <see cref="Changed"/> — the per-cell
/// selection rings via <see cref="GridSelectionModel.CellRestyle"/>, the quick-stats bars via the event —
/// so this class never reaches into the visuals it affects.
/// </para>
/// </summary>
public sealed class GridSelectionController
{
    private readonly Control _owner; // used only to reach the TopLevel's clipboard

    public GridSelectionController(Control owner) => _owner = owner;

    /// <summary>The selection state: selected cells, the owning result, the active + anchor cells, drag flags.</summary>
    public GridSelectionModel Model { get; } = new();

    /// <summary>Raised after any selection change, once the model is consistent. The stats bars listen.</summary>
    public event Action? Changed;

    /// <summary>Re-render everything that reflects the selection: each realized cell's ring, then the bars.</summary>
    public void Notify()
    {
        Model.CellRestyle?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Drop the selection without notifying — for callers that rebuild the visuals anyway.</summary>
    public void Clear() => Model.Clear();

    /// <summary>Subscribe a realized cell's ring-restyle. Cells must unsubscribe when they leave the visual
    /// tree, or a recycled container keeps restyling a Border nobody can see.</summary>
    public void AddRestyleListener(Action restyle) => Model.CellRestyle += restyle;

    /// <summary>Unsubscribe a cell's ring-restyle (on detach from the visual tree).</summary>
    public void RemoveRestyleListener(Action restyle) => Model.CellRestyle -= restyle;

    /// <summary>Forget every cell's restyle subscription — the cells that owned them are being discarded,
    /// and they re-subscribe as they rebuild.</summary>
    public void DropRestyleListeners() => Model.CellRestyle = null;

    /// <summary>Drop the selection and re-render (grid.clearSelection, the stats bar's Clear button).</summary>
    public void ClearAndNotify()
    {
        Model.Clear();
        Notify();
    }

    /// <summary>Whether a cell currently carries a selection ring.</summary>
    public bool IsSelected(ResultSetViewModel result, object?[] row, int col)
        => ReferenceEquals(Model.Result, result) && Model.Cells.Contains((row, col));

    /// <summary>True when the active cell sits in a foreign-key column (guards grid.followFk).</summary>
    public bool ActiveCellIsFk()
        => Model.Result is { } r && Model.Active is { } cell && r.ForeignKeyColumns.Contains(cell.Col);

    // ---- pointer ----------------------------------------------------------------------------

    /// <summary>Plain left-click: collapse the selection to this cell, re-seed the anchor, and arm a drag.</summary>
    public void SelectSingleAndBeginDrag(ResultSetViewModel result, object?[] row, int col, IPointer pointer, DataGrid grid)
    {
        Model.Result = result;
        Model.Cells.Clear();
        Model.Cells.Add((row, col));
        Model.Active = (row, col);
        Model.Anchor = (row, col);
        Model.Dragging = true;
        Model.DragAnchor = (row, col);
        pointer.Capture(grid);
        Notify();
    }

    /// <summary>Collapse the selection to a single cell without arming a drag — what a right-click that
    /// lands outside the current selection does, so the context menu can only ever act on cells the user is
    /// actually pointing at.</summary>
    public void SelectSingle(ResultSetViewModel result, object?[] row, int col)
    {
        Model.Result = result;
        Model.Cells.Clear();
        Model.Cells.Add((row, col));
        Model.Active = (row, col);
        Model.Anchor = (row, col);
        Notify();
    }

    /// <summary>Ctrl/Cmd-click: add or remove this one cell, and make it the new active + anchor.</summary>
    public void ToggleCell(ResultSetViewModel result, object?[] row, int col)
    {
        if (!ReferenceEquals(Model.Result, result)) { Model.Cells.Clear(); Model.Result = result; }
        var key = (row, col);
        if (!Model.Cells.Remove(key)) Model.Cells.Add(key);
        Model.Active = key;
        Model.Anchor = key;
        Notify();
    }

    /// <summary>Shift-click: rectangular range from the existing anchor to the clicked cell.</summary>
    public void ExtendTo(ResultSetViewModel result, object?[] row, int col)
    {
        if (Model.Anchor is not { } anchor || !ReferenceEquals(Model.Result, result)) return;
        SelectRectangle(result, anchor, (row, col));
        Model.Active = (row, col);
    }

    /// <summary>Whether a Shift-click can extend from an existing anchor in this result.</summary>
    public bool CanExtendFrom(ResultSetViewModel result)
        => Model.Anchor is not null && ReferenceEquals(Model.Result, result);

    /// <summary>During a drag, hit-test the cell under the pointer and select the rectangle from the
    /// drag anchor to it. A pointer over grid chrome (a header, the scrollbar) leaves the selection alone.</summary>
    public void DragTo(DataGrid grid, ResultSetViewModel result, PointerEventArgs e)
    {
        if (!Model.Dragging || !ReferenceEquals(Model.Result, result) || Model.DragAnchor is not { } anchor) return;
        if (grid.InputHitTest(e.GetPosition(grid)) is not Visual hit) return;
        var cell = hit.GetSelfAndVisualAncestors().OfType<Border>()
            .FirstOrDefault(b => b.Tag is ValueTuple<object?[], int>);
        if (cell?.Tag is not ValueTuple<object?[], int> target) return;

        Model.Active = (target.Item1, target.Item2);
        SelectRectangle(result, anchor, (target.Item1, target.Item2));
    }

    /// <summary>Release the pointer capture that a drag took.</summary>
    public void EndDrag(IPointer pointer)
    {
        Model.Dragging = false;
        pointer.Capture(null);
    }

    /// <summary>Replace the selection with the rectangle spanning cells a..b (inclusive).</summary>
    public void SelectRectangle(ResultSetViewModel result, (object?[] Row, int Col) a, (object?[] Row, int Col) b)
    {
        var cells = GridSelectionOps.Rectangle(result, a, b);
        if (cells.Count == 0) return; // an anchor row is gone — leave the selection as it was

        Model.Result = result;
        Model.Cells.Clear();
        foreach (var cell in cells) Model.Cells.Add(cell);
        Notify();
    }

    /// <summary>Row/column header click → select the whole row or column (#6). Shift extends the band from
    /// the current anchor (contiguous rows / columns), Ctrl adds it to the selection; a right-press selects
    /// the band too, unless it is already selected, so the context menu always acts on what is under the
    /// pointer. Returns what the press landed on, so the caller can clear on a click-away and report that a
    /// column selection covers the loaded rows only.
    /// <para>
    /// Registered <c>handledEventsToo</c> at grid level, so it also sees the presses the DataGrid and its
    /// headers already marked handled — which is why it hit-tests for a cell instead of reading
    /// <c>e.Handled</c> to decide whether a cell owns the press.
    /// </para></summary>
    public GridPressTarget TrySelectFromHeader(DataGrid grid, ResultSetViewModel result, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source) return GridPressTarget.None;

        DataGridRowHeader? rowHeader = null;
        DataGridColumnHeader? columnHeader = null;
        foreach (var visual in source.GetSelfAndVisualAncestors())
        {
            if (visual is Border { Tag: ValueTuple<object?[], int> }) return GridPressTarget.Cell;
            if (visual is DataGridRowHeader rh) { rowHeader = rh; break; }
            if (visual is DataGridColumnHeader ch) { columnHeader = ch; break; }
        }

        var point = e.GetCurrentPoint(grid).Properties;
        if (!point.IsLeftButtonPressed && !point.IsRightButtonPressed) return GridPressTarget.None;
        var extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift) && CanExtendFrom(result);
        var add = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (rowHeader is not null)
        {
            if (RowOf(rowHeader) is not { } row) return GridPressTarget.None;
            var from = extend ? Model.Anchor!.Value.Row : row;
            var origin = (row, extend ? Model.Anchor!.Value.Col : GridSelectionOps.FirstColumn(result));
            return SelectBand(grid, result, GridSelectionOps.WholeRows(result, from, row), origin,
                keepAnchor: extend, add, point.IsRightButtonPressed)
                ? GridPressTarget.RowHeader
                : GridPressTarget.None;
        }

        if (columnHeader is not null)
        {
            if (ColumnIndexOf(grid, columnHeader) is not { } col || result.Rows.Count == 0) return GridPressTarget.None;
            var from = extend ? Model.Anchor!.Value.Col : col;
            var origin = (extend ? Model.Anchor!.Value.Row : result.Rows[0], col);
            return SelectBand(grid, result, GridSelectionOps.WholeColumns(result, from, col), origin,
                keepAnchor: extend, add, point.IsRightButtonPressed)
                ? GridPressTarget.ColumnHeader
                : GridPressTarget.None;
        }

        return GridPressTarget.None;
    }

    /// <summary>Select a whole-row / whole-column band. Returns false when there was nothing to select, or
    /// when a right-press landed inside a band that is already selected (which must not shrink it).</summary>
    private bool SelectBand(
        DataGrid grid, ResultSetViewModel result, IReadOnlyList<(object?[] Row, int Col)> cells,
        (object?[] Row, int Col) origin, bool keepAnchor, bool add, bool rightButton)
    {
        if (cells.Count == 0) return false;
        var alreadySelected = ReferenceEquals(Model.Result, result) && cells.All(Model.Cells.Contains);
        if (rightButton && alreadySelected) return true; // leave the block alone, just let the menu open

        grid.Focus(); // route the following keystrokes to this grid, as a cell click does
        if (!add || !ReferenceEquals(Model.Result, result)) Model.Cells.Clear();
        Model.Result = result;
        foreach (var cell in cells) Model.Cells.Add(cell);
        Model.Active = origin;
        if (!keepAnchor) Model.Anchor = origin;
        Notify();
        return true;
    }

    /// <summary>The row array behind a row header (its owning <see cref="DataGridRow"/>'s item).</summary>
    private static object?[]? RowOf(DataGridRowHeader header)
        => header.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault()?.DataContext as object?[];

    /// <summary>A column header's result-column index. Headers are the Control instances the cell factory
    /// built per column, so they match by reference — and the Columns collection keeps its build order even
    /// after the user drags columns around (reordering moves DisplayIndex, not the collection).</summary>
    private static int? ColumnIndexOf(DataGrid grid, DataGridColumnHeader header)
    {
        for (var i = 0; i < grid.Columns.Count; i++)
            if (ReferenceEquals(grid.Columns[i].Header, header.Content)) return i;
        return null; // the top-left corner header owns no column
    }

    // ---- keyboard ---------------------------------------------------------------------------

    private static GridMotion? MotionOf(Key k) => k switch
    {
        Key.Left => GridMotion.Left,
        Key.Right => GridMotion.Right,
        Key.Up => GridMotion.Up,
        Key.Down => GridMotion.Down,
        Key.Home => GridMotion.Home,
        Key.End => GridMotion.End,
        Key.PageUp => GridMotion.PageUp,
        Key.PageDown => GridMotion.PageDown,
        _ => null,
    };

    /// <summary>Spatial cell-cursor motion: arrows / Home / End / PageUp / PageDown move the active cell,
    /// Shift extends a rectangle from the anchor, Ctrl jumps to the row or column edge. Returns false for
    /// any other key so it falls through to the shared command dispatcher or bubbles to the window.
    /// <para>
    /// Intrinsic navigation, deliberately NOT rebindable commands — the same call the editor's caret motion
    /// gets (§9.2's stated exception).
    /// </para>
    /// </summary>
    public bool HandleNavigation(DataGrid grid, ResultSetViewModel result, KeyEventArgs e)
    {
        if (MotionOf(e.Key) is not { } motion) return false;

        // First arrow into a grid that isn't the active one: seed the active cell at the top-left instead
        // of moving a cursor that isn't visible yet.
        if (!ReferenceEquals(Model.Result, result) || Model.Active is not { } active)
        {
            SeedActive(grid, result);
            return true;
        }

        var r = result.Rows.IndexOf(active.Row);
        if (r < 0) return false; // the active row was dropped (discarded new row) — let the key bubble

        var toEdge = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var page = Math.Max(1, VisiblePageSize(grid) - 1);
        var (nr, nc) = GridSelectionOps.Move(result, r, active.Col, motion, toEdge, page);

        MoveActive(grid, result, result.Rows[nr], nc, extend: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        return true;
    }

    /// <summary>Put the cursor on the top-left selectable cell (a grid taking focus with nothing active, or
    /// the first arrow key into it). No-op for an empty result.</summary>
    public void SeedActive(DataGrid grid, ResultSetViewModel result)
    {
        if (result.Rows.Count == 0) return;
        MoveActive(grid, result, result.Rows[0], GridSelectionOps.FirstColumn(result), extend: false);
    }

    /// <summary>Whether this grid still needs a seeded cursor (nothing active, or the cursor is elsewhere).</summary>
    public bool NeedsSeed(ResultSetViewModel result)
        => Model.Active is null || !ReferenceEquals(Model.Result, result);

    /// <summary>Move the active cell to (row, col); Shift extends the rectangle from the anchor, otherwise
    /// the selection collapses to the single cell and re-seeds the anchor. Scrolls the target into view.</summary>
    public void MoveActive(DataGrid grid, ResultSetViewModel result, object?[] row, int col, bool extend)
    {
        Model.Active = (row, col);
        Model.Result = result;
        if (extend)
        {
            Model.Anchor ??= Model.Active;
            SelectRectangle(result, Model.Anchor.Value, Model.Active.Value);
        }
        else
        {
            Model.Anchor = Model.Active;
            Model.Cells.Clear();
            if (col < result.Columns.Count) Model.Cells.Add((row, col));
            Notify();
        }
        if (col < grid.Columns.Count) grid.ScrollIntoView(row, grid.Columns[col]);
    }

    /// <summary>Approximate rows-per-page from the realized DataGridRow visuals (for PageUp/PageDown).</summary>
    private static int VisiblePageSize(DataGrid grid)
    {
        var realized = grid.GetVisualDescendants().OfType<DataGridRow>().Count(dgr => dgr.IsVisible);
        return realized > 0 ? realized : 12;
    }

    // ---- selection-driven actions -----------------------------------------------------------

    /// <summary>grid.selectAll (Ctrl+A): select every selectable cell of the result.</summary>
    public void SelectAll(ResultSetViewModel result)
    {
        Model.Result = result;
        Model.Cells.Clear();
        foreach (var cell in GridSelectionOps.AllCells(result)) Model.Cells.Add(cell);
        if (result.Rows.Count > 0)
        {
            Model.Active ??= (result.Rows[0], GridSelectionOps.FirstColumn(result));
            Model.Anchor ??= Model.Active;
        }
        Notify();
    }

    /// <summary>grid.copy (Ctrl+C): put the selection on the clipboard as tab-separated rows.</summary>
    public void Copy(ResultSetViewModel result)
    {
        if (!ReferenceEquals(Model.Result, result) || Model.Cells.Count == 0) return;
        TopLevel.GetTopLevel(_owner)?.Clipboard?.SetTextAsync(GridSelectionOps.Tsv(result, Model.Cells));
    }

    /// <summary>Copy as ▸ <paramref name="format"/>: the same selection rendered as CSV / Markdown / JSON /
    /// a rich table / SQL / an IN list instead of TSV. The tabular ones carry a header row (unlike plain
    /// Copy), since a bare grid of values is of little use in those formats.
    /// <para>
    /// The table format is the one that isn't just text: it goes on the clipboard as the platform's HTML
    /// flavour (with TSV as the plain-text alternative) so Teams and Word paste a table instead of markup.
    /// </para>
    /// </summary>
    public void CopyAs(ResultSetViewModel result, CopyFormat format)
    {
        if (!ReferenceEquals(Model.Result, result) || Model.Cells.Count == 0) return;
        if (format == CopyFormat.Tsv) { Copy(result); return; }

        var block = TableBlock.ForSelection(result, Model.Cells);
        var text = CopyRenderer.Render(result, block, format);
        if (format == CopyFormat.Html)
        {
            CrashReporter.Observe(
                HtmlClipboard.SetAsync(TopLevel.GetTopLevel(_owner), text, GridSelectionOps.Tsv(result, Model.Cells)),
                "grid.copyAs.html");
            return;
        }
        TopLevel.GetTopLevel(_owner)?.Clipboard?.SetTextAsync(text);
    }

    /// <summary>Whether a paste has an editable target and somewhere to land (guards grid.paste and its
    /// menu item). A paste anchors on the active cell, so a grid with focus but no cursor is not a target.</summary>
    public bool CanPasteInto(ResultSetViewModel result)
        => result.IsEditable && ReferenceEquals(Model.Result, result) && Model.Active is not null;

    /// <summary>grid.paste (Ctrl+V / Shift+Insert): write the clipboard's cells into the grid — a single value
    /// fills the selection, a TSV block anchors at the cursor and fills right/down (see <see cref="GridPaste"/>
    /// for the shape rules). Returns what to tell the user, or null when nothing was attempted.
    /// <para>
    /// Every write goes through <see cref="ResultSetViewModel.SetCell"/>, the same call the in-cell editor
    /// makes, so paste inherits the (null) token, empty-means-NULL, and save-time coercion rather than growing
    /// a second value-parsing path. It only stages pending edits — the save confirmation is still the gate on
    /// anything reaching the server.
    /// </para>
    /// <para>
    /// The clipboard read is the only await here, and it is why the guard runs twice: the user can click into
    /// another cell, or a page load can replace the rows, while the platform is still answering.
    /// </para></summary>
    public async Task<string?> PasteAsync(DataGrid grid, ResultSetViewModel result)
    {
        if (!CanPasteInto(result)) return null;
        if (TopLevel.GetTopLevel(_owner)?.Clipboard is not { } clipboard) return null;

        var text = await clipboard.TryGetTextAsync();
        if (!CanPasteInto(result) || Model.Active is not { } active) return null;

        var block = GridPaste.Parse(text);
        if (block.Count == 0) return null;
        var writes = GridPaste.Plan(result, block, active, Model.Cells);
        var clipped = GridPaste.Clipped(result, block, active, Model.Cells);
        if (writes.Count == 0) return "Nothing pasted — the clipboard's block starts past the last row.";

        foreach (var (row, col, value) in writes) result.SetCell(row, col, value);
        ResultRowPainter.RefreshRowColors(grid, result);
        Notify(); // also re-reads the checkbox cells a paste wrote through

        var pasted = $"Pasted {writes.Count} cell{(writes.Count == 1 ? "" : "s")}.";
        return clipped == 0
            ? pasted
            : $"{pasted} {clipped} dropped — the block runs past the loaded rows/columns.";
    }

    /// <summary>Whether a copy action has anything to act on (guards the copy commands + menu items).</summary>
    public bool HasSelection(ResultSetViewModel result)
        => ReferenceEquals(Model.Result, result) && Model.Cells.Count > 0;

    /// <summary>grid.delete (Delete): mark every row that owns a selected cell for deletion. A pending-new
    /// row is dropped outright rather than marked, so prune any now-dangling selection entries afterwards.</summary>
    public void DeleteSelectedRows(DataGrid grid, ResultSetViewModel result)
    {
        if (!result.IsEditable || !ReferenceEquals(Model.Result, result)) return;
        foreach (var row in Model.Cells.Select(s => s.Row).Distinct().ToList())
            if (!result.IsRowDeleted(row)) result.ToggleDelete(row); // mark (never un-mark) for deletion
        Model.Cells.RemoveWhere(s => !result.Rows.Contains(s.Row));
        if (Model.Active is { } a && !result.Rows.Contains(a.Row)) { Model.Active = null; Model.Anchor = null; }
        ResultRowPainter.RefreshRowColors(grid, result);
        Notify();
    }

    /// <summary>grid.setNull (Ctrl+Shift+N) and the context menu's Set NULL: write NULL over every selected
    /// cell. The discoverable path to a value that was otherwise reachable only by knowing to type the
    /// <c>(null)</c> token into a cell (#33) — and the only path at all on a checkbox column, which has no
    /// text editor to type it into.
    /// <para>
    /// Writes the token itself through <see cref="ResultSetViewModel.SetCell"/>, exactly as the editor and a
    /// paste do, so this inherits the one value path (save-time coercion in <c>ResultEditModel</c>) instead of
    /// growing a second way for a cell to mean NULL. On a pending-new row that also matters: the token is an
    /// explicit NULL in the INSERT, where a blank cell is left to the column's default.
    /// </para>
    /// Returns what to tell the user, or null when nothing was attempted.</summary>
    public string? SetNullSelected(DataGrid grid, ResultSetViewModel result)
    {
        if (!result.IsEditable || !ReferenceEquals(Model.Result, result) || Model.Cells.Count == 0) return null;

        var plan = GridSelectionOps.PlanSetNull(result, Model.Cells);
        if (plan.Targets.Count == 0)
            return plan.NotNullable > 0
                ? $"Nothing set to NULL — {Refused(plan.NotNullable)}."
                : "Nothing set to NULL — the selected cells are already NULL.";

        foreach (var (row, col) in plan.Targets) result.SetCell(row, col, CellFormat.NullToken);
        ResultRowPainter.RefreshRowColors(grid, result);
        Notify(); // also re-reads the checkbox cells this wrote through

        var set = $"Set {CellCount(plan.Targets.Count)} to NULL.";
        if (plan.NotNullable == 0) return set;
        var columns = plan.NotNullable == 1 ? "a NOT NULL column" : "NOT NULL columns";
        return $"{set} {plan.NotNullable} skipped — {columns}.";
    }

    private static string CellCount(int count) => count == 1 ? "1 cell" : $"{count} cells";

    private static string Refused(int count) => count == 1
        ? "1 cell is in a NOT NULL column"
        : $"{count} cells are in NOT NULL columns";

    /// <summary>grid.beginEdit (Enter/F2): start editing the active cell via the DataGrid's own machinery —
    /// except on a checkbox column, which has no text editor and cycles its value instead.</summary>
    public void BeginEditActive(DataGrid grid, ResultSetViewModel result)
    {
        if (Model.Active is not { } a || !ReferenceEquals(Model.Result, result)) return;
        if (result.Rows.IndexOf(a.Row) < 0 || a.Col >= grid.Columns.Count) return;
        grid.ScrollIntoView(a.Row, grid.Columns[a.Col]);
        if (a.Col < result.Columns.Count && ColumnKinds.IsBool(result.Columns[a.Col]))
        {
            ToggleBool(grid, result, a.Row, a.Col);
            return;
        }
        grid.SelectedItem = a.Row;
        grid.CurrentColumn = grid.Columns[a.Col];
        grid.BeginEdit();
    }

    /// <summary>A plain left-click that landed <i>on the checkbox</i> of a bool cell → cycle its value. The
    /// cell has already taken the selection by the time this runs (its own press handler did, and this is a
    /// grid-level handler); a click anywhere else in the cell, or in any other column, therefore just selects.
    /// <para>
    /// The hot zone is the indicator's own bounds, so the thing you aim at and the thing that responds are the
    /// same rectangle. Modifier-clicks are excluded: Ctrl and Shift are range/multi-select gestures, and
    /// writing values while building a selection would be indefensible.
    /// </para></summary>
    public bool TryToggleBoolAtPointer(DataGrid grid, ResultSetViewModel result, PointerPressedEventArgs e)
    {
        if (!result.IsEditable || e.ClickCount != 1) return false;
        if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return false;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return false;
        if (BoolCellUnder(result, e.Source as Visual) is not { } cell) return false;

        // The indicator is not hit-testable, so the press landed on the cell border either way; what decides
        // is whether it landed inside the box the user can see.
        if (cell.Border.Child is not { } indicator) return false;
        if (!new Rect(indicator.Bounds.Size).Contains(e.GetCurrentPoint(indicator).Position)) return false;

        ToggleBool(grid, result, cell.Row, cell.Col);
        return true;
    }

    /// <summary>Double-tap a bool cell → cycle its value, anywhere in the cell. The forgiving counterpart to
    /// the click-the-box gesture, and the same double-tap that opens the text editor on any other column.
    /// Reads its target from the cell under the pointer rather than from the selection, so it acts on what was
    /// actually tapped.</summary>
    public void ToggleBoolAt(DataGrid grid, ResultSetViewModel result, TappedEventArgs e)
    {
        if (!result.IsEditable) return;
        if (BoolCellUnder(result, e.Source as Visual) is { } cell) ToggleBool(grid, result, cell.Row, cell.Col);
    }

    /// <summary>The bool cell containing <paramref name="source"/> — its selection border plus the (row,
    /// column) off that border's tag. Null when the pointer wasn't over a cell, or the cell isn't a checkbox
    /// column.</summary>
    private static (Border Border, object?[] Row, int Col)? BoolCellUnder(ResultSetViewModel result, Visual? source)
    {
        if (source is null) return null;
        foreach (var visual in source.GetSelfAndVisualAncestors())
        {
            if (visual is not Border { Tag: ValueTuple<object?[], int> tag } border) continue;
            var col = tag.Item2;
            return col < result.Columns.Count && ColumnKinds.IsBool(result.Columns[col])
                ? (border, tag.Item1, col)
                : null;
        }
        return null;
    }

    /// <summary>Cycle a checkbox cell's value in place, skipping the NULL leg on a NOT NULL column. A bool
    /// column is a <c>DataGridTemplateColumn</c> with no editing template — which Avalonia treats as
    /// read-only, so BeginEdit has nothing to open there and every bool write lands here instead.
    /// <para>The cell re-renders on <see cref="Notify"/>, which is also how a paste into a checkbox column
    /// shows up.</para></summary>
    private void ToggleBool(DataGrid grid, ResultSetViewModel result, object?[] row, int col)
    {
        if (!result.IsEditable) return;
        result.SetCell(row, col, BoolCellValue.Next(BoolCellValue.Read(row, col), result.AllowsNull(col)));
        ResultRowPainter.RefreshRowColors(grid, result);
        Notify();
    }
}
