using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using static Bearing.App.Controls.Tokens;
using Avalonia.Controls.Primitives;

namespace Bearing.App.Controls;

public sealed partial class ResultView
{
    // Editable grids currently rendered (grid + its result) — used to re-tint rows after an in-place save.
    private readonly List<(DataGrid Grid, ResultSetViewModel Result)> _editableGrids = new();
    // Every rendered result set → its grid, so a grid command invoked without a keystroke (the command
    // palette) can find the grid owning the current selection. Rebuilt on every render.
    private readonly Dictionary<ResultSetViewModel, DataGrid> _gridsByResult = new();
    // One stats bar per rendered result set, re-synced whenever the selection changes.
    private readonly List<QuickStatsBar> _statsBars = new();
    // Result sets the user has collapsed in stacked view (keyed by VM reference; new runs reset it).
    private readonly HashSet<ResultSetViewModel> _collapsed = new();

    private void SyncStatsBars()
    {
        foreach (var bar in _statsBars) bar.Sync();
    }

    /// <summary>Re-render the whole dock: back bar, header, body, and the inspector pane beside them. Called
    /// on a new result assignment and on a view-mode flip — never merely to open/close the inspector or
    /// re-tint rows, both of which would lose the grids' scroll position.</summary>
    private void Rebuild()
    {
        _editableGrids.Clear();
        _gridsByResult.Clear();
        _statsBars.Clear();
        _firstGrid = null;                    // re-captured as grids are built below (region-focus target)
        _selection.DropRestyleListeners();    // old cells are being discarded; they re-subscribe as they rebuild

        var results = _results;
        if (results is null || results.Count == 0) { Content = null; return; }

        var root = new DockPanel { LastChildFill = true };
        if (CanGoBack)
        {
            var back = ResultChrome.BackBar(() => GoBack?.Invoke());
            DockPanel.SetDock(back, Dock.Top);
            root.Children.Add(back);
        }

        var header = ResultChrome.DockHeader(ViewMode, mode =>
        {
            ViewMode = mode;               // triggers Rebuild (re-renders the toggle's active state)
            ViewModeChanged?.Invoke(mode); // persist on the VM
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(BuildBody(results));

        Content = _inspector.Wrap(root);
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
                    Header = ResultMetaText.TabHeader(i, results[i]),
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

        var (chevron, chevronGlyph) = ResultChrome.Chevron(_collapsed.Contains(result), collapsible);

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(chevron);
        if (result.HasGrid)
            AddLiveMeta(left, result, label);
        else
            left.Children.Add(new TextBlock
            {
                Text = ResultMetaText.Meta(label, result),
                Foreground = Res("Text.Dim"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });

        // Right of the meta row: subtle edit controls for an editable result, or a read-only lock chip
        // + reason for a locked one (design RESULTS_GRID §8). Undetermined results show neither.
        var metaRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        metaRow.Children.Add(left);
        Control? right = result.IsEditable && grid is not null ? BuildEditToolbar(result, grid)
            : result.LockReason is { } lockReason ? ResultChrome.LockChip(lockReason)
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
                chevronGlyph.Data = ResultChrome.ChevronGeometry(collapsed);
            };

        var bar = new Border
        {
            Background = Res("Bg.Chrome"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = SeparatorBrush,
            Padding = new Thickness(10, 5),
            Child = metaRow,
        };
        DockPanel.SetDock(bar, Dock.Top);

        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(bar);
        dock.Children.Add(body);
        return dock;
    }

    /// <summary>"Result · " (static) + a live "N rows · ms" bound to <see cref="ResultSetViewModel.MetaDetail"/>
    /// so the header count tracks infinite-scroll loads / count-on-demand, matching the status bar. A pageable
    /// set whose total is still unknown also gets a ∑ count button.</summary>
    private void AddLiveMeta(StackPanel left, ResultSetViewModel result, string? label)
    {
        left.Children.Add(new TextBlock
        {
            Text = $"{label ?? "Result"} · ",
            Foreground = Res("Text.Dim"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var detail = new TextBlock
        {
            Foreground = Res("Text.Dim"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            DataContext = result,
        };
        detail.Bind(TextBlock.TextProperty, new Binding(nameof(ResultSetViewModel.MetaDetail)));
        left.Children.Add(detail);

        if (!result.IsPageable) return;
        var countBtn = ResultChrome.SubtleButton("∑ count", "Count all rows");
        countBtn.Margin = new Thickness(6, 0, 0, 0);
        countBtn.DataContext = result;
        countBtn.Bind(Visual.IsVisibleProperty, new Binding(nameof(ResultSetViewModel.CanCount)));
        countBtn.Click += async (_, _) => { if (CountTotal is { } f) await f(result); };
        left.Children.Add(countBtn);
    }

    private Control BuildEditToolbar(ResultSetViewModel result, DataGrid grid)
        => ResultEditToolbar.Build(
            result, grid,
            onPreviewSql: () => PreviewSql?.Invoke(result),
            onSave: async () => { if (SaveChanges is { } f) await f(result); },
            onDiscard: async () => { if (DiscardChanges is { } f) await f(result); });
}
