using System;
using System.Linq;
using Avalonia.Controls;
using Bearing.App.Results;
using Bearing.App.ViewModels;

namespace Bearing.App.Controls;

/// <summary>
/// A result grid's <b>column header</b> menu (#118): freeze up to here, hide this column, show all.
/// <para>
/// Separate from <see cref="ResultContextMenu"/>, which is the cell menu, because the two act on different
/// things — that one on the selection, this one on the column being pointed at. The header's left-click is
/// already taken by column selection (#6), so these live on its right-click.
/// </para>
/// <para>
/// Built once for both this and the proposed client-side sort (#106): a sort belongs on exactly this menu,
/// on exactly this column, so the place for it to go should exist before it lands rather than being
/// retrofitted around a menu shaped only for freezing.
/// </para>
/// </summary>
public static class ResultColumnMenu
{
    /// <summary>
    /// The menu for the column at source index <paramref name="index"/>.
    /// </summary>
    /// <param name="frozenThroughHere">
    /// How many leading display columns "freeze up to here" means for this header. Supplied by the caller
    /// because only the grid knows the display order — a user who has dragged a column somewhere else has
    /// changed which columns are to the left of this one, and the frozen pane is defined by that, not by the
    /// source order (<see cref="ColumnLayout.FrozenCount"/>).
    /// </param>
    public static MenuFlyout Build(ResultSetViewModel result, int index, int frozenThroughHere)
    {
        var layout = result.ColumnLayout;
        var menu = new MenuFlyout();

        var name = index >= 0 && index < result.Columns.Count ? result.Columns[index].Name : "column";

        var freeze = Item(
            $"Freeze through “{name}”",
            () => layout.Freeze(frozenThroughHere),
            // Already exactly this far frozen: the item would be a no-op, and offering it suggests it is not.
            enabled: layout.FrozenCount != frozenThroughHere && frozenThroughHere > 0);
        menu.Items.Add(freeze);

        menu.Items.Add(Item("Unfreeze columns", () => layout.Unfreeze(), enabled: layout.FrozenCount > 0));

        menu.Items.Add(new Separator());

        // Refused for the last visible column rather than offered and then silently declined — a grid with
        // no columns has no header left to undo it from (see ColumnLayout.Hide).
        menu.Items.Add(Item(
            $"Hide “{name}”",
            () => layout.Hide(index),
            enabled: !layout.IsHidden(index) && layout.VisibleCount > 1));

        menu.Items.Add(Item(
            layout.HiddenText is { } hidden ? $"Show all columns ({hidden})" : "Show all columns",
            () => layout.ShowAll(),
            enabled: layout.HiddenText is not null));

        return menu;
    }

    /// <summary>
    /// How many leading columns "freeze through <paramref name="index"/>" means, as a count of <b>visible</b>
    /// columns in display order.
    /// <para>
    /// Both halves are load-bearing. Display order, because a column the user dragged left changes what is to
    /// the left of it. And <em>visible</em>, because a hidden column keeps its place in
    /// <c>grid.Columns</c> and its <c>DisplayIndex</c> — so counting raw display indexes while
    /// <see cref="ColumnLayout.Freeze"/> clamps against the visible count made the menu freeze fewer columns
    /// than the item it had just offered said it would.
    /// </para>
    /// <para>
    /// Here rather than in the view because it is the arithmetic behind this menu's own item, and it is the
    /// part that was wrong.
    /// </para>
    /// </summary>
    public static int FreezeThrough(DataGrid grid, int index)
    {
        if (index < 0 || index >= grid.Columns.Count) return 0;
        var target = grid.Columns[index].DisplayIndex;
        return grid.Columns.Count(c => c.IsVisible && c.DisplayIndex <= target);
    }

    private static MenuItem Item(string header, Action invoke, bool enabled)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => invoke();
        return item;
    }
}
