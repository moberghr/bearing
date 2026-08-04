using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bearing.App.Formatting;
using Bearing.App.Input;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using Path = Avalonia.Controls.Shapes.Path;

namespace Bearing.App.Controls;

/// <summary>
/// Renders a query run's result sets inside the results dock (design RESULTS_GRID.md). Structure:
/// a persistent dock header (RESULTS label + Stacked/Tabbed toggle), then the body — result sets
/// stacked vertically (default) or as tabs. Each set has a meta row (Result · N rows · ms), a grid,
/// an optional edit toolbar (editable sets) and a paging footer (single SELECT). All styling binds
/// to the Bearing token brushes in Themes/Tokens.axaml. Self-contained: assign <see cref="Results"/>
/// and it rebuilds.
/// </summary>
public sealed partial class ResultView : UserControl
{
    private IReadOnlyList<ResultSetViewModel>? _results;

    /// <summary>The result sets to display. Assigning replaces the rendered content.</summary>
    public IReadOnlyList<ResultSetViewModel>? Results
    {
        get => _results;
        set { _results = value; _inspect = null; ClearSelection(); Rebuild(); } // a new run resets inspector + stats
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

    /// <summary>Invoked when the user requests the next page of a pageable result set.</summary>
    public Func<ResultSetViewModel, Task>? LoadMore { get; set; }

    /// <summary>Invoked when the user requests the total count of a pageable result set.</summary>
    public Func<ResultSetViewModel, Task>? CountTotal { get; set; }

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

    /// <summary>Invoked to preview the SQL a save would run (the [Preview SQL] button).</summary>
    public Action<ResultSetViewModel>? PreviewSql { get; set; }

    /// <summary>The shared keybinding pipeline (set once by the window). On assignment the grid's discrete
    /// commands register into the shared registry so the same matcher drives them; spatial cell navigation
    /// stays local (see <see cref="OnGridKey"/>).</summary>
    public KeyDispatcher? CommandDispatcher
    {
        get => _dispatcher;
        set { _dispatcher = value; if (value is not null) RegisterGridCommands(value.Registry); }
    }
    private KeyDispatcher? _dispatcher;

    // The grid+result the in-flight grid keystroke is dispatching into — published for the duration of a
    // single OnGridKey dispatch only (cleared in its finally), so it can never be read stale.
    private (DataGrid Grid, ResultSetViewModel Result)? _keyStrokeTarget;

    /// <summary>The grid + result a grid command should act on: the grid the current keystroke is
    /// dispatching into, or — when a command runs without a keystroke (the command palette) — the grid that
    /// owns the current cell selection. Null when neither applies (nothing is selected and no key is in
    /// flight), leaving the command a no-op / its guard false.</summary>
    private (DataGrid Grid, ResultSetViewModel Result)? GridTarget()
    {
        if (_keyStrokeTarget is { } t) return t;
        if (_sel.Result is { } r && _gridsByResult.TryGetValue(r, out var g)) return (g, r);
        return null;
    }

    private void RegisterGridCommands(CommandRegistry r)
    {
        r.Register(KeyCommand.Sync(CommandIds.GridCopy, "Copy", KeyScope.Grid, "Grid",
            () => { if (GridTarget() is { } t) CopySelection(t.Result); }));
        r.Register(KeyCommand.Sync(CommandIds.GridSelectAll, "Select all", KeyScope.Grid, "Grid",
            () => { if (GridTarget() is { } t) SelectAll(t.Result); }));
        r.Register(KeyCommand.Sync(CommandIds.GridDelete, "Delete rows", KeyScope.Grid, "Grid",
            () => { if (GridTarget() is { } t) DeleteSelectedRows(t.Grid, t.Result); },
            canRun: () => GridTarget()?.Result.IsEditable == true));
        r.Register(KeyCommand.Sync(CommandIds.GridBeginEdit, "Edit cell", KeyScope.Grid, "Grid",
            () => { if (GridTarget() is { } t) BeginEditActive(t.Grid, t.Result); },
            canRun: () => GridTarget()?.Result.IsEditable == true));
        r.Register(KeyCommand.Sync(CommandIds.GridClearSelection, "Clear selection", KeyScope.Grid, "Grid",
            () => { ClearSelection(); SelectionChanged(); },
            canRun: () => _sel.Cells.Count > 0));
        r.Register(KeyCommand.Sync(CommandIds.GridFollowFk, "Follow foreign key", KeyScope.Grid, "Grid",
            FollowActiveFk, canRun: ActiveCellIsFk));
        r.Register(KeyCommand.Sync(CommandIds.GridBack, "Back (foreign-key navigation)", KeyScope.Grid, "Grid",
            () => GoBack?.Invoke(), canRun: () => CanGoBack));
    }

    /// <summary>The grid to hand keyboard focus to (region cycling); null when no results are shown.</summary>
    public Control? FocusableGrid => _firstGrid;
    private DataGrid? _firstGrid;

    private bool ActiveCellIsFk()
        => _sel.Result is { } r && _sel.Active is { } cell && r.ForeignKeyColumns.Contains(cell.Col);

    /// <summary>grid.followFk: drill into the row the active FK cell points to (same as clicking its ↗).</summary>
    private void FollowActiveFk()
    {
        if (_sel.Result is not { } result || _sel.Active is not { } cell) return;
        if (!result.ForeignKeyColumns.Contains(cell.Col)) return;
        _ = NavigateForeignKey?.Invoke(result, cell.Col, cell.Row);
    }

}
