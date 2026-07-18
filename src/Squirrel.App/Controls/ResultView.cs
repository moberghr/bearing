using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Squirrel.App.Formatting;
using Squirrel.App.ViewModels;
using Path = Avalonia.Controls.Shapes.Path;

namespace Squirrel.App.Controls;

/// <summary>
/// Renders a query run's result sets: a single grid for one set, sub-tabs for several,
/// and inline text for empty/non-query/error results. A pageable set (single SELECT) gets a
/// footer with the loaded-row count plus "Load more" / "Count" that call back into the shell.
/// Self-contained and reusable — assign <see cref="Results"/> and it rebuilds its content.
/// </summary>
public sealed class ResultView : UserControl
{
    private IReadOnlyList<ResultSetViewModel>? _results;

    /// <summary>The result sets to display. Assigning replaces the rendered content.</summary>
    public IReadOnlyList<ResultSetViewModel>? Results
    {
        get => _results;
        set { _results = value; Rebuild(); }
    }

    /// <summary>Invoked when the user requests the next page of a pageable result set.</summary>
    public Func<ResultSetViewModel, Task>? LoadMore { get; set; }

    /// <summary>Invoked when the user requests the total count of a pageable result set.</summary>
    public Func<ResultSetViewModel, Task>? CountTotal { get; set; }

    /// <summary>Invoked when a foreign-key cell is clicked: (result set, column index, row values).</summary>
    public Func<ResultSetViewModel, int, object?[], Task>? NavigateForeignKey { get; set; }

    /// <summary>Whether the back bar is shown (FK navigation has a previous result to return to).</summary>
    public bool CanGoBack { get; set; }

    /// <summary>Invoked when the back bar's button is clicked.</summary>
    public Action? GoBack { get; set; }

    /// <summary>Invoked to commit a result set's pending edits (the [Save changes] button).</summary>
    public Func<ResultSetViewModel, Task>? SaveChanges { get; set; }

    /// <summary>Invoked to discard a result set's pending edits (the [Discard] button).</summary>
    public Func<ResultSetViewModel, Task>? DiscardChanges { get; set; }

    /// <summary>Invoked to preview the SQL a save would run (the [Preview SQL] button).</summary>
    public Action<ResultSetViewModel>? PreviewSql { get; set; }

    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.FromRgb(0x4D, 0x9B, 0xFF));
    // Green for new/edited rows, red for rows pending deletion (kept visible until save).
    private static readonly IBrush ChangedRowBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x3F, 0xB9, 0x50));
    private static readonly IBrush DeletedRowBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xE0, 0x40, 0x40));
    private static readonly IBrush NullBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x88, 0x88, 0x88));

    // Editable grids currently rendered (grid + its result) — used to re-tint rows after an in-place save.
    private readonly List<(DataGrid Grid, ResultSetViewModel Result)> _editableGrids = new();

    /// <summary>Re-apply pending-change row highlights (call after an in-place save clears pending state).</summary>
    public void RefreshRowHighlights()
        => Dispatcher.UIThread.Post(() => { foreach (var (grid, result) in _editableGrids) RefreshRowColors(grid, result); });

    private static IBrush RowBrush(ResultSetViewModel result, object?[]? row)
    {
        if (row is null) return Brushes.Transparent;
        if (result.IsRowDeleted(row)) return DeletedRowBrush;
        if (result.IsNewRow(row) || result.IsRowEdited(row)) return ChangedRowBrush;
        return Brushes.Transparent;
    }

    /// <summary>Re-tint the currently-realized rows to reflect pending edit/new/delete state.</summary>
    private static void RefreshRowColors(DataGrid grid, ResultSetViewModel result)
    {
        foreach (var dgr in grid.GetVisualDescendants().OfType<DataGridRow>())
            dgr.Background = RowBrush(result, dgr.DataContext as object?[]);
    }

    private void Rebuild()
    {
        _editableGrids.Clear();
        var body = BuildBody();
        Content = CanGoBack ? WithBackBar(body) : body;
    }

    private Control? BuildBody()
    {
        var results = _results;
        if (results is null || results.Count == 0) return null;
        if (results.Count == 1) return BuildResultSet(results[0]);

        var tabs = new TabControl();
        for (var i = 0; i < results.Count; i++)
            tabs.Items.Add(new TabItem { Header = TabHeader(i, results[i]), Content = BuildResultSet(results[i]) });
        tabs.SelectedIndex = 0;
        return tabs;
    }

    /// <summary>Prepend a slim "Back" bar (returns to the pre-navigation result) above the body.</summary>
    private Control WithBackBar(Control? body)
    {
        var arrow = new Path
        {
            Data = Geometry.Parse("M5,1 L1,5 L5,9 M1,5 L10,5"),
            Stroke = LinkBrush,
            StrokeThickness = 1.4,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var caption = new TextBlock { Text = "Back", Foreground = LinkBrush, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        inner.Children.Add(arrow);
        inner.Children.Add(caption);

        var back = new Button
        {
            Content = inner,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        back.Click += (_, _) => GoBack?.Invoke();

        var bar = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x88, 0x88, 0x88)),
            Padding = new Thickness(2),
            Child = back,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        DockPanel.SetDock(bar, Dock.Top);

        var panel = new DockPanel();
        panel.Children.Add(bar);
        if (body is not null) panel.Children.Add(body);
        return panel;
    }

    private static string TabHeader(int index, ResultSetViewModel result)
    {
        if (!result.Success) return $"Result {index + 1} · error";
        if (result.Columns.Count == 0) return $"Result {index + 1} · {result.Message}";
        return $"Result {index + 1} ({result.RowCount})";
    }

    private Control BuildResultSet(ResultSetViewModel result)
    {
        if (!result.Success)
            return new TextBlock { Text = $"Error: {result.Error?.Message}", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };

        if (result.Columns.Count == 0)
            return new TextBlock { Text = result.Message ?? "Statement executed.", Margin = new Thickness(8) };

        var grid = BuildGrid(result);
        Control content = result.IsPageable ? WithFooter(grid, result) : grid;
        return result.IsEditable ? WithEditToolbar(content, grid, result) : content;
    }

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
        };
        grid.LoadingRow += (_, e) =>
        {
            e.Row.Header = (e.Row.Index + 1).ToString();
            if (result.IsEditable) e.Row.Background = RowBrush(result, e.Row.DataContext as object?[]);
        };
        for (var i = 0; i < result.Columns.Count; i++)
        {
            // FK columns keep their (read-only) jump-icon template; other columns are editable text
            // (an explicit template-column editor — indexer-bound DataGridTextColumns don't edit reliably).
            if (result.ForeignKeyColumns.Contains(i))
                grid.Columns.Add(ForeignKeyColumn(result, i));
            else if (result.IsEditable)
                grid.Columns.Add(EditableColumn(result, i));
            else
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = result.Columns[i].Name,
                    Binding = new Binding($"[{i}]") { Converter = CellDisplayConverter.Instance },
                });
        }
        grid.ItemsSource = result.Rows; // ObservableCollection → paged rows append without a rebuild

        if (result.IsEditable)
        {
            _editableGrids.Add((grid, result));
            grid.CellEditEnding += (_, e) =>
            {
                if (e.EditAction != DataGridEditAction.Commit) return;
                if (e.Row.DataContext is not object?[] row || e.Column.Tag is not int idx) return;
                if (e.EditingElement is TextBox tb && idx < row.Length) row[idx] = tb.Text;
                result.MarkEdited(row);
                e.Row.Background = RowBrush(result, row); // tint the edited row immediately
            };
        }
        return grid;
    }

    /// <summary>An editable column: a TextBlock display cell + a TextBox editing cell (shown on
    /// double-click / F2). The committed text is written back to the row in <c>CellEditEnding</c>.</summary>
    private static DataGridColumn EditableColumn(ResultSetViewModel result, int index)
        => new DataGridTemplateColumn
        {
            Header = result.Columns[index].Name,
            Tag = index, // column index, read back in CellEditEnding
            CellTemplate = new FuncDataTemplate<object?[]>((row, _) =>
            {
                var cell = new TextBlock
                {
                    Text = CellText(row, index),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                if (row is null || index >= row.Length || row[index] is null) // dim "(null)" as a marker
                {
                    cell.FontStyle = FontStyle.Italic;
                    cell.Foreground = NullBrush;
                }
                return cell;
            }),
            CellEditingTemplate = new FuncDataTemplate<object?[]>((row, _) => new TextBox
            {
                Text = CellText(row, index),
                Padding = new Thickness(3, 1),
                BorderThickness = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
            }),
        };

    private static string CellText(object?[]? row, int index)
        => row is not null && index < row.Length ? CellFormat.Display(row[index]) : "";

    /// <summary>Add an edit toolbar above the grid: Add row / Delete row, and — when there are pending
    /// changes — a count plus Save / Discard.</summary>
    private Control WithEditToolbar(Control content, DataGrid grid, ResultSetViewModel result)
    {
        var add = new Button { Content = "+ Add row", Margin = new Thickness(0, 0, 6, 0) };
        add.Click += (_, _) => { var row = result.AddRow(); grid.ScrollIntoView(row, null); RefreshRowColors(grid, result); };

        var delete = new Button { Content = "Delete row", Margin = new Thickness(0, 0, 12, 0) };
        delete.Click += (_, _) => { if (grid.SelectedItem is object?[] row) { result.ToggleDelete(row); RefreshRowColors(grid, result); } };

        var pending = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        pending.Bind(TextBlock.TextProperty, new Binding(nameof(ResultSetViewModel.PendingText)));

        var preview = new Button { Content = "Preview SQL", Margin = new Thickness(0, 0, 6, 0) };
        preview.Click += (_, _) => PreviewSql?.Invoke(result);

        var save = new Button { Content = "Save changes", Margin = new Thickness(0, 0, 6, 0) };
        save.Click += async (_, _) => { if (SaveChanges is { } f) await f(result); };

        var discard = new Button { Content = "Discard" };
        discard.Click += async (_, _) => { if (DiscardChanges is { } f) await f(result); };

        // The pending group only shows once there's something to save.
        var pendingGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        pendingGroup.Children.Add(pending);
        pendingGroup.Children.Add(preview);
        pendingGroup.Children.Add(save);
        pendingGroup.Children.Add(discard);
        pendingGroup.Bind(IsVisibleProperty, new Binding(nameof(ResultSetViewModel.HasPendingChanges)));

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(add);
        bar.Children.Add(delete);
        bar.Children.Add(pendingGroup);

        var toolbar = new Border
        {
            DataContext = result,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x88, 0x88, 0x88)),
            Padding = new Thickness(6, 4),
            Child = bar,
        };
        DockPanel.SetDock(toolbar, Dock.Top);

        var panel = new DockPanel();
        panel.Children.Add(toolbar);
        panel.Children.Add(content);
        return panel;
    }

    /// <summary>A foreign-key column: the value shows as plain text with a clickable jump-icon on the
    /// right that navigates to the referenced row.</summary>
    private DataGridColumn ForeignKeyColumn(ResultSetViewModel result, int index)
        => new DataGridTemplateColumn
        {
            Header = result.Columns[index].Name,
            CellTemplate = new FuncDataTemplate<object?[]>((row, _) =>
            {
                if (row is null) return new TextBlock();
                var hasValue = row.Length > index && row[index] is not null;

                var value = new TextBlock
                {
                    Text = CellText(row, index),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(4, 0, 4, 0),
                };

                // A drawn "↗" (vector Path, not a font glyph — symbol glyphs render clipped in the
                // app font). Wrapped in a transparent Border so the whole 16×16 box is the hit target.
                var arrow = new Path
                {
                    Data = Geometry.Parse("M1,9 L9,1 M4,1 L9,1 L9,6"),
                    Stroke = LinkBrush,
                    StrokeThickness = 1.4,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var jump = new Border
                {
                    Child = arrow,
                    Background = Brushes.Transparent,
                    Width = 16,
                    Height = 16,
                    Margin = new Thickness(2, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    IsVisible = hasValue,
                };
                ToolTip.SetTip(jump, "Open referenced row");
                jump.PointerPressed += async (_, _) =>
                {
                    if (hasValue && NavigateForeignKey is { } nav) await nav(result, index, row);
                };

                var cell = new DockPanel();
                DockPanel.SetDock(jump, Dock.Right);
                cell.Children.Add(jump);
                cell.Children.Add(value);
                return cell;
            }),
        };

    /// <summary>Wrap a grid in a DockPanel with a bottom footer: loaded-row text + Load more / Count.</summary>
    private Control WithFooter(Control grid, ResultSetViewModel result)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
        text.Bind(TextBlock.TextProperty, new Binding(nameof(ResultSetViewModel.FooterText)));

        var loadMore = new Button { Content = "Load more", Margin = new Thickness(6, 0, 0, 0) };
        loadMore.Bind(IsVisibleProperty, new Binding(nameof(ResultSetViewModel.HasMore)));
        loadMore.Click += async (_, _) => { if (LoadMore is { } f) await f(result); };

        var count = new Button { Content = "Count", Margin = new Thickness(6, 0, 0, 0) };
        count.Bind(IsVisibleProperty, new Binding(nameof(ResultSetViewModel.CanCount)));
        count.Click += async (_, _) => { if (CountTotal is { } f) await f(result); };

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(text);
        bar.Children.Add(loadMore);
        bar.Children.Add(count);

        var footer = new Border
        {
            DataContext = result,
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x88, 0x88, 0x88)),
            Padding = new Thickness(6, 4),
            Child = bar,
        };
        DockPanel.SetDock(footer, Dock.Bottom);

        var panel = new DockPanel();
        panel.Children.Add(footer);
        panel.Children.Add(grid);
        return panel;
    }
}
