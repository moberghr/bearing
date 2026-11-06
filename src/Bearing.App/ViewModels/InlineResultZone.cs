using System;
using System.Collections.Generic;

namespace Bearing.App.ViewModels;

/// <summary>
/// One block of results shown inline in the editor, beneath the statement that produced it. Belongs to a tab
/// (<see cref="EditorTabViewModel.InlineZones"/>); never persisted.
/// </summary>
public sealed class InlineResultZone
{
    public InlineResultZone(int offset, IReadOnlyList<ResultSetViewModel> sets, string? caption)
    {
        Offset = offset;
        Sets = sets;
        Caption = caption;
    }

    /// <summary>Identity for the view's control cache — offsets move, sets are replaced by re-runs.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Buffer offset the zone hangs under: it is drawn below the line holding this offset. Kept current
    /// by the editor while the tab is shown (it follows edits), so it is right again when the tab comes back.</summary>
    public int Offset { get; set; }

    /// <summary>The result sets in this zone, in the order the run returned them.</summary>
    public IReadOnlyList<ResultSetViewModel> Sets { get; }

    /// <summary>The height the user dragged the table's rows to, or null for the default
    /// (<c>InlineResultView.VisibleRows</c> rows). Kept on the zone so it survives the view being rebuilt
    /// (tab switches).</summary>
    public double? TableHeight { get; set; }

    /// <summary>A line above the table when the zone holds several sets ("3 result sets from this batch"),
    /// else null.</summary>
    public string? Caption { get; }
}
