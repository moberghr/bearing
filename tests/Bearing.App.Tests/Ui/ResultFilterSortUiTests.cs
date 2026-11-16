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
}
