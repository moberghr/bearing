using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Bearing.App.Results;
using Bearing.App.Services;
using Bearing.App.ViewModels;
using Avalonia.Input.Platform;
using Avalonia;

namespace Bearing.App.Controls;

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

    /// <summary>Cycle a checkbox cell's value in place (the keyboard's equivalent of clicking it). A bool
    /// column is a <c>DataGridTemplateColumn</c> with no editing template, so BeginEdit has nothing to open;
    /// leaving Enter dead on a cell the cursor can now land on (#9) would be the worse answer.
    /// <para>The realized CheckBox re-reads the row on <see cref="Notify"/>, which is also how a paste into a
    /// checkbox column shows up.</para></summary>
    private void ToggleBool(DataGrid grid, ResultSetViewModel result, object?[] row, int col)
    {
        if (!result.IsEditable) return;
        result.SetCell(row, col, BoolCellValue.Next(BoolCellValue.Read(row, col)));
        ResultRowPainter.RefreshRowColors(grid, result);
        Notify();
    }
}
