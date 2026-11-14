using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Bearing.App.Results;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The header sort glyph (#106) on a realized grid: a click on it sorts and does <b>not</b> select the
/// column — the rest of the header still does (#6) — and the glyph lets go of the result when its grid is
/// discarded. The sort rules themselves are in <c>ResultSortTests</c>.
/// </summary>
[Collection(UiTestCollection.Name)]
public class SortGlyphTests
{
    private readonly UiTestSession _ui;

    public SortGlyphTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task Every_header_carries_a_glyph() => _ui.Run(() =>
    {
        var (result, _) = ResultsHarness.WideEditableResult(columns: 3, rows: 4);
        var (window, view) = ResultsHarness.Show(result);

        Assert.Equal(result.Columns.Count, Glyphs(view).Length);
        window.Close();
    });

    /// <summary>A selection made before the click is exactly the selection after it — the glyph sorts, it
    /// does not select its column the way the rest of the header does. The selected cell moves with its row.</summary>
    [Fact]
    public Task Clicking_the_glyph_sorts_and_leaves_the_selection_alone() => _ui.Run(() =>
    {
        var (result, rows) = ResultsHarness.WideEditableResult(columns: 3, rows: 4);
        var (window, view) = ResultsHarness.Show(result);
        var first = rows[0];
        view.Selection.SelectSingle(result, rows[1], 2);
        var before = view.Selection.Model.Cells.ToHashSet();

        Click(window, Glyphs(view)[0]);   // id: ascending — already the query's order
        Assert.Equal(SortDirection.Ascending, result.Sort.DirectionOf(0));
        Click(window, Glyphs(view)[0]);   // descending

        Assert.Equal(SortDirection.Descending, result.Sort.DirectionOf(0));
        Assert.Same(first, result.Rows[^1]);
        Assert.Equal(SortDirection.Descending, Glyphs(view)[0].Direction);
        Assert.Equal(before, view.Selection.Model.Cells.ToHashSet());
        window.Close();
    });

    /// <summary>The control for the claim above: the header itself, away from the glyph, still selects its
    /// column — so the empty selection there is the glyph's doing, not a header that stopped selecting.</summary>
    [Fact]
    public Task Clicking_the_header_name_still_selects_the_column() => _ui.Run(() =>
    {
        var (result, _) = ResultsHarness.WideEditableResult(columns: 3, rows: 4);
        var (window, view) = ResultsHarness.Show(result);

        var header = Glyphs(view)[1].FindAncestorOfType<DataGridColumnHeader>()
                     ?? throw new InvalidOperationException("the glyph is not in a header");
        var point = header.TranslatePoint(new Point(8, header.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException("the header is not in the window");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.UpdateLayout();

        Assert.Equal(result.Rows.Count, view.Selection.Model.Cells.Count);
        Assert.False(result.Sort.IsSorted);
        window.Close();
    });

    /// <summary>The glyph subscribes to the result's sort while attached; a closed window must leave the
    /// result with no handler pinning a discarded header (§9.9b).</summary>
    [Fact]
    public Task Glyphs_detach_from_the_result_with_their_grid() => _ui.Run(() =>
    {
        var (result, _) = ResultsHarness.WideEditableResult(columns: 3, rows: 4);
        var (window, _) = ResultsHarness.Show(result);
        Assert.Equal(result.Columns.Count, result.Sort.ChangedSubscribers);

        window.Close();

        Assert.Equal(0, result.Sort.ChangedSubscribers);
    });

    /// <summary>The column menu offers the same sort; "Clear sort" only once there is one to clear, and the
    /// direction already in force is not offered again.</summary>
    [Fact]
    public Task The_column_menu_sorts_through_the_same_route() => _ui.Run(() =>
    {
        var (result, _) = ResultsHarness.WideEditableResult(columns: 3, rows: 4);
        SortDirection? asked = null;

        var fresh = MenuItems(ResultColumnMenu.Build(result, 1, frozenThroughHere: 1, sort: d => asked = d));
        Assert.Equal(["Sort ascending", "Sort descending", "Clear sort"], fresh.Take(3).Select(i => (string)i.Header!));
        Assert.False(fresh[2].IsEnabled);

        result.SortBy(1, SortDirection.Ascending);
        var sorted = MenuItems(ResultColumnMenu.Build(result, 1, frozenThroughHere: 1, sort: d => asked = d));
        Assert.False(sorted[0].IsEnabled);
        Assert.True(sorted[2].IsEnabled);

        sorted[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(SortDirection.Descending, asked);
    });

    private static MenuItem[] MenuItems(MenuFlyout menu) => menu.Items.OfType<MenuItem>().ToArray();

    private static SortGlyph[] Glyphs(ResultView view) => view.GetVisualDescendants().OfType<SortGlyph>()
        .OrderBy(g => g.FindAncestorOfType<DataGridColumnHeader>()?.Bounds.X ?? 0)
        .ToArray();

    private static void Click(Window window, SortGlyph glyph)
    {
        var point = glyph.TranslatePoint(new Point(glyph.Bounds.Width / 2, glyph.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException("the glyph is not in the window");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        ResultsHarness.Pump(window);
    }
}
