using System;
using System.Collections.Generic;
using Squirrel.App.ViewModels;

namespace Squirrel.App.Results;

/// <summary>
/// Owns the results grid's cell-selection and drag state — previously eight loose fields scattered across
/// the <c>ResultView</c> partials that any of them could poke. This gives that state one home and names its
/// invariant: <see cref="Active"/>, <see cref="Anchor"/> and every entry in <see cref="Cells"/> belong to
/// <see cref="Result"/>. Pure state plus the restyle notifier; the visual reactions (re-applying selection
/// rings, toggling the stats bars, drawing rectangles) stay in <c>ResultView</c>, which mutates this and
/// then re-renders.
/// </summary>
public sealed class GridSelectionModel
{
    /// <summary>Selected cells, keyed by (row reference, column index). All belong to <see cref="Result"/>.</summary>
    public HashSet<(object?[] Row, int Col)> Cells { get; } = new();

    /// <summary>The result set the current selection / active / anchor belong to (null = nothing selected).</summary>
    public ResultSetViewModel? Result { get; set; }

    /// <summary>The active ("cursor") cell that arrow keys move; belongs to <see cref="Result"/>.</summary>
    public (object?[] Row, int Col)? Active { get; set; }

    /// <summary>The anchor a Shift-range extends from; belongs to <see cref="Result"/>.</summary>
    public (object?[] Row, int Col)? Anchor { get; set; }

    /// <summary>A click-drag cell selection is in progress.</summary>
    public bool Dragging { get; set; }

    /// <summary>The cell a drag started from.</summary>
    public (object?[] Row, int Col)? DragAnchor { get; set; }

    /// <summary>Each realized measure cell subscribes to re-apply its selection ring on a selection change;
    /// cleared on rebuild as the old cells are discarded.</summary>
    public Action? CellRestyle { get; set; }

    /// <summary>Drop the selection: cells, owning result, active + anchor. Drag state is left untouched
    /// (matches the previous ClearSelection).</summary>
    public void Clear()
    {
        Cells.Clear();
        Result = null;
        Active = null;
        Anchor = null;
    }
}
