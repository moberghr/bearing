using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Media;
using Bearing.App.Input;
using Bearing.App.ViewModels;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Controls;

public sealed partial class ResultView
{
    /// <summary>Build a result set's body (grid + quick-stats bar) and hand back the grid so the caller can
    /// put the (subtle) edit controls on the meta row. Non-grid results return a null grid.</summary>
    private Control BuildResultSet(ResultSetViewModel result, out DataGrid? grid)
    {
        grid = null;
        // A result with no grid is one line of text, so it is styled as text rather than left at whatever
        // font size it inherits — it sat two sizes above the meta row naming it, unpadded against the frame.
        if (!result.Success)
            return ResultChrome.ResultText($"Error: {result.Error?.Message}", Res("Error.Red"), wrap: true);

        if (result.Columns.Count == 0)
            return ResultChrome.ResultText(result.Message ?? "Statement executed.", Res("Text.Code"));

        grid = BuildGrid(result);
        // Any cell is selectable; the stats bar surfaces itself only when ≥2 selected cells are numeric.
        // Row count + count-on-demand + edit controls all live on the meta row now (no footer).
        var stats = new QuickStatsBar(result, _selection);
        _statsBars.Add(stats);
        return stats.Wrap(grid);
    }

    private DataGrid BuildGrid(ResultSetViewModel result)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = !result.IsEditable,
            CanUserResizeColumns = true,
            CanUserReorderColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            HeadersVisibility = DataGridHeadersVisibility.All,      // row-number gutter + column headers
            Background = Res("Bg.Editor"),                          // flat body per design (#1A2027)
            HorizontalGridLinesBrush = ResultRowPainter.GridLine,   // subtle #232A33 row/column dividers
            VerticalGridLinesBrush = ResultRowPainter.GridLine,
        };
        _firstGrid ??= grid;          // first grid of this render → region-focus target
        ResultGridChrome.Apply(grid); // cell-level selection, tight rows, gutter, themed headers, insets

        grid.LoadingRow += (_, e) =>
        {
            e.Row.Header = (e.Row.Index + 1).ToString();
            // Design row striping; editable rows still tint on a pending edit/new/delete (handled inside).
            if (result.IsEditable) ResultRowPainter.ApplyRowStatus(e.Row, result);
            else e.Row.Background = ResultRowPainter.RowBackground(e.Row.Index);
            // Infinite scroll: when a near-bottom row realizes and more rows exist, fetch the next page.
            if (result.HasMore && e.Row.Index >= result.Rows.Count - 8) TriggerAutoLoad(result);
        };

        for (var i = 0; i < result.Columns.Count; i++)
            grid.Columns.Add(_cells.BuildColumn(result, i, grid));
        grid.ItemsSource = result.Rows; // ObservableCollection → paged rows append without a rebuild

        // Double-tap: a column header (incl. its resize gripper) auto-fits that column; a checkbox cell
        // cycles its value, which is how a bool is edited with the mouse now that a plain click only selects.
        grid.DoubleTapped += (_, e) =>
        {
            ResultGridChrome.AutoFitColumn(grid, e);
            _selection.ToggleBoolAt(grid, result, e);
        };

        // Right-click menu: copy (in any format), paste, set NULL, fetch the rest, export. The cells collapse
        // the selection onto themselves on a right-click outside it (see ResultCellFactory), so what the menu
        // acts on is always what the user is pointing at.
        grid.ContextFlyout = ResultContextMenu.Build(
            result,
            hasSelection: () => _selection.HasSelection(result),
            copy: () => _selection.Copy(result),
            copyAs: format => _selection.CopyAs(result, format),
            paste: result.IsEditable ? () => PasteInto(grid, result) : null,
            canPaste: () => _selection.CanPasteInto(result),
            setNull: result.IsEditable ? () => SetNullIn(grid, result) : null,
            fetchAll: result.IsPageable ? () => FetchAll?.Invoke(result) ?? Task.CompletedTask : null,
            export: format => Export?.Invoke(result, format) ?? Task.CompletedTask);

        // #118: the header's own menu, resolved before the grid's cell flyout gets the chance. Handled in
        // the tunnel phase because ContextFlyout is driven by ContextRequested, which the grid would
        // otherwise answer with the cell menu for a press that landed on a header.
        grid.AddHandler(ContextRequestedEvent, (_, e) => OnGridContextRequested(grid, result, e),
            RoutingStrategies.Tunnel);

        // Hidden / frozen columns are applied here and re-applied whenever the layout changes, in place: the
        // alternative is rebuilding the grid, which would drop the scroll position and every expanded
        // inspector for what is meant to be a change of view.
        //
        // The handler is *tracked* so Rebuild can detach it. The layout lives as long as its
        // ResultSetViewModel while a grid lives only until the next render — and RebuildResults runs on
        // every tab switch over the same result sets, so an untracked subscription left one dead closure per
        // switch, each pinning a discarded DataGrid and its whole visual tree, and each re-applying the
        // layout to a grid nobody can see.
        ApplyColumnLayout(grid, result);
        void OnLayoutChanged() => ApplyColumnLayout(grid, result);
        result.ColumnLayout.Changed += OnLayoutChanged;
        _layoutSubscriptions.Add((result.ColumnLayout, OnLayoutChanged));

        WireSelection(grid, result);
        if (result.IsEditable) WireEditing(grid, result);
        return grid;
    }

    /// <summary>
    /// Push the result's column layout (#118) onto the grid, and drop any now-hidden cells from the
    /// selection.
    /// <para>
    /// The prune is the part that matters. <c>GridSelectionOps</c> keeps a hidden column out of a
    /// <em>new</em> selection, but a selection made <b>before</b> the hide still held those cells — so
    /// Ctrl+A, hide a column, Copy put the column the user had just hidden on the clipboard, and Set NULL
    /// would have staged an UPDATE against a cell with no visual. Pruning here keeps "a selection copy is
    /// what you see" true after the fact as well as by construction.
    /// </para>
    /// </summary>
    private void ApplyColumnLayout(DataGrid grid, ResultSetViewModel result)
    {
        var layout = result.ColumnLayout;
        for (var i = 0; i < grid.Columns.Count && i < result.Columns.Count; i++)
            grid.Columns[i].IsVisible = !layout.IsHidden(i);
        grid.FrozenColumnCount = layout.FrozenCount;
        _selection.DropHiddenColumns(result);
    }

    /// <summary>
    /// A right-click on a column header opens the column menu instead of the cell menu (#118). Anything
    /// else — a cell, the gutter, the corner, empty space — is left to the grid's own flyout.
    /// </summary>
    private void OnGridContextRequested(DataGrid grid, ResultSetViewModel result, ContextRequestedEventArgs e)
    {
        if (e.Source is not Visual source) return;

        DataGridColumnHeader? header = null;
        foreach (var visual in source.GetSelfAndVisualAncestors())
        {
            // A cell settles it: the cell menu is the right one, and cells sit under the same grid.
            if (visual is Border { Tag: ValueTuple<object?[], int> }) return;
            if (visual is DataGridColumnHeader candidate) { header = candidate; break; }
        }
        if (header is null) return;
        if (GridSelectionController.ColumnIndexOfHeader(grid, header) is not { } index) return;  // the corner

        // A count of leading *visible* columns in display order — see ResultColumnMenu.FreezeThrough,
        // which owns the arithmetic because it is the arithmetic behind its own menu item.
        var through = ResultColumnMenu.FreezeThrough(grid, index);
        ResultColumnMenu.Build(result, index, through).ShowAt(header);
        e.Handled = true;
    }

    /// <summary>Hook the grid up to the selection controller. Cells drive their own selection (per-cell
    /// PointerPressed in <see cref="ResultCellFactory"/>); the grid extends a drag, selects whole rows and
    /// columns from the headers, and clears the selection when a click missed all of that.
    /// <c>handledEventsToo: true</c> is required because the DataGrid marks these pointer events handled in
    /// the tunnel phase.</summary>
    private void WireSelection(DataGrid grid, ResultSetViewModel result)
    {
        grid.AddHandler(PointerMovedEvent, (_, e) => _selection.DragTo(grid, result, e),
            RoutingStrategies.Bubble, handledEventsToo: true);
        grid.AddHandler(PointerReleasedEvent, (_, e) => { if (_selection.Model.Dragging) _selection.EndDrag(e.Pointer); },
            RoutingStrategies.Bubble, handledEventsToo: true);
        grid.AddHandler(PointerPressedEvent, (_, e) => OnGridPressed(grid, result, e),
            RoutingStrategies.Bubble, handledEventsToo: true);

        // Keyboard-drive the grid. Handled in the tunnel phase so we pre-empt the DataGrid's own
        // arrow-nav / Ctrl+C before it acts (setting Handled skips its class-level OnKeyDown).
        grid.Focusable = true;
        grid.AddHandler(KeyDownEvent, (_, e) => OnGridKey(grid, result, e), RoutingStrategies.Tunnel);

        // …and the *release* of a Tab has to be claimed too. Avalonia's `DataGrid_KeyUp` answers a Tab keyup
        // with `ScrollSlotIntoView(…, forceHorizontalScroll: true)` aimed at the DataGrid's **own** current
        // cell — which is not our cursor (we own cell selection, §9.2, and only a click and BeginEdit ever
        // set the grid's), so it is wherever the last click left it. Tabbing therefore scrolled twice per
        // keystroke and disagreed with itself: measured 304 → 153 on the key down, then → 56 on the release.
        // We claim Tab on the way down, so claiming its release is the same decision, not a new one.
        // A cell editor's own Tab handling is left alone — it is a TextBox, and Tab there commits and moves.
        grid.AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key == Key.Tab && e.Source is not TextBox) e.Handled = true;
        }, RoutingStrategies.Tunnel);
        _gridsByResult[result] = grid; // resolve the grid for a palette-invoked grid command (see GridTarget)

        // When the grid takes focus (e.g. via F6) with no active cell yet, seed the top-left cell so the
        // focus is visible instead of the caller having to press an arrow first.
        grid.GotFocus += (_, _) => { if (_selection.NeedsSeed(result)) _selection.SeedActive(grid, result); };
    }

    /// <summary>A press the cells didn't take: the row-number gutter selects the whole row, a column header
    /// its whole column (#6), and the corner above the gutter the whole result (#55); anything else — the
    /// scrollbar, empty space below the last row — clears the selection, which is the click-away this
    /// handler replaced.
    /// <para>
    /// A cell press has already selected itself by now (this runs after the cell's own handler, being further
    /// up the tree), so the one thing left to do for it is the checkbox gesture: a click that landed on a bool
    /// cell's box also cycles the value.
    /// </para>
    /// <para>
    /// A column selection covers the <i>loaded</i> rows, so on a part-fetched result it says so rather than
    /// letting a short Copy as ▸ IN list look complete.
    /// </para></summary>
    private void OnGridPressed(DataGrid grid, ResultSetViewModel result, PointerPressedEventArgs e)
    {
        switch (_selection.TrySelectFromHeader(grid, result, e))
        {
            case GridPressTarget.Cell:
                _selection.TryToggleBoolAtPointer(grid, result, e);
                return;
            case GridPressTarget.RowHeader:
            case GridPressTarget.Corner:
                return;
            case GridPressTarget.ColumnHeader:
                if (result is { IsPageable: true, HasMore: true })
                    Status?.Invoke($"Column selected over the loaded rows only ({result.RowCountText}) — ⤓ all fetches the rest.");
                return;
            default:
                if (_selection.Model.Cells.Count > 0) _selection.ClearAndNotify();
                return;
        }
    }

    /// <summary>Capture a committed cell edit back onto the result set and tint the row immediately.</summary>
    private void WireEditing(DataGrid grid, ResultSetViewModel result)
    {
        _editableGrids.Add((grid, result));
        grid.CellEditEnding += (_, e) =>
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            if (e.Row.DataContext is not object?[] row || e.Column.Tag is not int idx) return;
            if (e.EditingElement is TextBox tb) result.SetCell(row, idx, tb.Text);
            ResultRowPainter.ApplyRowStatus(e.Row, result); // tint + status bar on the edited row immediately

            // There is deliberately no ScrollIntoView here any more. It was the twin of the click path's
            // KeepClickedCellInView and went the same way (§9.10a): the causes it was written for — the grid
            // re-adopting a current cell, the row re-tinting, the cell's content swapping — do not move the
            // viewport, and what it still did was reveal a cell clipped by the viewport edge. So committing
            // an edit in the sliver of a half-visible column slid the whole result sideways, measured at
            // 304 → 250: the same jump #60 reports, from a commit instead of a click.
        };
    }

    /// <summary>Keyboard-drive a result grid. Discrete grid commands (copy, select-all, delete, begin-edit,
    /// clear) go through the shared dispatcher; the grid+result they act on is published on
    /// <see cref="_keyStrokeTarget"/> for the duration of the dispatch only (grid commands are synchronous, so
    /// they read it inside <c>TryHandle</c>). A command whose guard is false (Delete on a read-only set,
    /// Escape with no selection) leaves the key unhandled so it falls through to spatial navigation below, or
    /// bubbles to the window.</summary>
    private void OnGridKey(DataGrid grid, ResultSetViewModel result, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;                 // a cell editor is focused — let it have the keys
        if (!result.HasGrid || result.Rows.Count == 0) return;

        _keyStrokeTarget = (grid, result);
        bool handled;
        try { handled = _dispatcher?.TryHandle(e, KeyScope.Grid) == true; }
        finally { _keyStrokeTarget = null; }
        if (handled) return;

        // Everything below is spatial cell-cursor motion — intrinsic grid navigation, not a rebindable
        // command (mirrors how the editor's caret motion isn't in the keymap).
        if (_selection.HandleNavigation(grid, result, e)) e.Handled = true;
    }
}
