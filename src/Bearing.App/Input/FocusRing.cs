using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Bearing.App.Input;

/// <summary>
/// focus.cycle (F6): move keyboard focus around the shell's regions — editor → results grid → sidebar →
/// editor — skipping regions that aren't currently shown or can't take focus.
/// <para>
/// The ring arithmetic (<see cref="Order"/>) is separated from the focusing so it can be unit-tested; Wayland
/// blocks driving real focus headlessly (§4.3).
/// </para>
/// </summary>
public static class FocusRing
{
    /// <summary>The indices to try, in order, when leaving <paramref name="current"/>: the next region and
    /// onwards, wrapping, ending back at <paramref name="current"/> itself (so a ring of one still focuses
    /// something, and a ring where every other region refuses focus falls back to where it started).
    /// A <paramref name="current"/> outside the ring — nothing focused yet — starts from the first region.</summary>
    public static IEnumerable<int> Order(int count, int current)
    {
        if (count <= 0) yield break;
        var start = current is >= 0 && current < count ? current : 0;
        for (var step = 1; step <= count; step++)
            yield return (start + step) % count;
    }

    /// <summary>Whether <paramref name="node"/> is <paramref name="root"/> or sits inside it. Used both to
    /// classify which region holds focus and to tell a click inside the menu bar from one outside it.</summary>
    public static bool IsWithin(Visual? node, Visual root)
    {
        for (; node is not null; node = node.GetVisualParent())
            if (ReferenceEquals(node, root)) return true;
        return false;
    }

    /// <summary>Move focus to the next region that accepts it.
    /// <para>
    /// Each region is the control to focus paired with the container used to detect "focus is currently
    /// here" — they differ because a focused inner element (a grid cell presenter) must still classify as
    /// its region.
    /// </para>
    /// </summary>
    public static void Cycle(TopLevel? top, IReadOnlyList<(Control Focus, Visual Container)> regions)
    {
        if (regions.Count == 0) return;
        if (regions.Count == 1) { regions[0].Focus.Focus(); return; }

        var focused = top?.FocusManager?.GetFocusedElement() as Visual;
        var current = -1;
        if (focused is not null)
            for (var i = 0; i < regions.Count; i++)
                if (IsWithin(focused, regions[i].Container)) { current = i; break; }

        foreach (var i in Order(regions.Count, current))
            if (regions[i].Focus.Focus()) return;
    }
}
