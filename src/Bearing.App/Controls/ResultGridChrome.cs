using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Controls;

/// <summary>
/// Bends the Fluent <see cref="DataGrid"/> into the Bearing results grid: cell-level selection instead of a
/// whole-row highlight, tighter rows, a real row-number gutter, themed headers, and content inset clear of
/// the always-visible overlay scrollbars. All of it applied per grid through the local style scope, so two
/// grids in a stacked view can't fight over one global style.
/// </summary>
public static class ResultGridChrome
{
    /// <summary>Apply every chrome tweak to a freshly-built results grid.</summary>
    public static void Apply(DataGrid grid)
    {
        ScrollViewer.SetAllowAutoHide(grid, false); // keep the scrollbar visible
        SuppressRowSelectionHighlight(grid);
        ReserveScrollbarSpace(grid);
        StyleGridChrome(grid);
    }

    /// <summary>Zero out the DataGrid's built-in whole-row selection highlight so only cell-level
    /// selection shows (the row-selected background is opacity-driven in the theme).</summary>
    private static void SuppressRowSelectionHighlight(DataGrid grid)
    {
        grid.Resources["DataGridRowSelectedBackgroundOpacity"] = 0.0;
        grid.Resources["DataGridRowSelectedHoveredBackgroundOpacity"] = 0.0;
        grid.Resources["DataGridRowSelectedUnfocusedBackgroundOpacity"] = 0.0;
        grid.Resources["DataGridRowSelectedHoveredUnfocusedBackgroundOpacity"] = 0.0;
        grid.Resources["DataGridCellFocusVisualPrimaryBrush"] = Brushes.Transparent;
        grid.Resources["DataGridCellFocusVisualSecondaryBrush"] = Brushes.Transparent;
    }

    /// <summary>Inset the rows/headers presenters so the always-visible overlay scrollbars (which the
    /// DataGrid template lets the rows span under) no longer cover cell content.</summary>
    private static void ReserveScrollbarSpace(DataGrid grid)
    {
        const double bar = 14; // approximate always-visible scrollbar thickness
        var rows = new Style(x => x.Name("PART_RowsPresenter"));
        rows.Setters.Add(new Setter(Layoutable.MarginProperty, new Thickness(0, 0, bar, bar)));
        grid.Styles.Add(rows);
        var headers = new Style(x => x.Name("PART_ColumnHeadersPresenter"));
        headers.Setters.Add(new Setter(Layoutable.MarginProperty, new Thickness(0, 0, bar, 0)));
        grid.Styles.Add(headers);
    }

    /// <summary>Trim the Fluent DataGrid's generous vertical padding and turn the row-number header into a
    /// proper right-aligned gutter (dim, padded, with a separator) instead of digits jammed against the
    /// first cell.</summary>
    private static void StyleGridChrome(DataGrid grid)
    {
        // Tighter data rows: lower the row floor and zero the cell's vertical padding so a single
        // line of text no longer sits in a tall box.
        var row = new Style(x => x.OfType<DataGridRow>());
        row.Setters.Add(new Setter(Layoutable.MinHeightProperty, 26.0));
        grid.Styles.Add(row);

        var cell = new Style(x => x.OfType<DataGridCell>());
        cell.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(0)));
        cell.Setters.Add(new Setter(Layoutable.MinHeightProperty, 26.0));
        grid.Styles.Add(cell);

        // Column headers (design §Results grid): bg.window fill, text.dim, 600 weight, border dividers —
        // not the Fluent default near-black. The row-number gutter shares this exact fill (below).
        var colHeader = new Style(x => x.OfType<DataGridColumnHeader>());
        colHeader.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Res("Bg.Window")));
        colHeader.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Res("Text.Dim")));
        colHeader.Setters.Add(new Setter(TemplatedControl.FontWeightProperty, FontWeight.SemiBold));
        colHeader.Setters.Add(new Setter(DataGridColumnHeader.SeparatorBrushProperty, SeparatorBrush));
        grid.Styles.Add(colHeader);

        // Row-number gutter: same bg.window as the header row, right-aligned dim digits, a separator.
        var header = new Style(x => x.OfType<DataGridRowHeader>());
        header.Setters.Add(new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
        header.Setters.Add(new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        header.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 0, 14, 0)));
        header.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Res("Text.Faint")));
        header.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Res("Bg.Window")));
        header.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, SeparatorBrush));
        header.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0, 0, 1, 0)));
        header.Setters.Add(new Setter(Layoutable.MinWidthProperty, 44.0)); // steady gutter for 2–3 digit counts
        grid.Styles.Add(header);
    }

    /// <summary>Double-tap a column header (or its resize gripper) → auto-fit the column to its content.</summary>
    public static void AutoFitColumn(DataGrid grid, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<DataGridColumnHeader>().FirstOrDefault() is not { } header)
            return;
        var col = grid.Columns.FirstOrDefault(c => ReferenceEquals(c.Header, header.Content));
        if (col is not null) col.Width = DataGridLength.Auto; // recomputes to fit content
    }
}
