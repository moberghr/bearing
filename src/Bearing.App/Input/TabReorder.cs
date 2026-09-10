using System;
using System.Collections.Generic;
using Avalonia;

namespace Bearing.App.Input;

/// <summary>
/// Where a dragged tab lands. Two pieces of arithmetic, both pulled out of the gesture (§2.5) because both
/// have the edge cases and neither needs a window: which gap the pointer is in, and which index in
/// <c>Tabs</c> that gap corresponds to.
/// <para>
/// The second is the awkward one, and it is why this exists at all. The strips render two <b>views</b> over
/// one master list (<c>PinnedTabs</c> / <c>UnpinnedTabs</c>, §9.1), so "third in the unpinned row" is not an
/// index into anything the user can see the whole of — it has to be translated back through the pinned flags
/// of every tab. Getting that wrong does not throw; it silently drops the tab somewhere else.
/// </para>
/// </summary>
internal static class TabReorder
{
    /// <summary>
    /// A gap between two tabs: the slot a drop would insert at, and where to draw the insertion caret.
    /// </summary>
    /// <param name="Slot">Index in the strip's own items — <c>0</c> is before the first tab,
    /// <c>ItemCount</c> is after the last. Counts the dragged tab, which is still in the strip while the
    /// drag is in flight; <see cref="SlotWithoutDragged"/> takes it back out.</param>
    /// <param name="Caret">The insertion line in the strip's coordinates: zero width, the height of the tab
    /// it sits beside.</param>
    internal readonly record struct Gap(int Slot, Rect Caret);

    /// <summary>
    /// Which gap a pointer at <paramref name="pointer"/> is in, given each tab's arranged bounds.
    /// <para>
    /// Rows, not just columns: the strip wraps when it is expanded (the chevron's "show all tabs"), so tabs
    /// on the second row share the X range of the first. The pointer's own row is found by its Y, and only
    /// then does X decide the gap — reading X alone would put a drop on row two into row one.
    /// </para>
    /// <para>
    /// A pointer past the last tab of its row lands after that tab, not at the end of the strip: with the
    /// strip wrapped, the far right of row one is nowhere near the end of the list.
    /// </para>
    /// </summary>
    /// <param name="tabs">Each tab's bounds in strip coordinates, in item order. A zero-width entry is a
    /// container that is not realized yet and is skipped — it has no position to judge.</param>
    public static Gap Hit(IReadOnlyList<Rect> tabs, Point pointer)
    {
        // The tabs on the pointer's own row. With a single-row strip that is all of them.
        var row = new List<int>();
        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].Width <= 0) continue;
            if (pointer.Y >= tabs[i].Y && pointer.Y <= tabs[i].Bottom) row.Add(i);
        }

        // Above the first row or below the last: the strip is the target but no row is, so fall back to the
        // end of the list rather than refusing — a drag that strays a few pixels below a 28px strip still
        // means what it plainly means.
        if (row.Count == 0) return AfterLast(tabs);

        // Halfway is the tipping point, as it is in every reorderable list: past a tab's midpoint the drop
        // goes after it.
        foreach (var i in row)
        {
            var bounds = tabs[i];
            if (pointer.X < bounds.X + bounds.Width / 2) return new Gap(i, Caret(bounds.X, bounds));
        }

        var last = tabs[row[^1]];
        return new Gap(row[^1] + 1, Caret(last.Right, last));
    }

    private static Gap AfterLast(IReadOnlyList<Rect> tabs)
    {
        for (var i = tabs.Count - 1; i >= 0; i--)
            if (tabs[i].Width > 0) return new Gap(i + 1, Caret(tabs[i].Right, tabs[i]));
        return new Gap(0, default);   // nothing arranged: there is one slot and no caret to draw
    }

    private static Rect Caret(double x, Rect tab) => new(x, tab.Y, 0, tab.Height);

    /// <summary>
    /// The same slot with the dragged tab taken out of the count. The dragged tab stays in its strip while
    /// the drag is in flight (nothing moves until the pointer is released), so every gap to its right is one
    /// too far once it has been lifted out.
    /// </summary>
    /// <param name="draggedIndex">The dragged tab's index in this strip, or <c>-1</c> when it is being
    /// dragged in from the other row.</param>
    public static int SlotWithoutDragged(int slot, int draggedIndex)
        => draggedIndex >= 0 && slot > draggedIndex ? slot - 1 : slot;

    /// <summary>
    /// The index in the master tab list to move the dragged tab to.
    /// <para>
    /// The answer is in <c>ObservableCollection.Move</c>'s coordinates, which is the list with the moved item
    /// already removed — that is what its destination index means, and mixing the two spaces is the mistake
    /// this function exists to contain (§9.9b item 6).
    /// </para>
    /// </summary>
    /// <param name="pinned">Each tab's <c>IsPinned</c>, in master order.</param>
    /// <param name="from">The dragged tab's index in that list.</param>
    /// <param name="targetPinned">Which row it is being dropped on — dropping across rows pins or unpins it,
    /// which is the same gesture browsers use and needs no separate affordance.</param>
    /// <param name="slot">Its position among the target row's <i>other</i> tabs.</param>
    /// <returns>The destination index, or <c>-1</c> when <paramref name="from"/> is not a tab.</returns>
    public static int TargetIndex(IReadOnlyList<bool> pinned, int from, bool targetPinned, int slot)
    {
        if (from < 0 || from >= pinned.Count) return -1;

        // Where the target row's other tabs sit once the dragged one is out of the list.
        var row = new List<int>();
        for (var i = 0; i < pinned.Count; i++)
        {
            if (i == from || pinned[i] != targetPinned) continue;
            row.Add(i < from ? i : i - 1);
        }

        slot = Math.Clamp(slot, 0, row.Count);
        if (slot < row.Count) return row[slot];
        if (row.Count > 0) return row[^1] + 1;

        // An empty target row — the first tab to be pinned, or the last one being unpinned. There is no slot
        // to pick between, so the row's own end of the list: pinned tabs lead it, unpinned tabs trail it.
        return targetPinned ? 0 : pinned.Count - 1;
    }
}
