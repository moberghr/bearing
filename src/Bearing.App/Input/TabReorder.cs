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
    /// Rows, not just columns: the strip wraps when it is expanded (the dropdown's "show all tabs"), so tabs
    /// on the second row share the X range of the first. The pointer's row is found by its Y, and only then
    /// does X decide the gap — reading X alone would put a drop on row two into row one.
    /// </para>
    /// <para>
    /// The row is the <b>nearest</b> one, not only a row the pointer is inside. A pointer a little below a
    /// 28px strip is still pointing at it — and the alternative, falling back to the end of the list, is how
    /// a tab dragged straight downwards and released ended up last, from a gesture that never moved
    /// sideways. How far outside the strip still counts is the caller's business
    /// (<c>TabDragReorder.DropTolerance</c>); this decides <i>which</i> row, given that it counts.
    /// </para>
    /// </summary>
    /// <param name="tabs">Each tab's bounds in strip coordinates, in item order. A zero-width entry is a
    /// container that is not realized yet and is skipped — it has no position to judge.</param>
    public static Gap Hit(IReadOnlyList<Rect> tabs, Point pointer)
    {
        if (NearestRow(tabs, pointer.Y) is not { } y) return new Gap(0, default);

        // The tabs on that row, in item order.
        var row = new List<int>();
        for (var i = 0; i < tabs.Count; i++)
            if (tabs[i].Width > 0 && tabs[i].Y == y) row.Add(i);

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

    /// <summary>
    /// The <c>Y</c> of the row nearest <paramref name="y"/>, or null when nothing is arranged.
    /// <para>
    /// Containment is <b>half-open</b> — <c>Y &lt;= y &lt; Bottom</c>. A <c>WrapPanel</c> puts row two's top
    /// exactly on row one's bottom, so an inclusive test matched both rows on that one pixel line, and the
    /// caller then mixed two rows' tabs into a single list.
    /// </para>
    /// <para>
    /// Ties go to the upper row. That only arises when rows are separated by a margin and the pointer is
    /// exactly between them; some answer has to be picked, and picking the same one every time is what keeps
    /// the caret from flickering between two rows as the hand shakes.
    /// </para>
    /// </summary>
    private static double? NearestRow(IReadOnlyList<Rect> tabs, double y)
    {
        double? best = null;
        var bestInside = false;
        var bestDistance = double.MaxValue;

        foreach (var tab in tabs)
        {
            if (tab.Width <= 0) continue;
            var inside = y >= tab.Y && y < tab.Bottom;
            var distance = inside ? 0 : y < tab.Y ? tab.Y - y : y - tab.Bottom;

            // A row the pointer is *in* always beats one it is merely touching. Both are at distance zero
            // on the line where two rows meet — that line is row two's, and comparing distances alone gave
            // it to row one.
            if (bestInside && !inside) continue;
            if (inside == bestInside)
            {
                if (distance > bestDistance) continue;
                if (distance == bestDistance && best is { } chosen && tab.Y >= chosen) continue;
            }

            bestInside = inside;
            bestDistance = distance;
            best = tab.Y;
        }
        return best;
    }

    /// <summary>
    /// Which way a drag at <paramref name="x"/> should pan a scrolling row: -1 left, +1 right, 0 not near an
    /// edge. <paramref name="zone"/> is how wide the sensitive strip along each edge is.
    /// <para>
    /// A row narrower than two zones would otherwise be all edge, and a pointer in the middle of it would
    /// be asked to scroll both ways at once. There is nowhere to stand in such a row, so it does not
    /// auto-scroll at all.
    /// </para>
    /// </summary>
    public static int EdgeScroll(double x, double width, double zone)
    {
        if (width <= zone * 2) return 0;
        if (x < zone) return -1;
        if (x > width - zone) return 1;
        return 0;
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
