using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Bearing.App.Input;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;

namespace Bearing.App.Controls;

/// <summary>
/// Renders a query run's result sets inside the results dock (design RESULTS_GRID.md). Structure:
/// a persistent dock header (RESULTS label + Stacked/Tabbed toggle), then the body — result sets
/// stacked vertically (default) or as tabs. Each set has a meta row (Result · N rows · ms), a grid,
/// an optional edit toolbar (editable sets) and a quick-stats bar. Self-contained: assign
/// <see cref="Results"/> and it rebuilds.
/// <para>
/// This class is the composition root for the dock and little else. The pieces it assembles own their own
/// behavior: <see cref="GridSelectionController"/> (cell selection + keyboard cursor),
/// <see cref="ResultCellFactory"/> (columns and cells), <see cref="InspectorPane"/> +
/// <see cref="CellInspectorView"/> (the value inspector), <see cref="QuickStatsBar"/>,
/// <see cref="ResultEditToolbar"/>, <see cref="ResultGridChrome"/>, <see cref="ResultRowPainter"/> and
/// <see cref="ResultChrome"/>.
/// </para>
/// </summary>
public sealed partial class ResultView : UserControl
{
    private readonly GridSelectionController _selection;
    private readonly ResultCellFactory _cells;
    private readonly InspectorPane _inspector = new();

    public ResultView()
    {
        _selection = new GridSelectionController(this);
        _selection.Changed += SyncStatsBars;
        // The factory can't resolve these itself: the inspector is this view's pane, and the FK navigation
        // callback is assigned by the shell after construction (so it must be read at click time, not now).
        _cells = new ResultCellFactory(
            _selection,
            inspect: ShowInspector,
            followForeignKey: (rs, col, row) => _ = NavigateForeignKey?.Invoke(rs, col, row));
    }

    /// <summary>The cell selection this dock drives. Exposed for tests to assert against — the app itself
    /// reaches it through the pieces that were handed the controller.</summary>
    internal GridSelectionController Selection => _selection;

    private IReadOnlyList<ResultSetViewModel>? _results;

    /// <summary>The result sets to display. Assigning replaces the rendered content.</summary>
    public IReadOnlyList<ResultSetViewModel>? Results
    {
        get => _results;
        // A new run resets the inspector + selection (and with it the stats bars).
        set { _results = value; CloseInspector(); _selection.Clear(); Rebuild(); }
    }

    private ResultsViewMode _viewMode = ResultsViewMode.Stacked;

    /// <summary>Stacked vs Tabbed presentation of multiple result sets. Set by the shell from the VM.</summary>
    public ResultsViewMode ViewMode
    {
        get => _viewMode;
        set { if (_viewMode == value) return; _viewMode = value; Rebuild(); }
    }

    /// <summary>Raised when the user flips the Stacked/Tabbed toggle (persist it on the VM).</summary>
    public Action<ResultsViewMode>? ViewModeChanged { get; set; }

    /// <summary>
    /// Re-render at the current type scale (#52). The grid's visuals are built in code and read their sizes
    /// once, so unlike a <c>{DynamicResource}</c> in XAML they do not follow a token change on their own — and
    /// a grid font setting that waits for the next query would look broken while you were dragging it.
    /// <para>
    /// Selection and the inspector survive deliberately: this is the same grid at a different size, not a new
    /// result, so throwing away what the user had selected would be a surprise.
    /// </para>
    /// </summary>
    public void RefreshTypeScale()
    {
        if (_results is not null) Rebuild();
    }

    /// <summary>Invoked when the user requests the next page of a pageable result set.</summary>
    public Func<ResultSetViewModel, Task>? LoadMore { get; set; }

    /// <summary>Invoked when the user requests the total count of a pageable result set.</summary>
    public Func<ResultSetViewModel, Task>? CountTotal { get; set; }

    /// <summary>Invoked when the user asks for every remaining page of a pageable result set at once.</summary>
    public Func<ResultSetViewModel, Task>? FetchAll { get; set; }

    /// <summary>Invoked to export a result set to a file (the format is picked from the Export menu).</summary>
    public Func<ResultSetViewModel, ExportFormat, Task>? Export { get; set; }

    /// <summary>Invoked when a foreign-key cell is clicked: (result set, column index, row values).</summary>
    public Func<ResultSetViewModel, int, object?[], Task>? NavigateForeignKey { get; set; }

    /// <summary>Whether the back bar is shown (FK navigation has a previous result to return to).</summary>
    public bool CanGoBack { get; set; }

    /// <summary>Invoked when the back bar's button is clicked.</summary>
    public Action? GoBack { get; set; }

    /// <summary>Invoked to commit a result set's pending edits (the [Save changes] button).</summary>
    public Func<ResultSetViewModel, Task>? SaveChanges { get; set; }

    /// <summary>Invoked to discard a result set's pending edits (the [Discard] button).</summary>
    public Func<ResultSetViewModel, Task>? DiscardChanges { get; set; }

    /// <summary>Report a one-line outcome to the shell's status bar (what a paste wrote, what it dropped).</summary>
    public Action<string>? Status { get; set; }

    /// <summary>The shared keybinding pipeline (set once by the window), used to resolve a keystroke that
    /// lands in a grid. Spatial cell navigation stays local (see <see cref="OnGridKey"/>).
    /// <para>
    /// Registration is <see cref="RegisterGridCommands"/>, deliberately not a side effect of this setter: the
    /// window has to register every command <i>before</i> it loads <c>keybindings.json</c>, because that load
    /// rejects bindings for ids it doesn't know — and the dispatcher can't exist until the keymap does.
    /// </para>
    /// </summary>
    public KeyDispatcher? CommandDispatcher
    {
        get => _dispatcher;
        set => _dispatcher = value;
    }
    private KeyDispatcher? _dispatcher;

    // The grid+result the in-flight grid keystroke is dispatching into — published for the duration of a
    // single OnGridKey dispatch only (cleared in its finally), so it can never be read stale.
    private (DataGrid Grid, ResultSetViewModel Result)? _keyStrokeTarget;

    /// <summary>The grid to hand keyboard focus to (region cycling); null when no results are shown.</summary>
    public Control? FocusableGrid => _firstGrid;
    private DataGrid? _firstGrid;

    /// <summary>The grid + result a grid command should act on: the grid the current keystroke is
    /// dispatching into, or — when a command runs without a keystroke (the command palette) — the grid that
    /// owns the current cell selection. Null when neither applies (nothing is selected and no key is in
    /// flight), leaving the command a no-op / its guard false.</summary>
    private (DataGrid Grid, ResultSetViewModel Result)? GridTarget()
    {
        if (_keyStrokeTarget is { } t) return t;
        if (_selection.Model.Result is { } r && _gridsByResult.TryGetValue(r, out var g)) return (g, r);
        return null;
    }

    /// <summary>Register the grid's discrete commands into the shared registry, so the same matcher (and the
    /// command palette) drives them. Called by the window before the keymap is loaded; safe to call twice.</summary>
    public void RegisterGridCommands(CommandRegistry r)
    {
        r.Register(KeyCommand.Sync(CommandIds.GridCopy, "Copy", KeyScope.Grid, "Grid",
            () => { if (GridTarget() is { } t) _selection.Copy(t.Result); }));
        // Copy as ▸ — one command per format so each is bindable and palette-reachable on its own. They ship
        // unbound (the gesture space is crowded and TSV owns Ctrl+C); the context menu is the discoverable path.
        foreach (var format in CopyRenderer.Alternatives)
        {
            var captured = format;
            r.Register(KeyCommand.Sync(CommandIds.GridCopyAs(captured), $"Copy as {CopyRenderer.Label(captured)}",
                KeyScope.Grid, "Grid",
                () => { if (GridTarget() is { } t) _selection.CopyAs(t.Result, captured); },
                canRun: () => GridTarget() is { } t && _selection.HasSelection(t.Result)));
        }
        // Paste is the one grid command with an await in it (the clipboard read), so it captures its target
        // before yielding — _keyStrokeTarget is cleared the moment TryHandle returns (see OnGridKey).
        r.Register(new KeyCommand(CommandIds.GridPaste, "Paste", KeyScope.Grid, "Grid",
            async () => { if (GridTarget() is { } t) await PasteInto(t.Grid, t.Result); },
            canRun: () => GridTarget() is { } t && _selection.CanPasteInto(t.Result)));
        r.Register(new KeyCommand(CommandIds.GridFetchAll, "Fetch all rows", KeyScope.Grid, "Grid",
            async () => { if (GridTarget() is { } t && FetchAll is { } f) await f(t.Result); },
            canRun: () => GridTarget()?.Result is { IsPageable: true, HasMore: true }));
        foreach (var format in new[] { ExportFormat.Csv, ExportFormat.Xlsx })
        {
            var captured = format;
            r.Register(new KeyCommand(CommandIds.GridExport(captured), $"Export result to {ResultExport.Label(captured)}",
                KeyScope.Grid, "Grid",
                async () => { if (GridTarget() is { } t && Export is { } f) await f(t.Result, captured); },
                canRun: () => GridTarget()?.Result.HasGrid == true));
        }
        r.Register(KeyCommand.Sync(CommandIds.GridSelectAll, "Select all", KeyScope.Grid, "Grid",
            () => { if (GridTarget() is { } t) _selection.SelectAll(t.Result); }));
        r.Register(KeyCommand.Sync(CommandIds.GridDelete, "Delete rows", KeyScope.Grid, "Grid",
            () => { if (GridTarget() is { } t) _selection.DeleteSelectedRows(t.Grid, t.Result); },
            canRun: () => GridTarget()?.Result.IsEditable == true));
        r.Register(KeyCommand.Sync(CommandIds.GridSetNull, "Set NULL", KeyScope.Grid, "Grid",
            () => { if (GridTarget() is { } t) SetNullIn(t.Grid, t.Result); },
            canRun: () => GridTarget() is { } t && t.Result.IsEditable && _selection.HasSelection(t.Result)));
        r.Register(KeyCommand.Sync(CommandIds.GridBeginEdit, "Edit cell", KeyScope.Grid, "Grid",
            () => { if (GridTarget() is { } t) _selection.BeginEditActive(t.Grid, t.Result); },
            canRun: () => GridTarget()?.Result.IsEditable == true));
        r.Register(KeyCommand.Sync(CommandIds.GridAddRow, "Add row", KeyScope.Grid, "Grid",
            () => { if (GridTarget() is { } t) AddRowTo(t.Grid, t.Result); },
            canRun: () => GridTarget()?.Result.IsEditable == true));
        // Save/Discard are guarded on pending changes as well as editability, which is what lets Ctrl+S fall
        // through to file.save on a grid with nothing of its own to commit.
        r.Register(new KeyCommand(CommandIds.GridSave, "Save changes", KeyScope.Grid, "Grid",
            async () => { if (GridTarget() is { } t && SaveChanges is { } f) await f(t.Result); },
            canRun: () => HasPendingEdits()));
        r.Register(new KeyCommand(CommandIds.GridDiscard, "Discard changes", KeyScope.Grid, "Grid",
            async () => { if (GridTarget() is { } t && DiscardChanges is { } f) await f(t.Result); },
            canRun: () => HasPendingEdits()));
        r.Register(KeyCommand.Sync(CommandIds.GridClearSelection, "Clear selection", KeyScope.Grid, "Grid",
            () => _selection.ClearAndNotify(),
            canRun: () => _selection.Model.Cells.Count > 0));
        r.Register(KeyCommand.Sync(CommandIds.GridInspect, "Inspect value", KeyScope.Grid, "Grid",
            ToggleInspectActiveCell, canRun: () => InspectableActiveCell() is not null));
        r.Register(KeyCommand.Sync(CommandIds.GridFollowFk, "Follow foreign key", KeyScope.Grid, "Grid",
            FollowActiveFk, canRun: _selection.ActiveCellIsFk));
        r.Register(KeyCommand.Sync(CommandIds.GridBack, "Back (foreign-key navigation)", KeyScope.Grid, "Grid",
            () => GoBack?.Invoke(), canRun: () => CanGoBack));
    }

    /// <summary>Whether the grid a command would act on has unsaved row edits (guards grid.save/grid.discard,
    /// and the toolbar's own buttons bind to the same <see cref="ResultSetViewModel.HasPendingChanges"/>).</summary>
    private bool HasPendingEdits()
        => GridTarget()?.Result is { IsEditable: true, HasPendingChanges: true };

    /// <summary>grid.addRow (Alt+Insert) and the toolbar's ＋ Add: append a pending-INSERT row, scroll to it
    /// and put the cursor on its first cell, so it can be filled in without reaching for the mouse.</summary>
    private void AddRowTo(DataGrid grid, ResultSetViewModel result)
    {
        if (!result.IsEditable) return;
        var row = result.AddRow();
        ResultRowPainter.RefreshRowColors(grid, result);
        _selection.MoveActive(grid, result, row, GridSelectionOps.FirstColumn(result), extend: false);
    }

    /// <summary>grid.setNull and the context menu's Set NULL: NULL the selected cells and report what it did
    /// (a selection spanning a NOT NULL column is written in part, and has to say so).</summary>
    private void SetNullIn(DataGrid grid, ResultSetViewModel result)
    {
        if (_selection.SetNullSelected(grid, result) is { } report) Status?.Invoke(report);
    }

    /// <summary>Paste the clipboard into a grid and report the outcome — including how many cells were
    /// dropped, so a paste clipped by the loaded row count can't pass for a complete one.</summary>
    private async Task PasteInto(DataGrid grid, ResultSetViewModel result)
    {
        if (await _selection.PasteAsync(grid, result) is { } report) Status?.Invoke(report);
    }

    /// <summary>grid.followFk: drill into the row the active FK cell points to (same as clicking its ↗).</summary>
    private void FollowActiveFk()
    {
        if (_selection.Model.Result is not { } result || _selection.Model.Active is not { } cell) return;
        if (!result.ForeignKeyColumns.Contains(cell.Col)) return;
        _ = NavigateForeignKey?.Invoke(result, cell.Col, cell.Row);
    }

    // ---- inspector ---------------------------------------------------------------------------

    /// <summary>Base point size for the inspector's value text (Settings ▸ Results, pushed in by the
    /// shell). A Ctrl+wheel zoom inside the pane updates this and reports it through
    /// <see cref="InspectorFontSizeChanged"/> so it persists.</summary>
    public double InspectorFontSize { get; set; } = 13;

    /// <summary>Raised when the user zooms the inspector, for the shell to write back to settings.</summary>
    public Action<double>? InspectorFontSizeChanged { get; set; }

    /// <summary>Which cell the pane is open on, so <c>grid.inspectValue</c> can tell "inspect this" from
    /// "close the one I already opened".</summary>
    private (object?[] Row, int Col)? _inspected;

    private void ShowInspector(ResultSetViewModel result, int index, object?[] row)
    {
        _inspected = (row, index);
        _inspector.Show(() => CellInspectorView.For(
            result, index, row, CloseInspector,
            fontSize: InspectorFontSize,
            onFontSize: size =>
            {
                InspectorFontSize = size;
                InspectorFontSizeChanged?.Invoke(size);
            }));
    }

    private void CloseInspector()
    {
        _inspected = null;
        _inspector.Hide();
    }

    /// <summary>The active cell, when there is one to inspect — guards <c>grid.inspectValue</c>.</summary>
    private (ResultSetViewModel Result, object?[] Row, int Col)? InspectableActiveCell()
    {
        if (GridTarget() is not { } t || _selection.Model.Active is not { } a) return null;
        return a.Col < t.Result.Columns.Count ? (t.Result, a.Row, a.Col) : null;
    }

    /// <summary>
    /// grid.inspectValue (F7): the keyboard path to the ⤢ affordance, which only appears on json and long
    /// values — F7 opens any cell. Pressing it again on the same cell closes the pane, so it reads as a peek
    /// rather than a one-way door; on a different cell it re-points the pane instead of closing it.
    /// </summary>
    private void ToggleInspectActiveCell()
    {
        if (InspectableActiveCell() is not { } cell) return;
        if (_inspected is { } open && ReferenceEquals(open.Row, cell.Row) && open.Col == cell.Col)
        {
            CloseInspector();
            return;
        }
        ShowInspector(cell.Result, cell.Col, cell.Row);
    }

    // ---- paging ------------------------------------------------------------------------------

    private readonly HashSet<ResultSetViewModel> _autoLoading = new(); // paging fetch in flight (infinite scroll)

    /// <summary>Fetch the next page when scrolled near the bottom (single-flight per result set).</summary>
    private void TriggerAutoLoad(ResultSetViewModel result)
    {
        if (LoadMore is not { } f || !result.HasMore || !_autoLoading.Add(result)) return;
        _ = LoadThenClear();
        async Task LoadThenClear()
        {
            try { await f(result); } finally { _autoLoading.Remove(result); }
        }
    }

    /// <summary>Re-apply pending-change row highlights (call after an in-place save clears pending state).</summary>
    public void RefreshRowHighlights()
        => Dispatcher.UIThread.Post(() =>
        {
            foreach (var (grid, result) in _editableGrids) ResultRowPainter.RefreshRowColors(grid, result);
        });
}
