using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Squirrel.App.Formatting;
using Squirrel.App.Input;
using Squirrel.App.Results;
using Squirrel.App.ViewModels;
using Squirrel.Core.Workspace;
using Path = Avalonia.Controls.Shapes.Path;

namespace Squirrel.App.Controls;

public sealed partial class ResultView
{
    /// <summary>Build a result set's body (grid + stats bar + paging footer) and hand back the grid so
    /// the caller can put the (subtle) edit controls on the meta row. Non-grid results return null grid.</summary>
    private Control BuildResultSet(ResultSetViewModel result, out DataGrid? grid)
    {
        grid = null;
        if (!result.Success)
            return new TextBlock { Text = $"Error: {result.Error?.Message}", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };

        if (result.Columns.Count == 0)
            return new TextBlock { Text = result.Message ?? "Statement executed.", Margin = new Thickness(8) };

        grid = BuildGrid(result);
        // Any cell is selectable; the stats bar surfaces itself only when ≥2 selected cells are numeric.
        // Row count + count-on-demand + edit controls all live on the meta row now (no footer).
        return WithStatsBar(grid, result);
    }

    // Long-text/array/json columns start capped so they show partially, but stay freely resizable
    // (no MaxWidth) and can be double-clicked (on the header) to auto-fit.
    private const double WideColumnInitial = 280;

    private DataGrid BuildGrid(ResultSetViewModel result)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = !result.IsEditable,
            CanUserResizeColumns = true,
            CanUserReorderColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            HeadersVisibility = DataGridHeadersVisibility.All, // row-number gutter + column headers
            Background = Res("Bg.Editor"),                     // flat body per design (#1F1F28)
            HorizontalGridLinesBrush = GridLine,               // subtle #252531 row/column dividers
            VerticalGridLinesBrush = GridLine,
        };
        _firstGrid ??= grid; // first grid of this render → region-focus target
        ScrollViewer.SetAllowAutoHide(grid, false); // keep the scrollbar visible
        SuppressRowSelectionHighlight(grid);        // cell-level selection only — no whole-row blue bar
        ReserveScrollbarSpace(grid);                // inset content so the scrollbars don't cover data
        StyleGridChrome(grid);                      // tighter rows + a proper row-number gutter
        grid.LoadingRow += (_, e) =>
        {
            e.Row.Header = (e.Row.Index + 1).ToString();
            // Design row striping; editable rows still tint on a pending edit/new/delete (handled inside).
            if (result.IsEditable) ApplyRowStatus(e.Row, result);
            else e.Row.Background = RowBackground(e.Row.Index);
            // Infinite scroll: when a near-bottom row realizes and more rows exist, fetch the next page.
            if (result.HasMore && e.Row.Index >= result.Rows.Count - 8) TriggerAutoLoad(result);
        };
        for (var i = 0; i < result.Columns.Count; i++)
        {
            // FK columns keep their (read-only) jump-icon template; bool columns render a checkbox;
            // everything else is a value cell (measure / inspectable / plain text), editable via a
            // template editor (indexer-bound DataGridTextColumns don't edit reliably on recycle).
            DataGridColumn col;
            if (result.ForeignKeyColumns.Contains(i))
                col = ForeignKeyColumn(result, i, grid);
            else if (IsBoolColumn(result.Columns[i]))
                col = new DataGridTemplateColumn { CellTemplate = BoolCell(result, i) }; // toggles inline
            else if (result.IsEditable)
                col = new DataGridTemplateColumn
                {
                    Tag = i, // column index, read back in CellEditEnding
                    CellTemplate = ValueCell(result, i, grid),
                    CellEditingTemplate = CellEditor(i),
                };
            else
                col = new DataGridTemplateColumn { CellTemplate = ValueCell(result, i, grid) };
            col.Header = BuildColumnHeader(result, i); // name + PK/FK/type badges
            if (IsWideType(result.Columns[i])) col.Width = new DataGridLength(WideColumnInitial); // capped, resizable
            grid.Columns.Add(col);
        }
        grid.ItemsSource = result.Rows; // ObservableCollection → paged rows append without a rebuild

        // Double-tap a column header (incl. its resize gripper) → auto-fit that column to its content.
        grid.DoubleTapped += (_, e) => AutoFitColumn(grid, e);

        // Measure cells drive their own selection (per-cell PointerPressed, below). The grid extends a
        // drag and clears the selection when a click missed a measure cell. handledEventsToo:true is
        // required because the DataGrid marks these pointer events handled in the tunnel phase.
        grid.AddHandler(PointerMovedEvent, (_, e) => { if (_dragging) DragSelectTo(grid, result, e); },
            RoutingStrategies.Bubble, handledEventsToo: true);
        grid.AddHandler(PointerReleasedEvent, (_, e) => { if (_dragging) { _dragging = false; e.Pointer.Capture(null); } },
            RoutingStrategies.Bubble, handledEventsToo: true);
        // Clear on click-away: plain handler (skipped when a measure cell already handled the press).
        grid.PointerPressed += (_, _) => { if (_selection.Count > 0) { ClearSelection(); SelectionChanged(); } };

        // Keyboard-drive the grid. Handled in the tunnel phase so we pre-empt the DataGrid's own
        // arrow-nav / Ctrl+C before it acts (setting Handled skips its class-level OnKeyDown).
        grid.Focusable = true;
        grid.AddHandler(KeyDownEvent, (_, e) => OnGridKey(grid, result, e), RoutingStrategies.Tunnel);

        // When the grid takes focus (e.g. via F6) with no active cell yet, seed the top-left cell so the
        // focus is visible instead of the caller having to press an arrow first.
        grid.GotFocus += (_, _) =>
        {
            if (result.Rows.Count > 0 && (_active is null || !ReferenceEquals(_selectionResult, result)))
                MoveActive(grid, result, result.Rows[0], FirstSelectableColumn(result), extend: false);
        };

        if (result.IsEditable)
        {
            _editableGrids.Add((grid, result));
            grid.CellEditEnding += (_, e) =>
            {
                if (e.EditAction != DataGridEditAction.Commit) return;
                if (e.Row.DataContext is not object?[] row || e.Column.Tag is not int idx) return;
                if (e.EditingElement is TextBox tb && idx < row.Length) row[idx] = tb.Text;
                result.MarkEdited(row);
                ApplyRowStatus(e.Row, result); // tint + status bar on the edited row immediately
            };
        }
        return grid;
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

    /// <summary>Long-text/array/json/tsvector columns get a capped initial width (still resizable).</summary>
    private static bool IsWideType(Squirrel.Core.Data.ColumnDescriptor c)
    {
        var t = Nullable.GetUnderlyingType(c.ClrType) ?? c.ClrType;
        return t == typeof(string) || t.IsArray || IsJsonType(c.DataTypeName)
            || string.Equals(c.DataTypeName, "tsvector", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Double-tap a column header (or its resize gripper) → auto-fit the column to its content.</summary>
    private static void AutoFitColumn(DataGrid grid, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<DataGridColumnHeader>().FirstOrDefault() is not { } header)
            return;
        var col = grid.Columns.FirstOrDefault(c => ReferenceEquals(c.Header, header.Content));
        if (col is not null) col.Width = DataGridLength.Auto; // recomputes to fit content
    }

    /// <summary>Inset the rows/headers presenters so the always-visible overlay scrollbars (which the
    /// DataGrid template lets the rows span under) no longer cover cell content.</summary>
    private static void ReserveScrollbarSpace(DataGrid grid)
    {
        const double bar = 14; // approximate always-visible scrollbar thickness
        var rows = new Style(x => x.Name("PART_RowsPresenter"));
        rows.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, bar, bar)));
        grid.Styles.Add(rows);
        var headers = new Style(x => x.Name("PART_ColumnHeadersPresenter"));
        headers.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, bar, 0)));
        grid.Styles.Add(headers);
    }

    /// <summary>Trim the Fluent DataGrid's generous vertical padding and turn the row-number header into a
    /// proper right-aligned gutter (dim, padded, with a separator) instead of digits jammed against the
    /// first cell. Applied per grid via the local style scope.</summary>
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
        colHeader.Setters.Add(new Setter(DataGridColumnHeader.SeparatorBrushProperty, Separator));
        grid.Styles.Add(colHeader);

        // Row-number gutter: same bg.window as the header row, right-aligned dim digits, a separator.
        var header = new Style(x => x.OfType<DataGridRowHeader>());
        header.Setters.Add(new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
        header.Setters.Add(new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        header.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 0, 14, 0)));
        header.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Res("Text.Faint")));
        header.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Res("Bg.Window")));
        header.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, Separator));
        header.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0, 0, 1, 0)));
        header.Setters.Add(new Setter(Layoutable.MinWidthProperty, 44.0)); // steady gutter for 2–3 digit counts
        grid.Styles.Add(header);
    }

}
