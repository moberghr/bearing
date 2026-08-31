using System;
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
    /// <summary>
    /// Font size of the row-number gutter, and the grid-level default everything else overrides. Unchanged
    /// at 13: <c>DataGridRowHeader</c> has no theme <c>FontSize</c> setter, so the gutter is the one part
    /// that really does inherit this — and <see cref="GutterWidth"/> is only 46px, sized for five digits at
    /// exactly this size.
    /// </summary>
    public const double FontSize = 13;

    /// <summary>
    /// What a value cell renders at, pinned in <see cref="StyleGridChrome"/> and measured with in
    /// <see cref="MeasureText"/>. Those two have to be the same number: a column sized for one size and drawn
    /// at another is #73 — <c>grid.FontSize</c> never reached the cells, because the Fluent theme sets
    /// <c>FontSize</c> on <c>DataGridCell</c> and a setter on the descendant outranks an inherited value, so
    /// every column was measured at 13 and drawn at 15 and came out ~15% narrow.
    /// <para>
    /// 15 is what the theme has been giving the cells all along, so pinning it changes nothing on screen —
    /// it just stops the theme deciding. Same for <see cref="HeaderFontSize"/>, which is a different number
    /// for the same reason: the theme's, not ours.
    /// </para>
    /// </summary>
    public const double CellFontSize = 15;

    /// <summary>What a column header renders at, and is measured at. The Fluent theme gives headers 12 —
    /// three points smaller than the cells beneath them, which is deliberate in that theme and is what has
    /// been shipping. Pinned for the same reason as <see cref="CellFontSize"/>, and emphatically not unified
    /// with it: measuring headers at 15 would widen every column with a real name and push the neighbours
    /// off screen, which is the thing #30's sizing exists to prevent.</summary>
    public const double HeaderFontSize = 12;

    /// <summary>What a column header is drawn at (<see cref="StyleGridChrome"/>), and so what it must be
    /// measured at.</summary>
    public const FontWeight HeaderFontWeight = FontWeight.SemiBold;

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

    /// <summary>Non-text pixels in a column header: its padding both sides, the 1px column divider, and the
    /// same deliberate slack a value cell reserves (<c>ResultCellFactory.TextSlack</c>). A header measured to
    /// exactly its own width is arranged at exactly that width, with nothing left for a rounding difference
    /// or a differently-resolved mono face — the same knife edge the values were on before #73.</summary>
    private const double HeaderExtra = HeaderPadding * 2 + 1 + ResultCellFactory.TextSlack;

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

    /// <summary>Width of a string at the grid's font, through the same text stack that will render it (the
    /// mono stack in <c>App.axaml</c> resolves to whichever family is actually installed, and their advances
    /// differ by ~20%). Rounded up, because a column short by a fraction of a pixel ellipsizes exactly as
    /// badly as one short by five.
    /// <para>
    /// This replaced a cached single-character advance that <see cref="ColumnWidths"/> multiplied by a
    /// character count. Shaped text is not <c>N ×</c> the mean advance — side bearings and per-glyph widths
    /// differ — and the reconstruction ran short, which is what clipped <c>110122</c> to <c>1101…</c> (#73).
    /// One measurement per column, at column-build time, so nothing measures per cell.
    /// </para></summary>
    /// <param name="fontSize">The size the text will be drawn at — <see cref="CellFontSize"/> for a value,
    /// <see cref="HeaderFontSize"/> for a column name. Passing the wrong one reintroduces #73.</param>
    /// <param name="weight">The weight it will be drawn at. Headers are <see cref="FontWeight.SemiBold"/>
    /// (see <see cref="StyleGridChrome"/>) and a semibold mono face is a few percent wider, which on the
    /// header side is more than the slack reserved for it.</param>
    public static double MeasureText(string text, double fontSize, FontWeight weight = FontWeight.Normal)
    {
        if (text.Length == 0) return 0;
        var measured = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(MonoFont, weight: weight), fontSize, Brushes.Black).Width;
        // A family that resolved to nothing measurable would otherwise collapse every column onto
        // ColumnWidths.Min; 0.6em is the advance of a typical mono face.
        return Math.Ceiling(double.IsFinite(measured) && measured > 0 ? measured : text.Length * fontSize * 0.6);
    }

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
        // Pinned, not inherited from the grid: the Fluent theme sets FontSize on DataGridCell, and that
        // setter outranks grid.FontSize, so the cells rendered at the theme's size while ColumnWidths sized
        // them for ours (#73). Whatever FontSize says, this is what makes it true of the pixels.
        cell.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, CellFontSize));
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
        colHeader.Setters.Add(new Setter(TemplatedControl.FontWeightProperty, HeaderFontWeight));
        colHeader.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, HeaderFontSize)); // as the cells
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
