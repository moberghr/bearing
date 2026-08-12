using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Bearing.App.Results;
using Bearing.App.ViewModels;

namespace Bearing.App.Controls;

/// <summary>
/// The results grid's right-click menu: Copy, Copy as ▸, Export ▸, and Fetch all rows. Its own class rather
/// than another slab of <see cref="ResultView"/> (§9.1) — the menu owns which actions it offers and when
/// they're applicable, and gets the actions themselves as callbacks.
/// <para>
/// Enabled state is recomputed on open, not at build time: a menu built with the grid empty would otherwise
/// stay greyed out for the life of the result.
/// </para>
/// </summary>
internal static class ResultContextMenu
{
    /// <param name="fetchAll">Null when the host hasn't wired paging (then the item is hidden).</param>
    /// <param name="export">Null when the host hasn't wired export (then the submenu is hidden).</param>
    public static MenuFlyout Build(
        ResultSetViewModel result,
        Func<bool> hasSelection,
        Action copy,
        Action<CopyFormat> copyAs,
        Func<Task>? fetchAll,
        Func<ExportFormat, Task>? export)
    {
        // No access-key underscores: the app's menus only use them on the top-level bar (File/Edit/…).
        var copyItem = new MenuItem { Header = "Copy" };
        copyItem.Click += (_, _) => copy();

        var copyAsItem = new MenuItem { Header = "Copy as" };
        foreach (var format in CopyRenderer.Alternatives)
        {
            var captured = format;
            var item = new MenuItem { Header = CopyRenderer.Label(captured) };
            item.Click += (_, _) => copyAs(captured);
            copyAsItem.Items.Add(item);
        }

        var menu = new MenuFlyout();
        menu.Items.Add(copyItem);
        menu.Items.Add(copyAsItem);

        MenuItem? fetchItem = null;
        if (fetchAll is not null)
        {
            menu.Items.Add(new Separator());
            fetchItem = new MenuItem { Header = "Fetch all rows" };
            fetchItem.Click += async (_, _) => await fetchAll();
            menu.Items.Add(fetchItem);
        }

        if (export is not null)
        {
            var exportItem = new MenuItem { Header = "Export" };
            foreach (var format in new[] { ExportFormat.Csv, ExportFormat.Xlsx })
            {
                var captured = format;
                var item = new MenuItem { Header = $"{ResultExport.Label(captured)}…" };
                item.Click += async (_, _) => await export(captured);
                exportItem.Items.Add(item);
            }
            menu.Items.Add(exportItem);
        }

        menu.Opening += (_, _) =>
        {
            // Copy acts on the selection; export and fetch-all act on the whole result, so they stay
            // available with nothing selected.
            var selected = hasSelection();
            copyItem.IsEnabled = selected;
            copyAsItem.IsEnabled = selected;
            if (fetchItem is not null) fetchItem.IsEnabled = result.IsPageable && result.HasMore;
        };
        return menu;
    }
}
