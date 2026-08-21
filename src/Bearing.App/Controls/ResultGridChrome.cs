using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Bearing.App.Results;
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
    /// <summary>Data font size. Monospace so columns line up; set here rather than in a global
    /// <c>DataGrid</c> style because <see cref="ColumnWidths"/> has to measure a character at exactly this
    /// size while the columns are being built — before the grid is attached and app styles have applied.</summary>
    public const double FontSize = 13;

    /// <summary>Gap between a value cell's edge and its text (the display TextBlock's margin in
    /// <see cref="ResultCellFactory"/>).</summary>
    public const double CellTextMargin = 4;

    /// <summary>Left edge of a value's text inside its cell: the text margin plus the 1px every cell reserves
    /// inside its selection border. The header aims at this line.</summary>
    public const double CellTextInset = CellTextMargin + 1;

    /// <summary>Column-header padding: the same inset as the values under it, so a header's text and its
    /// column's text share a left edge.</summary>
    public const double HeaderPadding = CellTextInset;

    /// <summary>Width of the row-number gutter, pinned via <see cref="DataGrid.RowHeaderWidth"/> rather than
    /// left to auto-measure. Auto sized the gutter and the corner header above it independently, and they
    /// landed 2px apart — which offset the entire header row against the body, every column divider included.
    /// A style <c>MinWidth</c> can't fix that: it applies to the row headers, not to the corner. 46px is the
    /// design's <c>#</c> column (RESULTS_GRID §3), and with the padding below leaves room for 5 digits.</summary>
    public const double GutterWidth = 46;

    /// <summary>Non-text pixels in a column header: its padding both sides plus the 1px column divider.</summary>
    private const double HeaderExtra = HeaderPadding * 2 + 1;

    /// <summary>Width of a row's left status bar (teal edited / green new / red deleted — see
    /// <see cref="ResultRowPainter"/>). Reserved on every row, dirty or not, as the row's
    /// <c>BorderThickness</c>: the DataGridRow template insets its content by it, so a bar that appeared only
    /// on a dirty row would shove that row's cells sideways as you typed.
    /// <para>
    /// Because it insets the row — gutter and cells alike — the header row has to start at the same offset or
    /// every column divider reads 2px out. That is what the corner header's margin below is for; the column
    /// headers presenter inherits it, sitting in the grid column the corner sizes.
    /// </para></summary>
    public const double RowStatusBarWidth = 2;

    /// <summary>Apply every chrome tweak to a freshly-built results grid.</summary>
    public static void Apply(DataGrid grid)
    {
        grid.FontFamily = MonoFont;
        grid.FontSize = FontSize;
        grid.RowHeaderWidth = GutterWidth; // pinned so the gutter and the corner header can't disagree
        ScrollViewer.SetAllowAutoHide(grid, false); // keep the scrollbar visible
        SuppressRowSelectionHighlight(grid);
        ReserveScrollbarSpace(grid);
        StyleGridChrome(grid);
    }

    /// <summary>Width of one character at the grid's font — the unit <see cref="ColumnWidths"/> counts in.
    /// Measured through Avalonia's text stack (the mono stack in <c>App.axaml</c> resolves to whichever
    /// family is actually installed, and their advances differ by ~20%), cached because the font is fixed
    /// for the app's lifetime and every column asks.</summary>
    public static double CharAdvance
    {
        get
        {
            if (_advance is { } cached) return cached;
            const string probe = "0000000000";
            var text = new FormattedText(probe, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(MonoFont), FontSize, Brushes.Black);
            var measured = text.Width / probe.Length;
            // A family that resolved to nothing measurable would otherwise collapse every column onto
            // ColumnWidths.Min; 0.6em is the advance of a typical mono face.
            return (_advance = double.IsFinite(measured) && measured > 0 ? measured : FontSize * 0.6).Value;
        }
    }
    private static double? _advance;

    /// <summary>Pixels a header costs beyond its text: our padding, the divider, and each inline type badge
    /// (9px bold text in a padded chip with a 5px gap — see <see cref="ResultChrome.Badge"/>).</summary>
    public static double HeaderChromeFor(IEnumerable<string> badges)
    {
        var extra = HeaderExtra;
        foreach (var badge in badges) extra += badge.Length * 6 + 13;
        return extra;
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

    /// <summary>Trim the Fluent DataGrid's generous vertical padding, put the headers on the design's fill,
    /// and give the row-number gutter the same treatment.</summary>
    private static void StyleGridChrome(DataGrid grid)
    {
        // Tighter data rows: lower the row floor and zero the cell's vertical padding so a single
        // line of text no longer sits in a tall box.
        var row = new Style(x => x.OfType<DataGridRow>());
        row.Setters.Add(new Setter(Layoutable.MinHeightProperty, 26.0));
        // The status-bar lane, reserved for every row so a row doesn't shift when it goes dirty; only the
        // colour is per-row (ResultRowPainter). Matched by the corner header's margin below.
        row.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty,
            new Thickness(RowStatusBarWidth, 0, 0, 0)));
        grid.Styles.Add(row);

        // Push the whole header row over by the status-bar lane, so headers, the row-number gutter and the
        // cells share their column edges. The corner header sizes the template grid column the column-headers
        // presenter sits in, so shifting the corner shifts every header with it — do not also margin the
        // presenter, that would move them twice.
        var corner = new Style(x => x.Name("PART_TopLeftCornerHeader"));
        corner.Setters.Add(new Setter(Layoutable.MarginProperty, new Thickness(RowStatusBarWidth, 0, 0, 0)));
        grid.Styles.Add(corner);

        var cell = new Style(x => x.OfType<DataGridCell>());
        cell.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(0)));
        cell.Setters.Add(new Setter(Layoutable.MinHeightProperty, 26.0));
        grid.Styles.Add(cell);

        // Column headers (design §Results grid): bg.window fill, text.dim, 600 weight, border dividers —
        // not the Fluent default near-black. The row-number gutter shares this exact fill (below).
        var colHeader = new Style(x => x.OfType<DataGridColumnHeader>());
        colHeader.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Res("Bg.Window")));
        colHeader.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Res("Text.Dim")));
        // Tighter than Fluent's, and set so a header shares its column's left edge with the values under it
        // (see HeaderPadding) — plus, the initial width being computed rather than measured, a value
        // ColumnWidths can rely on instead of guessing (#30).
        colHeader.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(HeaderPadding, 0)));
        colHeader.Setters.Add(new Setter(TemplatedControl.FontWeightProperty, FontWeight.SemiBold));
        colHeader.Setters.Add(new Setter(DataGridColumnHeader.SeparatorBrushProperty, SeparatorBrush));
        grid.Styles.Add(colHeader);

        // Row-number gutter: same bg.window fill as the header row, dim digits, our separator colour.
        // Only these three setters do anything — the DataGridRowHeader template (12.1) hardcodes the rest:
        // its ContentPresenter carries HorizontalAlignment="Center" as a local value (which outranks a
        // style), and it never template-binds Padding, BorderBrush or BorderThickness. So the digits are
        // centred, not right-aligned as the design's `#` column asks, and the 1px right divider comes from
        // SeparatorBrush rather than a border of ours. Setting the four properties the template ignores is
        // what made this style look like it controlled a width it never did — see GutterWidth.
        var header = new Style(x => x.OfType<DataGridRowHeader>());
        header.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Res("Text.Faint")));
        header.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Res("Bg.Window")));
        header.Setters.Add(new Setter(DataGridRowHeader.SeparatorBrushProperty, SeparatorBrush));
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
