using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Bearing.App.ViewModels;

namespace Bearing.App.Views;

/// <summary>
/// What a press on the tab strip landed on. Hit-testing by the <c>Tag</c> the templates already set, shared
/// by the window's press routing and the drag-reorder gesture — both have to agree on which presses belong
/// to the ✕ and the pin toggle, and a second copy of the walk is a second answer waiting to drift.
/// </summary>
internal static class TabHeaderParts
{
    /// <summary>Whether a pressed visual is (or is inside) a tab's ✕.</summary>
    public static bool IsCloseAffordance(object? source)
        => source is Visual visual
           && visual.FindAncestorOfType<Border>(includeSelf: true) is { Tag: "close" };

    /// <summary>
    /// Whether a pressed visual is (or is inside) a tab's pin toggle.
    /// <para>
    /// Separate from <see cref="IsCloseAffordance"/> rather than folded into one "is an affordance" check,
    /// because the two do different things to the selection: closing must <b>not</b> select the tab first
    /// (#87's neighbour rule would then pick from the wrong index), while pinning a tab you are not on is a
    /// perfectly ordinary thing to want and selecting it would be a surprise. Both need the strip's tunnel
    /// handler to leave them alone; only the reason differs.
    /// </para>
    /// </summary>
    public static bool IsPinAffordance(object? source)
        => source is Visual visual
           && visual.FindAncestorOfType<Border>(includeSelf: true) is { Tag: "pin" };

    /// <summary>The tab a pressed visual belongs to, found by walking up to its container.</summary>
    public static (Control Target, EditorTabViewModel Tab)? Tab(object? source)
        => source is Visual visual
           && visual.FindAncestorOfType<TabStripItem>(includeSelf: true) is
           { DataContext: EditorTabViewModel tab } item
            ? (item, tab)
            : null;
}
