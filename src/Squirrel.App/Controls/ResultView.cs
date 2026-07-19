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
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Squirrel.App.Formatting;
using Squirrel.App.Results;
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
        set { _results = value; _inspect = null; Rebuild(); } // a new run closes any open inspector
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

    // Editable grids currently rendered (grid + its result) — used to re-tint rows after an in-place save.
    private readonly List<(DataGrid Grid, ResultSetViewModel Result)> _editableGrids = new();

    // Result sets the user has collapsed in stacked view (keyed by VM reference; new runs reset it).
    private readonly HashSet<ResultSetViewModel> _collapsed = new();

    // The cell currently open in the inspector pane (null = pane closed). Live controls for the pane
    // are held so it can open/close without a full Rebuild (which would reset the grid's scroll).
    private (ResultSetViewModel Result, int Index, object?[] Row)? _inspect;
    private ColumnDefinition? _inspectorCol;
    private ContentControl? _inspectorHost;

    /// <summary>Highlight brush for JSON nodes matching the inspector's find query.</summary>
    private static readonly FuncValueConverter<bool, IBrush> MatchHighlight =
        new(m => m ? Tint("Accent.Orange", 0x55) : Brushes.Transparent);

    /// <summary>Re-apply pending-change row highlights (call after an in-place save clears pending state).</summary>
    public void RefreshRowHighlights()
        => Dispatcher.UIThread.Post(() => { foreach (var (grid, result) in _editableGrids) RefreshRowColors(grid, result); });

    /// <summary>Pending-edit visuals for a row: a faint tint + a 2px left status bar
    /// (amber edited / green new / red deleted). Transparent when the row has no pending change.</summary>
    private static (IBrush Tint, IBrush Bar) RowStatus(ResultSetViewModel result, object?[]? row)
    {
        if (row is null) return (Brushes.Transparent, Brushes.Transparent);
        if (result.IsRowDeleted(row)) return (Tint("Error.Red", 0x2E), Res("Error.Red"));
        if (result.IsNewRow(row)) return (Tint("Ok.Green", 0x2E), Res("Ok.Green"));
        if (result.IsRowEdited(row)) return (Tint("Accent.Orange", 0x24), Res("Accent.Orange"));
        return (Brushes.Transparent, Brushes.Transparent);
    }

    private static void ApplyRowStatus(DataGridRow dgr, ResultSetViewModel result)
    {
        var (tint, bar) = RowStatus(result, dgr.DataContext as object?[]);
        dgr.Background = tint;
        dgr.BorderBrush = bar;
        dgr.BorderThickness = new Thickness(2, 0, 0, 0);
    }

    /// <summary>Re-tint the currently-realized rows to reflect pending edit/new/delete state.</summary>
    private static void RefreshRowColors(DataGrid grid, ResultSetViewModel result)
    {
        foreach (var dgr in grid.GetVisualDescendants().OfType<DataGridRow>())
            ApplyRowStatus(dgr, result);
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

        // The grid column fills; the inspector pane occupies a second column that's 0-wide when closed.
        var outer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(root, 0);
        outer.Children.Add(root);

        _inspectorCol = outer.ColumnDefinitions[1];
        _inspectorHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(_inspectorHost, 1);
        outer.Children.Add(_inspectorHost);
        RenderInspector(); // re-open the pane if a rebuild happened while it was showing

        Content = outer;
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

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(chevron);
        left.Children.Add(meta);

        // Read-only results surface an explicit lock chip + reason (design RESULTS_GRID §8) instead of
        // silently rejecting edits. Editable results (and undetermined ones) show nothing here.
        var metaRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        metaRow.Children.Add(left);
        if (result.LockReason is { } lockReason)
        {
            var chip = LockChip(lockReason);
            Grid.SetColumn(chip, 1);
            metaRow.Children.Add(chip);
        }

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

    /// <summary>An amber "🔒 Read-only — reason" chip for a locked result (drawn padlock, not a glyph).</summary>
    private static Control LockChip(string reason)
    {
        var amber = Res("Accent.Orange");
        // A small padlock: rounded body + shackle arc (vector, to avoid emoji/glyph rendering issues).
        var body = new Path
        {
            Data = Geometry.Parse("M2,5 h7 v6 h-7 z M3.5,5 v-1.5 a2,2 0 0 1 4,0 v1.5"),
            Stroke = amber,
            StrokeThickness = 1.2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        };
        var text = new TextBlock
        {
            Text = $"Read-only — {reason}",
            Foreground = amber,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        inner.Children.Add(body);
        inner.Children.Add(text);

        var chip = new Border
        {
            Background = Tint("Accent.Orange", 0x1E),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = inner,
        };
        ToolTip.SetTip(chip, reason);
        return chip;
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
            if (result.IsEditable) ApplyRowStatus(e.Row, result);
        };
        for (var i = 0; i < result.Columns.Count; i++)
        {
            // FK columns keep their (read-only) jump-icon template; other columns are editable text
            // (an explicit template-column editor — indexer-bound DataGridTextColumns don't edit reliably).
            DataGridColumn col;
            if (result.ForeignKeyColumns.Contains(i))
                col = ForeignKeyColumn(result, i);
            else if (result.IsEditable)
                col = new DataGridTemplateColumn
                {
                    Tag = i, // column index, read back in CellEditEnding
                    CellTemplate = ValueCell(result, i),
                    CellEditingTemplate = CellEditor(i),
                };
            else
                col = new DataGridTemplateColumn { CellTemplate = ValueCell(result, i) };
            col.Header = BuildColumnHeader(result, i); // name + PK/FK/type badges
            grid.Columns.Add(col);
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
                ApplyRowStatus(e.Row, result); // tint + status bar on the edited row immediately
            };
        }
        return grid;
    }

    /// <summary>A value display cell: text (dimmed italic "(null)"), plus an inspect (⤢) affordance for
    /// jsonb/json columns and any long/multiline value — clicking it opens the cell inspector pane.</summary>
    private IDataTemplate ValueCell(ResultSetViewModel result, int index)
    {
        var isJsonCol = IsJsonType(result.Columns[index].DataTypeName);
        return new FuncDataTemplate<object?[]>((row, _) =>
        {
            var text = new TextBlock
            {
                Text = CellText(row, index),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (row is null || index >= row.Length || row[index] is null) // dim "(null)" as a marker
            {
                text.FontStyle = FontStyle.Italic;
                text.Foreground = NullBrush;
                return text;
            }

            var raw = CellText(row, index);
            var inspectable = isJsonCol || raw.Length > 60 || raw.Contains('\n');
            if (!inspectable) return text;

            var expand = InspectAffordance();
            expand.PointerPressed += (_, _) => ShowInspector(result, index, row);
            var cell = new DockPanel();
            DockPanel.SetDock(expand, Dock.Right);
            cell.Children.Add(expand);
            cell.Children.Add(text);
            return cell;
        });
    }

    private static bool IsJsonType(string dataTypeName)
        => string.Equals(dataTypeName, "jsonb", StringComparison.OrdinalIgnoreCase)
        || string.Equals(dataTypeName, "json", StringComparison.OrdinalIgnoreCase);

    /// <summary>A small drawn "⤢" inspect icon (vector, not a glyph) that opens the cell inspector.</summary>
    private static Control InspectAffordance()
    {
        var arrow = new Path
        {
            Data = Geometry.Parse("M1,6 V1 H6 M1,1 L6,6 M13,8 V13 H8 M13,13 L8,8"),
            Stroke = Res("Syntax.Table"),
            StrokeThickness = 1.3,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new Border
        {
            Child = arrow,
            Background = Brushes.Transparent,
            Width = 18,
            Height = 16,
            Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(box, "Inspect value");
        return box;
    }

    /// <summary>Column header = the name plus inline type badges: orange PK, purple FK, teal jsonb/json
    /// (design RESULTS_GRID §3). Badges are 9px/700 tinted chips after the name.</summary>
    private static Control BuildColumnHeader(ResultSetViewModel result, int index)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = result.Columns[index].Name, VerticalAlignment = VerticalAlignment.Center });

        if (result.PrimaryKeyColumns.Contains(index)) row.Children.Add(Badge("PK", "Accent.Orange"));
        if (result.ForeignKeyColumns.Contains(index)) row.Children.Add(Badge("FK", "Syntax.Keyword"));

        var type = result.Columns[index].DataTypeName;
        if (string.Equals(type, "jsonb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "json", StringComparison.OrdinalIgnoreCase))
            row.Children.Add(Badge(type.ToLowerInvariant(), "Syntax.Table"));

        return row;
    }

    private static Control Badge(string text, string colorKey)
        => new Border
        {
            Background = Tint(colorKey, 0x33),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0),
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 9,
                FontWeight = FontWeight.Bold,
                Foreground = Res(colorKey),
            },
        };

    /// <summary>The in-cell editor (a TextBox seeded with the current value) shared by editable and FK
    /// columns. A template — re-materialized per row as the grid recycles containers on scroll —
    /// deliberately NOT a <see cref="DataGridTextColumn"/> with an indexer binding (<c>[i]</c>): that
    /// binding doesn't re-evaluate when a row container is reused (Avalonia DataGrid recycling, #17534).</summary>
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

    // ---- Cell inspector (large-value / JSON viewer, design RESULTS_GRID §6) -------------------

    private void ShowInspector(ResultSetViewModel result, int index, object?[] row)
    {
        _inspect = (result, index, row);
        RenderInspector();
    }

    private void HideInspector()
    {
        _inspect = null;
        RenderInspector();
    }

    /// <summary>Populate (or clear) the live inspector pane without a full Rebuild — keeps grid scroll.</summary>
    private void RenderInspector()
    {
        if (_inspectorHost is null || _inspectorCol is null) return;
        if (_inspect is { } ins)
        {
            _inspectorHost.Content = BuildInspector(ins.Result, ins.Index, ins.Row);
            _inspectorCol.Width = new GridLength(400);
        }
        else
        {
            _inspectorHost.Content = null;
            _inspectorCol.Width = new GridLength(0);
        }
    }

    private Control BuildInspector(ResultSetViewModel result, int index, object?[] row)
    {
        var raw = CellText(row, index);
        var colName = result.Columns[index].Name;
        var typeName = result.Columns[index].DataTypeName;
        var parsed = JsonTree.Parse(raw);
        var isJson = parsed is not null && (IsJsonType(typeName) || LooksJson(raw));

        // Header: film[<id>].<column> + type badge + copy + close.
        var title = new TextBlock
        {
            Text = $"{result.EditTarget?.Table ?? "row"}[{KeyDisplay(result, row)}].{colName}",
            Foreground = Res("Text.Primary"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var typeBadge = Badge(isJson ? (IsJsonType(typeName) ? typeName.ToLowerInvariant() : "json") : "text",
            isJson ? "Syntax.Table" : "Text.Dim");

        var copy = IconTextButton("⧉", "Copy value");
        copy.Click += (_, _) => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(isJson ? JsonTree.Prettify(raw) : raw);
        var close = IconTextButton("✕", "Close");
        close.Click += (_, _) => HideInspector();

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        var titleWrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleWrap.Children.Add(title);
        titleWrap.Children.Add(typeBadge);
        Grid.SetColumn(titleWrap, 0);
        Grid.SetColumn(copy, 2);
        Grid.SetColumn(close, 3);
        headerRow.Children.Add(titleWrap);
        headerRow.Children.Add(copy);
        headerRow.Children.Add(close);
        var header = new Border
        {
            Background = Res("Bg.Window"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Separator,
            Padding = new Thickness(10, 6),
            Child = headerRow,
        };
        DockPanel.SetDock(header, Dock.Top);

        var bodyHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        var formatted = true;
        var find = "";

        void RenderBody()
        {
            if (isJson && formatted && parsed is not null)
            {
                JsonTree.ApplyFind(parsed, find);
                bodyHost.Content = new ScrollViewer { Content = BuildJsonTreeView(parsed), Padding = new Thickness(8) };
            }
            else
            {
                var box = new TextBox
                {
                    Text = raw,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = Res("Text.Code"),
                    Margin = new Thickness(8),
                };
                bodyHost.Content = new ScrollViewer { Content = box };
            }
        }

        // Toolbar (JSON only): Formatted/Raw toggle + collapse/expand all + find.
        Control? toolbar = null;
        if (isJson && parsed is not null)
        {
            var fmtToggle = IconTextButton("Formatted", "Show as tree");
            var rawToggle = IconTextButton("Raw", "Show raw text");
            void SyncToggles()
            {
                fmtToggle.Foreground = formatted ? Res("Accent.Orange") : Res("Text.Dim");
                rawToggle.Foreground = formatted ? Res("Text.Dim") : Res("Accent.Orange");
            }
            fmtToggle.Click += (_, _) => { formatted = true; SyncToggles(); RenderBody(); };
            rawToggle.Click += (_, _) => { formatted = false; SyncToggles(); RenderBody(); };
            SyncToggles();

            var collapseAll = IconTextButton("⊟", "Collapse all");
            collapseAll.Click += (_, _) => { JsonTree.SetExpandedAll(parsed, false); RenderBody(); };
            var expandAll = IconTextButton("⊞", "Expand all");
            expandAll.Click += (_, _) => { JsonTree.SetExpandedAll(parsed, true); RenderBody(); };

            var findBox = new TextBox { PlaceholderText = "Find in value…", Width = 150, Margin = new Thickness(8, 0, 0, 0) };
            findBox.TextChanged += (_, _) => { find = findBox.Text ?? ""; if (formatted) RenderBody(); };

            var tb = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            tb.Children.Add(fmtToggle);
            tb.Children.Add(rawToggle);
            tb.Children.Add(collapseAll);
            tb.Children.Add(expandAll);
            tb.Children.Add(findBox);
            toolbar = new Border
            {
                Background = Res("Bg.Chrome"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = Separator,
                Padding = new Thickness(8, 4),
                Child = tb,
            };
            DockPanel.SetDock(toolbar, Dock.Top);
        }

        RenderBody();

        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(header);
        if (toolbar is not null) panel.Children.Add(toolbar);
        panel.Children.Add(bodyHost);

        return new Border
        {
            Background = Res("Bg.Editor"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = Separator,
            Width = 400,
            Child = panel,
        };
    }

    private static bool LooksJson(string raw)
    {
        var t = raw.TrimStart();
        return t.StartsWith('{') || t.StartsWith('[');
    }

    /// <summary>The first primary-key value in the row (for the inspector header), or "?" if none.</summary>
    private static string KeyDisplay(ResultSetViewModel result, object?[] row)
    {
        foreach (var i in result.PrimaryKeyColumns)
            if (i < row.Length && row[i] is not null) return CellFormat.Display(row[i]);
        return "?";
    }

    private TreeView BuildJsonTreeView(JsonTreeNode root)
    {
        var tree = new TreeView { ItemsSource = new[] { root }, Background = Brushes.Transparent };
        tree.ItemTemplate = new FuncTreeDataTemplate<JsonTreeNode>(
            (n, _) => BuildJsonNodeVisual(n), n => n.Children);
        // Reflect each node's fold state (find/collapse-all drive it from the model).
        var style = new Style(x => x.OfType<TreeViewItem>());
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty,
            new Binding(nameof(JsonTreeNode.IsExpanded)) { Mode = BindingMode.TwoWay }));
        tree.Styles.Add(style);
        return tree;
    }

    private static Control BuildJsonNodeVisual(JsonTreeNode node)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (node.Key is not null)
        {
            line.Children.Add(new TextBlock { Text = node.Key, Foreground = Res("Syntax.Func") });      // key: blue
            line.Children.Add(new TextBlock { Text = ": ", Foreground = Res("Text.Dim") });
        }
        if (node.IsContainer)
            line.Children.Add(new TextBlock { Text = node.CollapsedSummary, Foreground = Res("Text.Dim") });
        else
        {
            var disp = node.Kind == JsonNodeKind.String ? $"\"{node.Value}\"" : node.Value ?? "null";
            line.Children.Add(new TextBlock { Text = disp, Foreground = Res(ColorKeyForKind(node.Kind)) });
        }

        var wrap = new Border { Child = line, CornerRadius = new CornerRadius(3), Padding = new Thickness(2, 0) };
        wrap.Bind(Border.BackgroundProperty, new Binding(nameof(JsonTreeNode.IsMatch)) { Converter = MatchHighlight });
        return wrap;
    }

    private static string ColorKeyForKind(JsonNodeKind kind) => kind switch
    {
        JsonNodeKind.String => "Ok.Green",
        JsonNodeKind.Number => "Syntax.Number",
        JsonNodeKind.Boolean or JsonNodeKind.Null => "Syntax.Keyword",
        _ => "Text.Primary",
    };

    /// <summary>A borderless text/glyph button used for inspector controls (copy, close, toggles).</summary>
    private static Button IconTextButton(string content, string tip)
    {
        var b = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2),
            Foreground = Res("Text.Dim"),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

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
