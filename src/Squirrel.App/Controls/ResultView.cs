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
        set { _results = value; _inspect = null; ClearSelection(); Rebuild(); } // a new run resets inspector + stats
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
    private GridSplitter? _inspectorSplitter;
    private double _inspectorWidth = 400; // remembered across open/close (user can drag the splitter)

    /// <summary>Highlight brush for JSON nodes matching the inspector's find query.</summary>
    private static readonly FuncValueConverter<bool, IBrush> MatchHighlight =
        new(m => m ? Tint("Accent.Orange", 0x55) : Brushes.Transparent);

    // Numeric quick-stats: a set of selected measure cells keyed by (row reference, column index),
    // the result they belong to, a per-cell restyle notifier, and the stats bars to toggle/update.
    private readonly HashSet<(object?[] Row, int Col)> _selection = new();
    private ResultSetViewModel? _selectionResult;
    private Action? _cellRestyle; // each realized measure cell subscribes to re-apply its selection ring
    private readonly List<(ResultSetViewModel Result, Border Bar)> _statsBars = new();
    private bool _dragging;                              // a click-drag cell selection is in progress
    private (object?[] Row, int Col)? _dragAnchor;       // the cell the drag started from
    private readonly HashSet<ResultSetViewModel> _autoLoading = new(); // paging fetch in flight (infinite scroll)

    /// <summary>Fetch the next page when scrolled near the bottom (single-flight per result set).</summary>
    private void TriggerAutoLoad(ResultSetViewModel result)
    {
        if (LoadMore is not { } f || !result.HasMore || !_autoLoading.Add(result)) return;
        _ = LoadThenClear();
        async System.Threading.Tasks.Task LoadThenClear()
        {
            try { await f(result); } finally { _autoLoading.Remove(result); }
        }
    }

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
        _statsBars.Clear();
        _cellRestyle = null; // old cells are being discarded; they re-subscribe as they rebuild
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

        // The grid column fills; a draggable splitter + the inspector pane occupy two more columns that
        // collapse to 0-wide when the inspector is closed.
        var outer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(root, 0);
        outer.Children.Add(root);

        _inspectorSplitter = new GridSplitter
        {
            Width = 4,
            ResizeDirection = GridResizeDirection.Columns,
            Background = Separator,
            IsVisible = false,
        };
        Grid.SetColumn(_inspectorSplitter, 1);
        outer.Children.Add(_inspectorSplitter);

        _inspectorCol = outer.ColumnDefinitions[2];
        _inspectorHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(_inspectorHost, 2);
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
        var body = BuildResultSet(result, out var grid);
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

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(chevron);
        if (result.HasGrid)
        {
            // "Result · " (static) + live "N rows · ms" bound to MetaDetail so the header count tracks
            // infinite-scroll loads / count-on-demand, matching the footer and status bar.
            left.Children.Add(new TextBlock { Text = $"{label ?? "Result"} · ", Foreground = Res("Text.Dim"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var detail = new TextBlock { Foreground = Res("Text.Dim"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, DataContext = result };
            detail.Bind(TextBlock.TextProperty, new Binding(nameof(ResultSetViewModel.MetaDetail)));
            left.Children.Add(detail);

            // Count-on-demand (was the footer's ∑ Count): shown only while the total is unknown.
            if (result.IsPageable)
            {
                var countBtn = SubtleButton("∑ count", "Count all rows");
                countBtn.Margin = new Thickness(6, 0, 0, 0);
                countBtn.DataContext = result;
                countBtn.Bind(IsVisibleProperty, new Binding(nameof(ResultSetViewModel.CanCount)));
                countBtn.Click += async (_, _) => { if (CountTotal is { } f) await f(result); };
                left.Children.Add(countBtn);
            }
        }
        else
        {
            left.Children.Add(new TextBlock { Text = MetaText(label, result), Foreground = Res("Text.Dim"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        }

        // Right of the meta row: subtle edit controls for an editable result, or a read-only lock chip
        // + reason for a locked one (design RESULTS_GRID §8). Undetermined results show neither.
        var metaRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        metaRow.Children.Add(left);
        Control? right = result.IsEditable && grid is not null ? EditControls(result, grid)
            : result.LockReason is { } lockReason ? LockChip(lockReason)
            : null;
        if (right is not null)
        {
            Grid.SetColumn(right, 1);
            metaRow.Children.Add(right);
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
        body.Margin = new Thickness(0); // icon-only chip; reason lives in the tooltip
        var chip = new Border
        {
            Background = Tint("Accent.Orange", 0x1E),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Help),
            Child = body,
        };
        ToolTip.SetTip(chip, $"Read-only — {reason}");
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
        };
        ScrollViewer.SetAllowAutoHide(grid, false); // keep the scrollbar visible
        SuppressRowSelectionHighlight(grid);        // cell-level selection only — no whole-row blue bar
        ReserveScrollbarSpace(grid);                // inset content so the scrollbars don't cover data
        grid.LoadingRow += (_, e) =>
        {
            e.Row.Header = (e.Row.Index + 1).ToString();
            if (result.IsEditable) ApplyRowStatus(e.Row, result);
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

    /// <summary>A value display cell: text (dimmed italic "(null)", numeric in code color), plus an
    /// inspect (⤢) affordance for jsonb/json and any long/multiline value. Every value cell is
    /// selectable (single/drag/modifier-click); numeric selections drive the quick-stats bar.</summary>
    private IDataTemplate ValueCell(ResultSetViewModel result, int index, DataGrid grid)
    {
        var isJsonCol = IsJsonType(result.Columns[index].DataTypeName);
        var numeric = CellStats.IsNumeric(result.Columns[index].ClrType);
        return new FuncDataTemplate<object?[]>((row, _) =>
        {
            var isNull = row is null || index >= row.Length || row[index] is null;
            var text = new TextBlock
            {
                Text = CellText(row, index),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = isNull ? NullBrush : (numeric ? Res("Text.Code") : Res("Text.Primary")),
                FontStyle = isNull ? FontStyle.Italic : FontStyle.Normal,
            };

            Control inner = text;
            if (!isNull)
            {
                var raw = CellText(row, index);
                if (isJsonCol || raw.Length > 60 || raw.Contains('\n'))
                {
                    var expand = InspectAffordance();
                    // handledEventsToo: the DataGrid marks the press handled in the tunnel phase.
                    expand.AddHandler(PointerPressedEvent, (_, e) => { ShowInspector(result, index, row!); e.Handled = true; },
                        RoutingStrategies.Bubble, handledEventsToo: true);
                    var dock = new DockPanel { Margin = new Thickness(0, 0, 18, 0) }; // keep ⤢ clear of the scrollbar
                    DockPanel.SetDock(expand, Dock.Right);
                    dock.Children.Add(expand);
                    dock.Children.Add(text);
                    inner = dock;
                }
            }
            return MakeSelectable(inner, result, row, index, grid);
        });
    }

    /// <summary>Wrap a cell's content in a selectable border: single-click selects (blue ring) and
    /// starts a drag rectangle, Ctrl/Cmd/Shift-click toggles; the whole-row highlight stays invisible.
    /// Numeric selections feed the quick-stats bar; text selections just highlight.</summary>
    private Control MakeSelectable(Control inner, ResultSetViewModel result, object?[]? row, int index, DataGrid grid)
    {
        var border = new Border
        {
            Child = inner,
            Background = Brushes.Transparent,       // hit-testable across the whole cell
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(2),
        };
        if (row is null) return border; // nothing to key a selection on

        border.Tag = (row, index); // read back when a drag hit-tests the cell under the pointer

        void Restyle()
        {
            var selected = ReferenceEquals(_selectionResult, result) && _selection.Contains((row, index));
            border.Background = selected ? Tint("Syntax.Func", 0x2A) : Brushes.Transparent;
            border.BorderBrush = selected ? Res("Syntax.Func") : Brushes.Transparent;
            border.BorderThickness = new Thickness(selected ? 1 : 0);
        }
        Restyle();
        _cellRestyle += Restyle;
        border.DetachedFromVisualTree += (_, _) => _cellRestyle -= Restyle;

        border.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.ClickCount >= 2) return; // let the grid start editing on double-click
            if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
            var extend = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                || e.KeyModifiers.HasFlag(KeyModifiers.Meta)
                || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (extend)
            {
                ToggleCellSelection(result, row, index, extend: true);
            }
            else
            {
                _selectionResult = result;
                _selection.Clear();
                _selection.Add((row, index));
                _dragging = true;
                _dragAnchor = (row, index);
                e.Pointer.Capture(grid);
                SelectionChanged();
            }
            e.Handled = true;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        return border;
    }

    private static bool IsBoolColumn(Squirrel.Core.Data.ColumnDescriptor c)
        => (Nullable.GetUnderlyingType(c.ClrType) ?? c.ClrType) == typeof(bool);

    /// <summary>A boolean cell rendered as a checkbox: read-only display when the grid is locked,
    /// interactive (toggles the row value + marks it edited) when the result is editable.</summary>
    private IDataTemplate BoolCell(ResultSetViewModel result, int index)
        => new FuncDataTemplate<object?[]>((row, _) =>
        {
            var cb = new CheckBox
            {
                IsThreeState = true, // null → indeterminate
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = ToBool(row, index),
            };
            if (!result.IsEditable)
            {
                cb.IsHitTestVisible = false; // display only (not greyed like IsEnabled=false)
                cb.Focusable = false;
                return cb;
            }
            cb.IsCheckedChanged += (_, _) =>
            {
                if (row is null || index >= row.Length) return;
                if (Equals(row[index] as bool?, cb.IsChecked)) return; // no-op (e.g. initial bind)
                row[index] = cb.IsChecked;
                result.MarkEdited(row);
                if (cb.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault() is { } dgr)
                    ApplyRowStatus(dgr, result);
            };
            return cb;
        });

    private static bool? ToBool(object?[]? row, int index)
    {
        if (row is null || index >= row.Length) return null;
        return row[index] switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var b) => b,
            _ => null,
        };
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

    /// <summary>Subtle edit controls for the right of a result's meta row: ＋ Add / Delete / ⭳ Export
    /// (borderless), plus a pending commit group (● N pending · Script · Discard · Save) that appears
    /// only when there are unsaved changes.</summary>
    private Control EditControls(ResultSetViewModel result, DataGrid grid)
    {
        var add = SubtleButton("＋ Add", "Add row");
        add.Click += (_, _) => { var row = result.AddRow(); grid.ScrollIntoView(row, null); RefreshRowColors(grid, result); };

        var delete = SubtleButton("Delete", "Delete selected row");
        delete.Click += (_, _) => { if (grid.SelectedItem is object?[] row) { result.ToggleDelete(row); RefreshRowColors(grid, result); } };

        var export = SubtleButton("⭳ Export", "Export — coming soon"); // rendered; wired later (per decision)

        // Pending commit group: ● N pending · ‹ › Script · Discard (red outline) · ✓ Save (green fill).
        var dot = new TextBlock { Text = "●", Foreground = Res("Accent.Orange"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
        var pending = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 0, 6, 0), Foreground = Res("Text.Primary") };
        pending.Bind(TextBlock.TextProperty, new Binding(nameof(ResultSetViewModel.PendingText)));

        var script = SubtleButton("‹ › Script", "Preview the SQL a save would run");
        script.Click += (_, _) => PreviewSql?.Invoke(result);

        var discard = new Button
        {
            Content = "Discard", FontSize = 12, Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 6, 0),
            Background = Brushes.Transparent, BorderBrush = Res("Error.Red"), BorderThickness = new Thickness(1),
            Foreground = Res("Error.Red"), Cursor = new Cursor(StandardCursorType.Hand),
        };
        discard.Click += async (_, _) => { if (DiscardChanges is { } f) await f(result); };

        var save = new Button
        {
            Content = "✓ Save", FontSize = 12, Padding = new Thickness(8, 2),
            Background = Res("Ok.Green"), Foreground = Res("Bg.Editor"), Cursor = new Cursor(StandardCursorType.Hand),
        };
        save.Click += async (_, _) => { if (SaveChanges is { } f) await f(result); };

        var pendingGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, DataContext = result };
        pendingGroup.Children.Add(dot);
        pendingGroup.Children.Add(pending);
        pendingGroup.Children.Add(script);
        pendingGroup.Children.Add(discard);
        pendingGroup.Children.Add(save);
        pendingGroup.Bind(IsVisibleProperty, new Binding(nameof(ResultSetViewModel.HasPendingChanges)));

        var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(add);
        bar.Children.Add(delete);
        bar.Children.Add(export);
        bar.Children.Add(pendingGroup);
        return bar;
    }

    /// <summary>A borderless, dim, hand-cursor button for subtle inline actions.</summary>
    private static Button SubtleButton(string content, string tip)
    {
        var b = new Button
        {
            Content = content,
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2),
            Foreground = Res("Text.Dim"),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>A foreign-key column: the value shows as plain text with a clickable jump-icon on the
    /// right that navigates to the referenced row. In an editable result the value is also editable
    /// (double-click / F2) via the shared editor; the jump icon stays on the display cell.</summary>
    private DataGridColumn ForeignKeyColumn(ResultSetViewModel result, int index, DataGrid grid)
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
                // handledEventsToo: the DataGrid marks the press handled in the tunnel phase.
                jump.AddHandler(PointerPressedEvent, async (_, e) =>
                {
                    e.Handled = true;
                    if (hasValue && NavigateForeignKey is { } nav) await nav(result, index, row);
                }, RoutingStrategies.Bubble, handledEventsToo: true);

                var cell = new DockPanel { Margin = new Thickness(0, 0, 18, 0) }; // keep ↗ clear of the scrollbar
                DockPanel.SetDock(jump, Dock.Right);
                cell.Children.Add(jump);
                cell.Children.Add(value);
                return MakeSelectable(cell, result, row, index, grid);
            }),
        };

    // ---- Cell selection + quick-stats (design RESULTS_GRID §7) --------------------------------

    /// <summary>During a drag, hit-test the cell under the pointer and select the rectangle from the
    /// anchor to it (all selectable columns; bool checkbox columns are skipped).</summary>
    private void DragSelectTo(DataGrid grid, ResultSetViewModel result, PointerEventArgs e)
    {
        if (!_dragging || !ReferenceEquals(_selectionResult, result) || _dragAnchor is not { } anchor) return;
        if (grid.InputHitTest(e.GetPosition(grid)) is not Visual hit) return;
        var cell = hit.GetSelfAndVisualAncestors().OfType<Border>()
            .FirstOrDefault(b => b.Tag is ValueTuple<object?[], int>);
        if (cell?.Tag is not ValueTuple<object?[], int> target) return;

        var rows = result.Rows;
        int r0 = rows.IndexOf(anchor.Row), r1 = rows.IndexOf(target.Item1);
        if (r0 < 0 || r1 < 0) return;
        if (r0 > r1) (r0, r1) = (r1, r0);
        int c0 = Math.Min(anchor.Col, target.Item2), c1 = Math.Max(anchor.Col, target.Item2);

        _selection.Clear();
        for (var r = r0; r <= r1; r++)
        {
            var rr = rows[r];
            for (var c = c0; c <= c1; c++)
                if (c < rr.Length && !IsBoolColumn(result.Columns[c]))
                    _selection.Add((rr, c));
        }
        SelectionChanged();
    }

    private void ToggleCellSelection(ResultSetViewModel result, object?[] row, int index, bool extend)
    {
        if (!ReferenceEquals(_selectionResult, result)) { _selection.Clear(); _selectionResult = result; }
        var key = (row, index);
        if (extend) { if (!_selection.Remove(key)) _selection.Add(key); }
        else { _selection.Clear(); _selection.Add(key); }
        SelectionChanged();
    }

    private void ClearSelection()
    {
        _selection.Clear();
        _selectionResult = null;
    }

    /// <summary>Recompute the stats bars and re-apply every realized cell's selection ring.</summary>
    private void SelectionChanged()
    {
        foreach (var (result, bar) in _statsBars)
        {
            var show = ReferenceEquals(result, _selectionResult) && _selection.Count >= 2;
            if (show && CellStats.Aggregate(SelectedValues(result)) is { } stats)
            {
                bar.Child = BuildStatsContent(result, _selection.Count, stats);
                bar.IsVisible = true;
            }
            else
            {
                bar.IsVisible = false;
            }
        }
        _cellRestyle?.Invoke();
    }

    private IEnumerable<object?> SelectedValues(ResultSetViewModel result)
    {
        if (!ReferenceEquals(result, _selectionResult)) yield break;
        foreach (var (row, col) in _selection)
            if (col < row.Length) yield return row[col];
    }

    /// <summary>Wrap content with a bottom quick-stats bar (hidden until ≥2 measure cells are selected).</summary>
    private Control WithStatsBar(Control content, ResultSetViewModel result)
    {
        var bar = new Border
        {
            Background = Res("Bg.Hover"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = Separator,
            Padding = new Thickness(8, 4),
            IsVisible = false,
        };
        _statsBars.Add((result, bar));
        DockPanel.SetDock(bar, Dock.Bottom);

        var panel = new DockPanel();
        panel.Children.Add(bar);
        panel.Children.Add(content);
        return panel;
    }

    private Control BuildStatsContent(ResultSetViewModel result, int count, CellStatistics stats)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Stat($"{count} cells", "Text.Dim"));
        stack.Children.Add(Sep());
        stack.Children.Add(Stat($"count {stats.Count}", "Text.Primary"));
        stack.Children.Add(Sep());
        stack.Children.Add(Stat($"sum {CellStats.Format(stats.Sum)}", "Ok.Green")); // sum highlighted green
        stack.Children.Add(Sep());
        stack.Children.Add(Stat($"avg {CellStats.Format(stats.Avg)}", "Text.Primary"));
        stack.Children.Add(Sep());
        stack.Children.Add(Stat($"min {CellStats.Format(stats.Min)}", "Text.Primary"));
        stack.Children.Add(Sep());
        stack.Children.Add(Stat($"max {CellStats.Format(stats.Max)}", "Text.Primary"));

        var clear = IconTextButton("Clear", "Clear selection");
        clear.Margin = new Thickness(12, 0, 0, 0);
        clear.Click += (_, _) => { if (ReferenceEquals(_selectionResult, result)) { ClearSelection(); SelectionChanged(); } };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(stack, 0);
        Grid.SetColumn(clear, 1);
        grid.Children.Add(stack);
        grid.Children.Add(clear);
        return grid;

        static Control Stat(string text, string colorKey) => new TextBlock
        {
            Text = text, Foreground = Res(colorKey), VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
        };
        static Control Sep() => new TextBlock
        {
            Text = " · ", Foreground = Res("Text.Faint"), VerticalAlignment = VerticalAlignment.Center,
        };
    }

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
            _inspectorCol.Width = new GridLength(_inspectorWidth, GridUnitType.Pixel);
            _inspectorCol.MinWidth = 240;
            if (_inspectorSplitter is not null) _inspectorSplitter.IsVisible = true;
        }
        else
        {
            // Remember the dragged width before collapsing so re-opening keeps it.
            if (_inspectorCol.Width.IsAbsolute && _inspectorCol.Width.Value > 0) _inspectorWidth = _inspectorCol.Width.Value;
            _inspectorHost.Content = null;
            _inspectorCol.MinWidth = 0;
            _inspectorCol.Width = new GridLength(0);
            if (_inspectorSplitter is not null) _inspectorSplitter.IsVisible = false;
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
            Child = panel, // width comes from the resizable grid column
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

}
