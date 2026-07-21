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

    // Filled collapse-triangle geometries (drawn, not glyphs). Right = collapsed, down = expanded.
    private const string ChevronRight = "M0,0 L5,4 L0,8 Z";
    private const string ChevronDown = "M0,0 L8,0 L4,5 Z";

    /// <summary>Subtle in-grid cell separator (design §Results grid: 1px #252531 row + column dividers).</summary>
    private static readonly IBrush GridLine = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x31));

    /// <summary>Design row striping: a subtle neutral lift on alternate rows over the flat Bg.Editor body.</summary>
    private static readonly IBrush RowStripe = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x2E));

    /// <summary>Striped background per row parity — odd (0-based) rows lift, even rows stay transparent
    /// so the grid's flat Bg.Editor surface shows through.</summary>
    private static IBrush RowBackground(int rowIndex) => rowIndex % 2 == 1 ? RowStripe : Brushes.Transparent;

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
    // Keyboard navigation: the active ("cursor") cell that arrow keys move, and the anchor a Shift-range
    // extends from. Both belong to _selectionResult. Mouse clicks seed them; keys move them.
    private (object?[] Row, int Col)? _active;
    private (object?[] Row, int Col)? _selAnchor;
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
        // No pending change → the design row stripe; a pending edit/new/delete overrides with its tint.
        dgr.Background = ReferenceEquals(tint, Brushes.Transparent) ? RowBackground(dgr.Index) : tint;
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
        _firstGrid = null; // re-captured as grids are built below (region-focus target)
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

}
