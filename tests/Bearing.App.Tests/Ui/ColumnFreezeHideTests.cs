using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// #118 on a realized grid: the two claims no pure helper can hold — that the DataGrid actually stops
/// rendering a hidden column and actually freezes the leading ones, and that the change happens
/// <em>in place</em> rather than by rebuilding the view. The rules themselves are tested without a window in
/// <c>ColumnLayoutTests</c>.
/// </summary>
[Collection(UiTestCollection.Name)]
public class ColumnFreezeHideTests
{
    private readonly UiTestSession _ui;

    public ColumnFreezeHideTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task Hiding_a_column_takes_it_off_the_grid() => _ui.Run(() =>
    {
        var (result, rows) = ResultsHarness.WideEditableResult(columns: 4, rows: 3);
        var (window, view) = ResultsHarness.Show(result);
        var grid = ResultsHarness.Grid(view);

        Assert.NotNull(ResultsHarness.Cell(window, rows[0], 2));

        result.ColumnLayout.Hide(2);
        ResultsHarness.Pump(window);

        Assert.False(grid.Columns[2].IsVisible);
        Assert.True(grid.Columns[1].IsVisible);
        // The DataGrid recycles cell visuals rather than discarding them, so the assertion is that the cell
        // is no longer *shown* — which is what makes it unclickable, undraggable and unreachable by the cell
        // cursor. A test that asserted the visual was gone would pass or fail on the grid's recycling policy.
        Assert.False(ResultsHarness.RequireCell(window, rows[0], 2).IsEffectivelyVisible);
        Assert.True(ResultsHarness.RequireCell(window, rows[0], 1).IsEffectivelyVisible);
        window.Close();
    });

    [Fact]
    public Task Show_all_brings_the_column_and_its_cells_back() => _ui.Run(() =>
    {
        var (result, rows) = ResultsHarness.WideEditableResult(columns: 4, rows: 3);
        var (window, view) = ResultsHarness.Show(result);
        var grid = ResultsHarness.Grid(view);

        result.ColumnLayout.Hide(1);
        ResultsHarness.Pump(window);
        Assert.False(grid.Columns[1].IsVisible);

        result.ColumnLayout.ShowAll();
        ResultsHarness.Pump(window);

        Assert.True(grid.Columns[1].IsVisible);
        Assert.True(ResultsHarness.RequireCell(window, rows[0], 1).IsEffectivelyVisible);
        window.Close();
    });

    [Fact]
    public Task Freezing_reaches_the_grid_s_own_frozen_count() => _ui.Run(() =>
    {
        // Native support is the whole reason this is cheap (#118), so the assertion is that the layout state
        // arrives where the DataGrid reads it — not that a pane was drawn, which is eyeball QA (§0.2).
        var (result, _) = ResultsHarness.WideEditableResult(columns: 6, rows: 3);
        var (window, view) = ResultsHarness.Show(result);
        var grid = ResultsHarness.Grid(view);

        Assert.Equal(0, grid.FrozenColumnCount);

        result.ColumnLayout.Freeze(2);
        ResultsHarness.Pump(window);
        Assert.Equal(2, grid.FrozenColumnCount);

        result.ColumnLayout.Unfreeze();
        ResultsHarness.Pump(window);
        Assert.Equal(0, grid.FrozenColumnCount);
        window.Close();
    });

    [Fact]
    public Task Hiding_a_column_narrows_a_freeze_on_the_grid_too() => _ui.Run(() =>
    {
        // The clamp is not only bookkeeping: a DataGrid told to freeze more columns than it shows leaves
        // nothing scrollable.
        var (result, _) = ResultsHarness.WideEditableResult(columns: 2, rows: 3);   // id + 2 = 3 columns
        var (window, view) = ResultsHarness.Show(result);
        var grid = ResultsHarness.Grid(view);

        result.ColumnLayout.Freeze(2);
        ResultsHarness.Pump(window);
        Assert.Equal(2, grid.FrozenColumnCount);

        result.ColumnLayout.Hide(0);
        ResultsHarness.Pump(window);
        Assert.Equal(1, grid.FrozenColumnCount);
        window.Close();
    });

    [Fact]
    public Task The_meta_row_says_how_many_columns_are_hidden() => _ui.Run(() =>
    {
        // A hidden column may be an omission from what an export contains, but never a silent one.
        var (result, _) = ResultsHarness.WideEditableResult(columns: 4, rows: 3);
        var (window, _) = ResultsHarness.Show(result);

        Assert.DoesNotContain(Labels(window), l => l.Contains("hidden"));

        result.ColumnLayout.Hide(1);
        result.ColumnLayout.Hide(2);
        ResultsHarness.Pump(window);

        Assert.Contains("· 2 columns hidden", Labels(window));
        window.Close();
    });

    [Fact]
    public Task A_layout_change_keeps_the_same_grid_rather_than_rebuilding_it() => _ui.Run(() =>
    {
        // Rebuilding would drop the scroll position and any open inspector for what is meant to be a change
        // of view — and would lose the pending edits an editable result is holding.
        var (result, _) = ResultsHarness.WideEditableResult(columns: 4, rows: 3);
        var (window, view) = ResultsHarness.Show(result);
        var before = ResultsHarness.Grid(view);

        result.ColumnLayout.Hide(1);
        ResultsHarness.Pump(window);

        Assert.Same(before, ResultsHarness.Grid(view));
        window.Close();
    });

    /// <summary>The column menu is built for the header that was right-clicked, and its items reflect the
    /// state the layout is actually in.</summary>
    [Fact]
    public Task The_column_menu_offers_only_what_would_change_something() => _ui.Run(() =>
    {
        var (result, _) = ResultsHarness.WideEditableResult(columns: 4, rows: 3);
        var (window, _) = ResultsHarness.Show(result);

        var fresh = Items(ResultColumnMenu.Build(result, 1, frozenThroughHere: 2));
        Assert.True(fresh["Freeze through “col1”"]);
        Assert.False(fresh["Unfreeze columns"]);         // nothing frozen yet
        Assert.True(fresh["Hide “col1”"]);
        Assert.False(fresh["Show all columns"]);         // nothing hidden yet

        result.ColumnLayout.Freeze(2);
        result.ColumnLayout.Hide(3);
        var after = Items(ResultColumnMenu.Build(result, 1, frozenThroughHere: 2));

        Assert.False(after["Freeze through “col1”"]);     // already frozen exactly this far
        Assert.True(after["Unfreeze columns"]);
        Assert.True(after["Show all columns (1 column hidden)"]);
        window.Close();
    });

    [Fact]
    public Task The_menu_refuses_to_hide_the_last_visible_column() => _ui.Run(() =>
    {
        var (result, _) = ResultsHarness.WideEditableResult(columns: 1, rows: 2);   // id + col1
        var (window, _) = ResultsHarness.Show(result);

        result.ColumnLayout.Hide(0);
        var items = Items(ResultColumnMenu.Build(result, 1, frozenThroughHere: 1));

        Assert.False(items["Hide “col1”"]);
        window.Close();
    });

    // ---- regressions from the code review -------------------------------------------------------------

    /// <summary>
    /// "Freeze through here" is a count of leading <em>visible</em> columns. A hidden column keeps its place
    /// in <c>grid.Columns</c> and its <c>DisplayIndex</c>, so counting raw display indexes while
    /// <c>ColumnLayout.Freeze</c> clamps against the visible count froze fewer columns than the menu item
    /// said it would.
    /// </summary>
    [Fact]
    public Task Freeze_through_here_counts_visible_columns_only() => _ui.Run(() =>
    {
        var (result, _) = ResultsHarness.WideEditableResult(columns: 4, rows: 3);   // id + 4 = 5 columns
        var (window, view) = ResultsHarness.Show(result);
        var grid = ResultsHarness.Grid(view);

        // With nothing hidden, display order and source order agree.
        Assert.Equal(4, ResultColumnMenu.FreezeThrough(grid, 3));

        // Hide the first two, leaving three visible: source columns 2, 3, 4.
        result.ColumnLayout.Hide(0);
        result.ColumnLayout.Hide(1);
        ResultsHarness.Pump(window);

        // Right-clicking source column 3 — the *second* visible one — means "freeze two", not "freeze four".
        var through = ResultColumnMenu.FreezeThrough(grid, 3);
        Assert.Equal(2, through);

        result.ColumnLayout.Freeze(through);
        ResultsHarness.Pump(window);
        // Two of three visible columns is legal, so nothing is clamped away and the item did what it said.
        Assert.Equal(2, grid.FrozenColumnCount);
        window.Close();
    });

    /// <summary>
    /// The layout handler must not accumulate. <c>RebuildResults</c> runs on every tab switch over the same
    /// <c>ResultSetViewModel</c>, and the layout outlives the grid — so an untracked subscription left one
    /// dead closure per switch, each pinning a discarded DataGrid and its whole visual tree, and each
    /// re-applying the layout to a grid nobody can see.
    /// </summary>
    [Fact]
    public Task Re_rendering_does_not_accumulate_layout_handlers() => _ui.Run(() =>
    {
        var (result, _) = ResultsHarness.WideEditableResult(columns: 4, rows: 3);
        var (window, view) = ResultsHarness.Show(result);

        // Two to begin with, and both are meant to be there: the view model's own, which keeps the meta
        // row's "N columns hidden" marker in step for the result's whole life, and the live grid's.
        var baseline = result.ColumnLayout.ChangedSubscribers;
        Assert.Equal(2, baseline);

        // Ten re-renders of the same result set, as ten tab switches would do.
        for (var i = 0; i < 10; i++)
        {
            view.Results = [result];
            ResultsHarness.Pump(window);
        }

        // The count is what matters, not its value: one per render would be twelve by now, each pinning a
        // discarded DataGrid.
        Assert.Equal(baseline, result.ColumnLayout.ChangedSubscribers);

        // …and the one that remains is the live grid's: a hide still reaches it.
        result.ColumnLayout.Hide(2);
        ResultsHarness.Pump(window);
        Assert.False(ResultsHarness.Grid(view).Columns[2].IsVisible);
        window.Close();
    });

    private static System.Collections.Generic.Dictionary<string, bool> Items(MenuFlyout menu)
        => menu.Items.OfType<MenuItem>()
            .ToDictionary(i => (string)i.Header!, i => i.IsEnabled);

    private static System.Collections.Generic.List<string> Labels(Window window)
        => window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "")
            .Where(t => t.Length > 0)
            .ToList();
}
