using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Bearing.App.Results;
using Bearing.App.ViewModels;

namespace Bearing.App.Controls;

/// <summary>
/// The results grid's right-click menu: Copy, Copy as ▸, Paste, Set value ▸, Export ▸, and Fetch all rows.
/// Its
/// own class rather than another slab of <see cref="ResultView"/> (§9.1) — the menu owns which actions it
/// offers and when they're applicable, and gets the actions themselves as callbacks.
/// <para>
/// Enabled state is recomputed on open, not at build time: a menu built with the grid empty would otherwise
/// stay greyed out for the life of the result.
/// </para>
/// </summary>
internal static class ResultContextMenu
{
    /// <param name="paste">Null for a read-only result (then the item is hidden — a locked grid should not
    /// advertise a write it will refuse).</param>
    /// <param name="canPaste">Whether a paste has a cursor to anchor on right now.</param>
    /// <param name="setNull">Null for a read-only result (hidden, like Paste). This is the discoverable way to
    /// enter NULL — typing the <c>(null)</c> token was the only one (#33) — and the only way at all on a
    /// checkbox column, which has no text editor.</param>
    /// <param name="setExpression">Writes a server-side expression over the selection (#149). Null alongside
    /// <paramref name="setNull"/> for a read-only result.</param>
    /// <param name="expressions">The expressions to offer, canonically spelled, in menu order — supplied by
    /// the caller rather than read here, because which ones exist is the connection's engine's business and
    /// this class knows nothing about engines.</param>
    /// <param name="fetchAll">Null when the host hasn't wired paging (then the item is hidden).</param>
    /// <param name="export">Null when the host hasn't wired export (then the submenu is hidden).</param>
    public static MenuFlyout Build(
        ResultSetViewModel result,
        Func<bool> hasSelection,
        Action copy,
        Action<CopyFormat> copyAs,
        Func<Task>? paste,
        Func<bool> canPaste,
        Action? setNull,
        Action<string>? setExpression,
        IReadOnlyList<string> expressions,
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

        MenuItem? pasteItem = null;
        if (paste is not null)
        {
            pasteItem = new MenuItem { Header = "Paste" };
            pasteItem.Click += async (_, _) => await paste();
            menu.Items.Add(pasteItem);
        }

        // One home for "put a value in these cells", the way DBeaver's is: NULL first, then the expressions
        // the server evaluates. NULL keeps the top of the list (and its Ctrl+Shift+N) because it is the one
        // value a cell can hold that is not typeable as itself (#33) — the rest are SQL, which is a
        // different claim, so they sit below a separator rather than beside it.
        MenuItem? setValueItem = null;
        if (setNull is not null)
        {
            setValueItem = new MenuItem { Header = "Set value" };

            var nullItem = new MenuItem { Header = "NULL" };
            nullItem.Click += (_, _) => setNull();
            setValueItem.Items.Add(nullItem);

            if (setExpression is not null && expressions.Count > 0)
            {
                setValueItem.Items.Add(new Separator());
                foreach (var expression in expressions)
                {
                    var captured = expression;
                    var item = new MenuItem { Header = captured };
                    item.Click += (_, _) => setExpression(captured);
                    setValueItem.Items.Add(item);
                }
            }
            menu.Items.Add(setValueItem);
        }

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
            // Paste writes at the cursor, not over the selection, so it has its own applicability test.
            if (pasteItem is not null) pasteItem.IsEnabled = canPaste();
            // Set value writes over the selection, so it shares Copy's applicability test.
            if (setValueItem is not null) setValueItem.IsEnabled = selected;
            if (fetchItem is not null) fetchItem.IsEnabled = result.IsPageable && result.HasMore;
        };
        return menu;
    }
}
