using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Squirrel.App.Formatting;
using Squirrel.App.ViewModels;
using Squirrel.Core.Workspace;
using Path = Avalonia.Controls.Shapes.Path;

namespace Squirrel.App.Controls;

/// <summary>
/// Renders a query run's result sets inside the results dock (design RESULTS_GRID.md). Structure:
/// a persistent dock header (RESULTS label + Stacked/Tabbed toggle), then the body — result sets
/// stacked vertically (default) or as tabs. Each set has a meta row (Result · N rows · ms), a grid,
/// an optional edit toolbar (editable sets) and a paging footer (single SELECT). All styling binds
/// to the Kanagawa token brushes in Themes/Tokens.axaml. Self-contained: assign <see cref="Results"/>
/// and it rebuilds.
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

    private ResultsViewMode _viewMode = ResultsViewMode.Stacked;

    /// <summary>Stacked vs Tabbed presentation of multiple result sets. Set by the shell from the VM.</summary>
    public ResultsViewMode ViewMode
    {
        get => _viewMode;
        set { if (_viewMode == value) return; _viewMode = value; Rebuild(); }
    }

    /// <summary>Raised when the user flips the Stacked/Tabbed toggle (persist it on the VM).</summary>
    public Action<ResultsViewMode>? ViewModeChanged { get; set; }

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

    // ---- Token brush helpers (resolve from Themes/Tokens.axaml at build time) ----------------

    private static IBrush Res(string key)
        => (Application.Current?.FindResource(key) as IBrush) ?? Brushes.Transparent;

    /// <summary>A token color re-emitted at a given alpha (for faint row/selection tints).</summary>
    private static IBrush Tint(string key, byte alpha)
    {
        var c = (Res(key) as ISolidColorBrush)?.Color ?? Colors.Transparent;
        return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    private static IBrush LinkBrush => Res("Syntax.Func");   // FK jump-icon / back arrow
    private static IBrush NullBrush => Res("Text.Faint");    // dimmed "(null)" marker
    private static IBrush Separator => Res("Border");        // 1px region separators
    // Green for new/edited rows, red for rows pending deletion (kept visible until save).
    private static IBrush ChangedRowBrush => Tint("Ok.Green", 0x2E);
    private static IBrush DeletedRowBrush => Tint("Error.Red", 0x2E);

    // Editable grids currently rendered (grid + its result) — used to re-tint rows after an in-place save.
    private readonly List<(DataGrid Grid, ResultSetViewModel Result)> _editableGrids = new();

    // Result sets the user has collapsed in stacked view (keyed by VM reference; new runs reset it).
    private readonly HashSet<ResultSetViewModel> _collapsed = new();

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
        var results = _results;
        if (results is null || results.Count == 0) { Content = null; return; }

        var root = new DockPanel { LastChildFill = true };
        if (CanGoBack)
        {
            var back = BuildBackBar();
            DockPanel.SetDock(back, Dock.Top);
            root.Children.Add(back);
        }

        var header = BuildDockHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(BuildBody(results));
        Content = root;
    }

    // ---- Dock header: RESULTS label + Stacked/Tabbed toggle (always shown when there are results) ----

    private Control BuildDockHeader()
    {
        var label = new TextBlock
        {
            Text = "RESULTS",
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = Res("Text.Dim"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var toggle = BuildViewToggle();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(label);
        grid.Children.Add(toggle);

        return new Border
        {
            Background = Res("Bg.Window"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Separator,
            Padding = new Thickness(10, 5),
            Child = grid,
        };
    }

    /// <summary>Segmented Stacked/Tabbed control: active segment filled orange with dark text.</summary>
    private Control BuildViewToggle()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Segment("▤ Stacked", ResultsViewMode.Stacked));
        row.Children.Add(Segment("▭ Tabbed", ResultsViewMode.Tabbed));

        return new Border
        {
            Background = Res("Bg.Window"),
            BorderThickness = new Thickness(1),
            BorderBrush = Separator,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(2),
            Child = row,
        };
    }

    private Control Segment(string text, ResultsViewMode mode)
    {
        var active = ViewMode == mode;
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = active ? Res("Bg.Editor") : Res("Text.Dim"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var seg = new Border
        {
            Child = tb,
            Background = active ? Res("Accent.Orange") : Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 2),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        seg.PointerPressed += (_, _) =>
        {
            if (ViewMode == mode) return;
            ViewMode = mode;               // triggers Rebuild (re-renders the toggle's active state)
            ViewModeChanged?.Invoke(mode); // persist on the VM
        };
        return seg;
    }

    // ---- Body: single set, stacked, or tabbed ------------------------------------------------

    private Control BuildBody(IReadOnlyList<ResultSetViewModel> results)
    {
        if (results.Count == 1)
            return BuildSetContainer(results[0], "Result", collapsible: false, capHeight: false);

        if (ViewMode == ResultsViewMode.Tabbed)
        {
            var tabs = new TabControl { Padding = new Thickness(0) };
            for (var i = 0; i < results.Count; i++)
                tabs.Items.Add(new TabItem
                {
                    Header = TabHeader(i, results[i]),
                    Content = BuildSetContainer(results[i], null, collapsible: false, capHeight: false),
                });
            tabs.SelectedIndex = 0;
            return tabs;
        }

        // Stacked: every set vertically in one scroll area, each capped so you can scroll between them.
        var stack = new StackPanel { Spacing = 0 };
        for (var i = 0; i < results.Count; i++)
            stack.Children.Add(BuildSetContainer(results[i], $"Result {i + 1}", collapsible: true, capHeight: true));
        return new ScrollViewer
        {
            Content = stack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    /// <summary>A result set = meta row (Result · N rows · ms, optional collapse chevron) + its body.</summary>
    private Control BuildSetContainer(ResultSetViewModel result, string? label, bool collapsible, bool capHeight)
    {
        var body = BuildResultSet(result);
        if (capHeight)
            body = new Border { Child = body, MaxHeight = 360 };

        var chevron = new TextBlock
        {
            Text = _collapsed.Contains(result) ? "▸" : "▾",
            Foreground = Res("Text.Faint"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            IsVisible = collapsible,
            Cursor = collapsible ? new Cursor(StandardCursorType.Hand) : Cursor.Default,
        };

        var meta = new TextBlock
        {
            Text = MetaText(label, result),
            Foreground = Res("Text.Dim"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var metaRow = new StackPanel { Orientation = Orientation.Horizontal };
        metaRow.Children.Add(chevron);
        metaRow.Children.Add(meta);

        if (_collapsed.Contains(result)) body.IsVisible = false;

        if (collapsible)
            chevron.PointerPressed += (_, _) =>
            {
                var collapsed = !_collapsed.Remove(result);
                if (collapsed) _collapsed.Add(result);
                body.IsVisible = !collapsed;
                chevron.Text = collapsed ? "▸" : "▾";
            };

        var bar = new Border
        {
            Background = Res("Bg.Chrome"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Separator,
            Padding = new Thickness(10, 5),
            Child = metaRow,
        };
        DockPanel.SetDock(bar, Dock.Top);

        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(bar);
        dock.Children.Add(body);
        return dock;
    }

    /// <summary>Meta-row text: "Result · 10 rows · 88 ms", or the message/error for non-grid results.</summary>
    private static string MetaText(string? label, ResultSetViewModel result)
    {
        var name = label ?? "Result";
        if (!result.Success) return $"{name} · error: {result.Error?.Message}";
        if (result.Columns.Count == 0) return $"{name} · {result.Message ?? "Statement executed."}";
        var ms = (long)Math.Round(result.Duration.TotalMilliseconds);
        var rows = result.RowCount == 1 ? "1 row" : $"{result.RowCount} rows";
        return $"{name} · {rows} · {ms} ms";
    }

    /// <summary>Prepend a slim "Back" bar (returns to the pre-navigation result) above the body.</summary>
    private Control BuildBackBar()
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

        return new Border
        {
            Background = Res("Bg.Window"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Separator,
            Padding = new Thickness(6, 2),
            Child = back,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
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
                grid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = result.Columns[i].Name,
                    CellTemplate = DisplayCell(i),
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
            CellTemplate = DisplayCell(index),
            CellEditingTemplate = CellEditor(index),
        };

    /// <summary>Read-only display template for a value cell: plain text with a dimmed italic "(null)"
    /// marker. Shared by read-only and editable columns. This is a <see cref="DataGridTemplateColumn"/>
    /// template — re-materialized per row as the grid recycles containers on scroll — deliberately NOT a
    /// <see cref="DataGridTextColumn"/> with an indexer binding (<c>[i]</c>): that binding doesn't
    /// re-evaluate when a row container is reused for a new row, so recycled cells render the bare row
    /// object as "System.Object" (Avalonia DataGrid recycling, discussion #17534).</summary>
    private static IDataTemplate DisplayCell(int index)
        => new FuncDataTemplate<object?[]>((row, _) =>
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
        });

    /// <summary>The in-cell editor (a TextBox seeded with the current value) shared by editable and FK columns.</summary>
    private static IDataTemplate CellEditor(int index)
        => new FuncDataTemplate<object?[]>((row, _) => new TextBox
        {
            Text = CellText(row, index),
            Padding = new Thickness(3, 1),
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
        });

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
            Background = Res("Bg.Chrome"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Separator,
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
    /// right that navigates to the referenced row. In an editable result the value is also editable
    /// (double-click / F2) via the shared editor; the jump icon stays on the display cell.</summary>
    private DataGridColumn ForeignKeyColumn(ResultSetViewModel result, int index)
        => new DataGridTemplateColumn
        {
            Header = result.Columns[index].Name,
            Tag = index, // enables CellEditEnding capture when the grid is editable
            CellEditingTemplate = result.IsEditable ? CellEditor(index) : null,
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
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0), Foreground = Res("Text.Dim") };
        text.Bind(TextBlock.TextProperty, new Binding(nameof(ResultSetViewModel.FooterText)));

        var count = new Button { Content = "∑ Count", Margin = new Thickness(8, 0, 0, 0) };
        count.Bind(IsVisibleProperty, new Binding(nameof(ResultSetViewModel.CanCount)));
        count.Click += async (_, _) => { if (CountTotal is { } f) await f(result); };

        var loadMore = new Button { Content = "↓ Load more", Margin = new Thickness(6, 0, 0, 0) };
        loadMore.Bind(IsVisibleProperty, new Binding(nameof(ResultSetViewModel.HasMore)));
        loadMore.Click += async (_, _) => { if (LoadMore is { } f) await f(result); };

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(text);
        bar.Children.Add(count);
        bar.Children.Add(loadMore);

        var footer = new Border
        {
            DataContext = result,
            Background = Res("Bg.Window"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = Separator,
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
