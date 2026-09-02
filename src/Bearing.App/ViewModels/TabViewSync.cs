using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Bearing.App.ViewModels;

/// <summary>
/// Brings an observable view into line with a desired list by the smallest set of edits — removals, then
/// inserts and moves — rather than clearing and refilling it.
/// <para>
/// The distinction matters because these views back the tab strips, whose <c>SelectedItem</c> is bound
/// two-way: a <c>Clear()</c> makes the control fix up its own selection and write that back through the
/// binding, which is exactly how #87 lost the selected tab. Patching in place leaves a selection that is
/// still in the list alone.
/// </para>
/// </summary>
internal static class TabViewSync
{
    /// <summary>Make <paramref name="view"/> hold exactly <paramref name="desired"/>, in that order.</summary>
    public static void Apply<T>(ObservableCollection<T> view, IReadOnlyList<T> desired)
        where T : class
    {
        // Backwards, so removing does not shift the indices still to be examined.
        for (var i = view.Count - 1; i >= 0; i--)
            if (!Contains(desired, view[i]))
                view.RemoveAt(i);

        for (var i = 0; i < desired.Count; i++)
        {
            var want = desired[i];
            var at = IndexOf(view, want);
            if (at < 0) view.Insert(i, want);
            else if (at != i) view.Move(at, i);
        }
    }

    // Reference identity, not Equals: tabs are view models, and two tabs on the same file are still two tabs.
    private static bool Contains<T>(IReadOnlyList<T> items, T item) where T : class
        => IndexOf(items, item) >= 0;

    private static int IndexOf<T>(IReadOnlyList<T> items, T item) where T : class
    {
        for (var i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item)) return i;
        return -1;
    }
}
