using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Bearing.App.Results;

namespace Bearing.App.Controls;

/// <summary>
/// The meta row's ⭳ Export control: a subtle button whose click opens a format menu (CSV / Excel).
/// <para>
/// It sits on <i>every</i> grid result, not only editable ones — exporting a join or a view is at least as
/// common as exporting a single table, and the button used to live in <see cref="ResultEditToolbar"/>, which
/// a read-only result never renders.
/// </para>
/// </summary>
internal static class ResultExportButton
{
    public static Control Build(Func<ExportFormat, Task> export)
    {
        var menu = new MenuFlyout();
        foreach (var format in new[] { ExportFormat.Csv, ExportFormat.Xlsx })
        {
            var captured = format;
            var item = new MenuItem { Header = $"{ResultExport.Label(captured)}…" };
            item.Click += async (_, _) => await export(captured);
            menu.Items.Add(item);
        }

        var button = ResultChrome.SubtleButton("⭳ Export", "Export every row of this result to a file");
        button.Flyout = menu;
        return button;
    }
}
