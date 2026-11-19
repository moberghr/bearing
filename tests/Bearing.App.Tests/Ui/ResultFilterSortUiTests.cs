using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Bearing.App.Input;
using Bearing.App.Results;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Client-side sort and filter on a real results grid (#106, design v4). These assert what the controls do —
/// which rows the grid realizes, what is selected, where focus is — not how they look; that still needs an
/// eyeball (§4.3).
/// </summary>
[Collection(UiTestCollection.Name)]
public class ResultFilterSortUiTests
{
    private readonly UiTestSession _ui;

    public ResultFilterSortUiTests(UiTestSession ui) => _ui = ui;

    private static ResultColumnHeader Header(ResultView view, int column)
        => view.GetVisualDescendants().OfType<ResultColumnHeader>()
            .Single(h => h.FindAncestorOfType<DataGridColumnHeader>() is { } dh
                         && GridSelectionController.ColumnIndexOfHeader(ResultsHarness.Grid(view), dh) == column);

    private static void Press(Window window, Visual target, Point? at = null)
    {
        var p = target.TranslatePoint(at ?? new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("target is not connected to the window");
        window.MouseMove(p);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        ResultsHarness.Pump(window);
    }

    /// <summary>The row-number gutter's text for each realized row, top to bottom.</summary>
    private static string[] Gutter(ResultView view)
        => view.GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.IsVisible && r.Bounds.Height > 0)
            .OrderBy(r => r.TranslatePoint(default, view)!.Value.Y)
            .Select(r => r.Header as string ?? "")
            .ToArray();

    [Fact]
    public Task The_sort_indicator_cycles_the_sort_and_leaves_the_column_unselected() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult(columns: 3, rows: 12);
        var (window, view) = ResultsHarness.Show(rs);
        var header = Header(view, 0);
        Assert.NotNull(header.SortButton);

        Press(window, header.SortButton!);
        Assert.Equal(new RowSort(0, SortDirection.Ascending), rs.Filter.Sort);
        Assert.Empty(view.Selection.Model.Cells);           // the indicator is not a column-selecting click

        Press(window, header.SortButton!);
        Assert.Equal(SortDirection.Descending, rs.Filter.Sort!.Direction);
        // The grid shows the view: the highest id first, and the gutter numbers the view (1, 2, …), not ids.
        Assert.Same(rows[^1], rs.ViewRows[0]);
        Assert.Same(rs.ViewRows[0], ResultsHarness.Cell(view, rs.ViewRows[0], 0)!.Tag is ValueTuple<object?[], int> t ? t.Item1 : null);
        Assert.Equal(["1", "2", "3"], Gutter(view).Take(3));

        Press(window, header.SortButton!);
        Assert.Null(rs.Filter.Sort);
        Assert.Same(rows[0], rs.ViewRows[0]);
        window.Close();
    });

    [Fact]
    public Task A_header_click_beside_the_indicator_still_selects_the_column() => _ui.Run(() =>
    {
        var (rs, _) = ResultsHarness.WideEditableResult(columns: 3, rows: 12);
        var (window, view) = ResultsHarness.Show(rs);
        var header = Header(view, 1);

        Press(window, header, new Point(6, header.Bounds.Height / 2));   // on the name, left of the indicator

        Assert.Null(rs.Filter.Sort);
        Assert.Equal(12, view.Selection.Model.Cells.Count);
        Assert.All(view.Selection.Model.Cells, c => Assert.Equal(1, c.Col));
        window.Close();
    });

    [Fact]
    public Task Ctrl_F_in_the_grid_opens_the_bar_with_its_input_focused_and_typing_filters() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult(columns: 3, rows: 12);
        var (window, view) = ResultsHarness.Show(rs);
        var registry = new CommandRegistry();
        view.RegisterGridCommands(registry);
        view.CommandDispatcher = new KeyDispatcher(KeymapDefaults.Build(), registry);

        Press(window, ResultsHarness.RequireCell(view, rows[0], 1));
        var bar = view.GetVisualDescendants().OfType<ResultFilterBar>().Single();
        Assert.False(bar.IsVisible);

        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        window.KeyRelease(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        ResultsHarness.Pump(window);

        Assert.True(bar.IsVisible);
        Assert.True(bar.Input.IsFocused, "the quick-filter input should take the keyboard");

        window.KeyTextInput("r3c2");
        ResultsHarness.Pump(window);
        Assert.Equal("r3c2", rs.Filter.QuickFilter);
        Assert.Same(rows[2], Assert.Single(rs.ViewRows));
        Assert.Null(ResultsHarness.Cell(view, rows[0], 1));             // the hidden row isn't realized
        Assert.Empty(view.Selection.Model.Cells);                        // the filter change cleared it

        // Esc: clears the text, then closes the bar.
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "");
        ResultsHarness.Pump(window);
        Assert.Equal("", rs.Filter.QuickFilter);
        Assert.True(bar.IsVisible);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "");
        ResultsHarness.Pump(window);
        Assert.False(bar.IsVisible);
        Assert.Equal(12, rs.ViewRows.Count);
        window.Close();
    });

    [Fact]
    public Task The_column_filter_row_filters_marks_the_header_and_keeps_Enter_out_of_the_grid() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult(columns: 3, rows: 12);
        var (window, view) = ResultsHarness.Show(rs);
        rs.Filter.Open();
        rs.Filter.IsColumnFilterRowOpen = true;
        ResultsHarness.Pump(window);

        var header = Header(view, 0);
        Assert.True(header.FilterInput.IsVisible);
        Press(window, header.FilterInput);
        Assert.True(header.FilterInput.IsFocused);
        Assert.Empty(view.Selection.Model.Cells);           // typing into a header input doesn't select the column

        window.KeyTextInput(">= 10");
        ResultsHarness.Pump(window);
        Assert.Equal([10, 11, 12], rs.ViewRows.Select(r => (int)r[0]!).ToArray());

        var dot = header.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Width == 5 && b.Height == 5);
        Assert.True(dot.IsVisible);

        // Enter stays in the input: no cell editor opens, focus doesn't move.
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "");
        ResultsHarness.Pump(window);
        Assert.True(header.FilterInput.IsFocused);
        Assert.Null(ResultsHarness.CellEditor(ResultsHarness.Grid(view)));

        // The bar can't be Esc'd shut over an active column filter; ✕ clears it all.
        Assert.False(rs.Filter.Escape());
        rs.Filter.ClearAndClose();
        ResultsHarness.Pump(window);
        Assert.False(header.FilterInput.IsVisible);
        Assert.Equal(12, rs.ViewRows.Count);
        window.Close();
    });

    [Fact]
    public Task Filters_that_leave_no_loaded_row_show_the_empty_state() => _ui.Run(() =>
    {
        var (rs, _) = ResultsHarness.WideEditableResult(columns: 3, rows: 12);
        var (window, view) = ResultsHarness.Show(rs);
        rs.Filter.Open();
        rs.Filter.QuickFilter = "no such value anywhere";
        ResultsHarness.Pump(window);

        var message = view.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "No loaded rows match these filters.");
        Assert.True(message.IsEffectivelyVisible);

        rs.Filter.QuickFilter = "";
        ResultsHarness.Pump(window);
        Assert.False(message.IsEffectivelyVisible);
        window.Close();
    });

    [Fact]
    public Task The_sorted_column_carries_its_wash_and_others_do_not() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult(columns: 3, rows: 6);
        var (window, view) = ResultsHarness.Show(rs);
        rs.Filter.CycleSort(2);
        ResultsHarness.Pump(window);

        var sorted = ResultsHarness.RequireCell(view, rs.ViewRows[0], 2);
        var other = ResultsHarness.RequireCell(view, rs.ViewRows[0], 1);
        Assert.True(sorted.Background is ISolidColorBrush { Color.A: > 0 }, "the sorted column should be washed");
        Assert.True(other.Background is ISolidColorBrush { Color.A: 0 } or null, "other columns stay clear");
        window.Close();
    });

    /// <summary>
    /// Every header name is drawn with a real brush. The unsorted state set the name's Foreground to null, which
    /// in Avalonia is "no brush", not "inherit" — every unsorted header went blank, and nothing above looked.
    /// </summary>
    [Fact]
    public Task Header_names_stay_visible_sorted_or_not() => _ui.Run(() =>
    {
        var (rs, _) = ResultsHarness.WideEditableResult(columns: 3, rows: 6);
        var (window, view) = ResultsHarness.Show(rs);

        void AssertDrawn(string when)
        {
            foreach (var header in view.GetVisualDescendants().OfType<ResultColumnHeader>())
                Assert.True(header.Title.Foreground is ISolidColorBrush { Color.A: > 0 },
                    $"{when}: '{header.Title.Text}' has no visible brush ({header.Title.Foreground?.ToString() ?? "null"})");
        }

        AssertDrawn("unsorted");
        rs.Filter.CycleSort(1);
        ResultsHarness.Pump(window);
        AssertDrawn("sorted");
        rs.Filter.Sort = null;
        ResultsHarness.Pump(window);
        AssertDrawn("sorted then cleared");
        window.Close();
    });

    /// <summary>The indicator is geometry, the same on every machine, and it fits its box uncropped.</summary>
    [Fact]
    public Task The_indicator_is_drawn_and_fits_its_button() => _ui.Run(() =>
    {
        var (rs, _) = ResultsHarness.WideEditableResult(columns: 3, rows: 6);
        var (window, view) = ResultsHarness.Show(rs);
        var header = Header(view, 1);

        Assert.Same(ResultColumnHeader.GlyphFor(null), header.Glyph.Data);
        rs.Filter.CycleSort(1);
        ResultsHarness.Pump(window);
        Assert.Same(ResultColumnHeader.GlyphFor(new RowSort(1, SortDirection.Ascending)), header.Glyph.Data);
        rs.Filter.CycleSort(1);
        ResultsHarness.Pump(window);
        Assert.Same(ResultColumnHeader.GlyphFor(new RowSort(1, SortDirection.Descending)), header.Glyph.Data);

        var button = header.SortButton!;
        var at = header.Glyph.TranslatePoint(default, button)!.Value;
        Assert.InRange(at.X, 0, button.Bounds.Width - header.Glyph.Bounds.Width);
        Assert.InRange(at.Y, 0, button.Bounds.Height - header.Glyph.Bounds.Height);
        Assert.Equal(10, header.Glyph.Bounds.Height);
        window.Close();
    });

    // ---- header menu, loaded-only notice, wash beside another result's selection -------------------------

    private static MenuItem MenuItemNamed(MenuFlyout menu, string header)
        => menu.Items.OfType<MenuItem>().Single(i => (string)i.Header! == header);

    [Fact]
    public Task The_header_menu_sorts_and_clears_and_offers_only_what_changes_something() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult(columns: 3, rows: 6);
        var (window, _) = ResultsHarness.Show(rs);

        var menu = ResultColumnMenu.Build(rs, 1, frozenThroughHere: 2);
        Assert.False(MenuItemNamed(menu, "Clear sort").IsEnabled);   // nothing sorted yet
        MenuItemNamed(menu, "Sort descending").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(new RowSort(1, SortDirection.Descending), rs.Filter.Sort);
        Assert.Same(rows[^1], rs.ViewRows[0]);

        var again = ResultColumnMenu.Build(rs, 1, frozenThroughHere: 2);
        Assert.True(MenuItemNamed(again, "Sort ascending").IsEnabled);
        Assert.False(MenuItemNamed(again, "Sort descending").IsEnabled);   // already this way

        // From another column's header, Clear sort names the column it clears.
        var other = ResultColumnMenu.Build(rs, 2, frozenThroughHere: 3);
        MenuItemNamed(other, "Clear sort (“col1”)").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Null(rs.Filter.Sort);
        window.Close();
    });

    [Fact]
    public Task Sorting_a_part_fetched_result_says_it_covers_the_loaded_rows_only() => _ui.Run(() =>
    {
        var (rs, _) = ResultsHarness.WideEditableResult(columns: 3, rows: 6, pageable: true);
        rs.HasMore = true;
        var (window, view) = ResultsHarness.Show(rs);
        string? status = null;
        view.Status = s => status = s;

        Press(window, Header(view, 1).SortButton!);

        Assert.NotNull(status);
        Assert.StartsWith("Sorted by “col1” ascending over the loaded rows only (", status);

        status = null;
        rs.HasMore = false;
        Press(window, Header(view, 1).SortButton!);   // → descending, over every row: nothing to say
        Assert.Null(status);
        window.Close();
    });

    /// <summary>
    /// Turning a sort off when the load order already was the sorted order leaves the view's rows as they are —
    /// no reset re-realizes the cells. With the selection in another result (so this one's isn't cleared and
    /// notified), the wash stayed on the formerly sorted column.
    /// </summary>
    [Fact]
    public Task The_wash_follows_the_sort_while_another_result_holds_the_selection() => _ui.Run(() =>
    {
        var (a, aRows) = ResultsHarness.WideEditableResult(columns: 2, rows: 3);
        var (b, _) = ResultsHarness.WideEditableResult(columns: 2, rows: 3);
        var (window, view) = ResultsHarness.Show(a, b);
        Press(window, ResultsHarness.RequireCell(view, aRows[0], 1));
        Assert.Same(a, view.Selection.Model.Result);

        b.Filter.Sort = new RowSort(0, SortDirection.Ascending);   // ids are already ascending
        ResultsHarness.Pump(window);
        Assert.True(ResultsHarness.RequireCell(view, b.ViewRows[0], 0).Background is ISolidColorBrush { Color.A: > 0 });

        b.Filter.Sort = null;
        ResultsHarness.Pump(window);
        Assert.True(ResultsHarness.RequireCell(view, b.ViewRows[0], 0).Background is ISolidColorBrush { Color.A: 0 } or null,
            "the wash should leave with the sort");
        Assert.Same(a, view.Selection.Model.Result);                // the other result's selection is kept
        Assert.NotEmpty(view.Selection.Model.Cells);
        window.Close();
    });

    // ---- eyeball-QA regressions -----------------------------------------------------------------------

    /// <summary>The idle indicator hides again once the pointer leaves, after a full cycle back to unsorted.
    /// A click left the button holding focus, and "show while focused" kept it on screen for good.</summary>
    [Fact]
    public Task After_a_full_cycle_the_idle_indicator_hides_when_the_pointer_leaves() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult(columns: 3, rows: 6);
        var (window, view) = ResultsHarness.Show(rs);
        var header = Header(view, 1);

        for (var i = 0; i < 3; i++) Press(window, header.SortButton!);   // asc → desc → off
        Assert.Null(rs.Filter.Sort);
        Assert.False(header.SortButton!.IsFocused);

        var away = ResultsHarness.RequireCell(view, rows[^1], 3);
        window.MouseMove(away.TranslatePoint(new Point(4, 4), window)!.Value);
        ResultsHarness.Pump(window);
        Assert.Equal(0, header.SortButton.Opacity);
        window.Close();
    });

    private static double IndicatorGap(ResultColumnHeader header)
    {
        var dh = header.FindAncestorOfType<DataGridColumnHeader>()!;
        var right = header.SortButton!.TranslatePoint(new Point(header.SortButton.Bounds.Width, 0), dh)!.Value.X;
        return dh.Bounds.Width - right;
    }

    /// <summary>
    /// Double-click auto-fit (Width = Auto) fits once and stays put: the header copied its own width from the
    /// column, so under an auto-sized column the two fed each other — every layout pass, horizontal scrolling
    /// included, widened it — and the copied width lagged, leaving the indicator standing off the right edge.
    /// </summary>
    [Fact]
    public Task Auto_fit_is_stable_and_keeps_the_indicator_at_the_right_edge() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult(columns: 8, rows: 6);
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);
        var column = grid.Columns[1];
        Assert.InRange(IndicatorGap(Header(view, 1)), 0, 16);

        column.Width = DataGridLength.Auto;
        ResultsHarness.Pump(window);
        var fitted = column.ActualWidth;

        grid.ScrollIntoView(rows[0], grid.Columns[^1]);    // horizontal scrolling re-lays the headers out
        ResultsHarness.Pump(window);
        grid.ScrollIntoView(rows[0], grid.Columns[0]);
        ResultsHarness.Pump(window);
        column.Width = DataGridLength.Auto;                 // a second double-click
        ResultsHarness.Pump(window);
        ResultsHarness.Pump(window);

        Assert.Equal(fitted, column.ActualWidth, 1);
        Assert.InRange(IndicatorGap(Header(view, 1)), 0, 16);
        window.Close();
    });

    private static void Key_(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, "");
        window.KeyRelease(key, RawInputModifiers.None, physical, "");
        ResultsHarness.Pump(window);
    }

    /// <summary>Escape in the grid steps back a layer at a time: the selection, then the filters (the bar's
    /// clear-and-close; the sort stays). It also works when the filter left no row on screen.</summary>
    [Fact]
    public Task Escape_in_the_grid_clears_the_selection_then_the_filters() => _ui.Run(() =>
    {
        var (rs, rows) = ResultsHarness.WideEditableResult(columns: 3, rows: 6);
        var (window, view) = ResultsHarness.Show(rs);
        var registry = new CommandRegistry();
        view.RegisterGridCommands(registry);
        view.CommandDispatcher = new KeyDispatcher(KeymapDefaults.Build(), registry);
        var grid = ResultsHarness.Grid(view);

        rs.Filter.Open();
        rs.Filter.SetColumnFilter(1, "r");          // every row still matches
        rs.Filter.Sort = new RowSort(0, SortDirection.Descending);
        ResultsHarness.Pump(window);
        Press(window, ResultsHarness.RequireCell(view, rs.ViewRows[0], 1));
        Assert.True(grid.IsKeyboardFocusWithin);
        Assert.NotEmpty(view.Selection.Model.Cells);

        Key_(window, Key.Escape, PhysicalKey.Escape);
        Assert.Empty(view.Selection.Model.Cells);
        Assert.True(rs.Filter.IsFiltered);           // one layer per press

        Key_(window, Key.Escape, PhysicalKey.Escape);
        Assert.False(rs.Filter.IsFiltered);
        Assert.False(rs.Filter.IsFilterOpen);
        Assert.NotNull(rs.Filter.Sort);              // the sort has its own control

        // A filter that leaves nothing on screen: Escape still reaches it.
        rs.Filter.Open();
        rs.Filter.SetColumnFilter(1, "no such value");
        ResultsHarness.Pump(window);
        Assert.Empty(rs.ViewRows);
        grid.Focus();
        ResultsHarness.Pump(window);
        Key_(window, Key.Escape, PhysicalKey.Escape);
        Assert.False(rs.Filter.IsFiltered);
        Assert.Equal(6, rs.ViewRows.Count);
        window.Close();
    });

    /// <summary>The meta row's and the bar's icons are drawn geometry, not font glyphs that render clipped.</summary>
    [Fact]
    public Task The_filter_and_fetch_buttons_draw_their_icons() => _ui.Run(() =>
    {
        var (rs, _) = ResultsHarness.WideEditableResult(columns: 3, rows: 6);
        var (window, view) = ResultsHarness.Show(rs);
        rs.Filter.Open();
        rs.Filter.QuickFilter = "r1";
        ResultsHarness.Pump(window);

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.DoesNotContain(texts, t => t.Contains('⌕') || t.Contains('⤓') || t.Contains('✕'));
        var icons = view.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .Where(p => p.IsEffectivelyVisible && p.Data is not null && p.Stroke is not null).ToList();
        Assert.Contains(icons, p => ReferenceEquals(p.Data, Application.Current!.FindResource("Icon.FilterClear")));
        window.Close();
    });
}

