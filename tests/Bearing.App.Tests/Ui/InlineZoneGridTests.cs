using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Bearing.App.Controls;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// An inline zone is the real results view (docs/specs/2026-11-06-inline-results.md §5–§6, §8, §10): a grid
/// that edits, sized to its rows up to a cap, no wider than the editor, pinned to its left edge, and the same
/// view after a tab switch. These assert realized sizes and state; how it looks is eyeball QA (§4.3).
/// </summary>
[Collection(UiTestCollection.Name)]
public class InlineZoneGridTests
{
    private readonly UiTestSession _ui;

    public InlineZoneGridTests(UiTestSession ui) => _ui = ui;

    internal static void Place(ShellHarness shell, EditorTabViewModel tab, params ResultSetViewModel[] sets)
    {
        var scope = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 1;", 0, isWholeBuffer: false);
        tab.ApplyInlineRun(scope, InlineResultPlacement.Assign(scope, sets));
        shell.Pump();
    }

    internal static ResultSetViewModel Editable(int rows = 3, int columns = 2)
        => ResultsHarness.WideEditableResult(columns, rows).Result;

    [Fact]
    public Task A_zone_shows_an_editable_grid_and_a_cell_edit_is_pending() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_zone_shows_an_editable_grid_and_a_cell_edit_is_pending));
        var (editor, tab) = InlineResultsTests.Setup(shell);
        var set = Editable();
        Place(shell, tab, set);

        var zone = Assert.Single(InlineResultsTests.Zones(editor));
        var grid = Assert.Single(zone.Results.Grids);
        Assert.False(grid.IsReadOnly);
        Assert.NotNull(ResultsHarness.Cell(zone, set.Rows[0], 1));   // a realized cell, as in the pane
        // The edit toolbar is the pane's: Save appears once something is pending.
        set.SetCell(set.Rows[0], 1, "changed");
        shell.Pump();
        Assert.True(set.HasPendingChanges);
        Assert.Contains(zone.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible
            && b.Content is string text && text.Contains("Save"));
    });

    [Fact]
    public Task A_short_result_is_shorter_than_the_cap_and_a_long_one_stops_at_it() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_short_result_is_shorter_than_the_cap_and_a_long_one_stops_at_it));
        var (editor, tab) = InlineResultsTests.Setup(shell);
        var row = ResultGridChrome.RowMinHeight;
        var cap = InlineGridSizing.DefaultCap(row);

        Place(shell, tab, Editable(rows: 3));
        var shortGrid = Assert.Single(Assert.Single(InlineResultsTests.Zones(editor)).Results.Grids);
        Assert.Equal(InlineGridSizing.GridHeight(3, row, cap), shortGrid.Bounds.Height, 1);

        Place(shell, tab, Editable(rows: 40));
        var longGrid = Assert.Single(Assert.Single(InlineResultsTests.Zones(editor)).Results.Grids);
        Assert.Equal(InlineGridSizing.GridHeight(40, row, cap), longGrid.Bounds.Height, 1);
        Assert.True(longGrid.Bounds.Height < 40 * row, "a long result stops at the cap");
        Assert.True(shortGrid.Bounds.Height < longGrid.Bounds.Height);
    });

    [Fact]
    public Task An_added_row_grows_the_grid() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(An_added_row_grows_the_grid));
        var (editor, tab) = InlineResultsTests.Setup(shell);
        var set = Editable(rows: 2);
        Place(shell, tab, set);
        var grid = Assert.Single(Assert.Single(InlineResultsTests.Zones(editor)).Results.Grids);
        var before = grid.Bounds.Height;

        set.AddRow();
        shell.Pump();

        Assert.Equal(before + ResultGridChrome.RowMinHeight, grid.Bounds.Height, 1);
    });

    [Fact]
    public Task Stacked_sets_each_get_the_cap() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Stacked_sets_each_get_the_cap));
        var (editor, tab) = InlineResultsTests.Setup(shell);
        shell.Vm.ResultsViewMode = ResultsViewMode.Stacked;
        Place(shell, tab, Editable(rows: 40), Editable(rows: 40));

        var grids = Assert.Single(InlineResultsTests.Zones(editor)).Results.Grids.ToList();
        var expected = InlineGridSizing.GridHeight(40, ResultGridChrome.RowMinHeight,
            InlineGridSizing.DefaultCap(ResultGridChrome.RowMinHeight));
        Assert.Equal(2, grids.Count);
        Assert.All(grids, g => Assert.Equal(expected, g.Bounds.Height, 1));
    });

    [Fact]
    public Task A_wide_result_is_no_wider_than_the_editor_and_a_narrow_one_fits_its_columns() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_wide_result_is_no_wider_than_the_editor_and_a_narrow_one_fits_its_columns));
        var (editor, tab) = InlineResultsTests.Setup(shell);
        var viewport = editor.TextArea.TextView.Bounds.Width;

        Place(shell, tab, Editable(columns: 30));
        var wide = Assert.Single(InlineResultsTests.Zones(editor));
        Assert.True(wide.Bounds.Width <= viewport, $"zone {wide.Bounds.Width}px in a {viewport}px editor");

        Place(shell, tab, Editable(columns: 1));
        var narrow = Assert.Single(InlineResultsTests.Zones(editor));
        Assert.True(narrow.Bounds.Width < wide.Bounds.Width, $"narrow {narrow.Bounds.Width}px vs wide {wide.Bounds.Width}px");
    });

    [Fact]
    public Task Scrolling_the_editor_sideways_leaves_the_zone_at_the_left_edge() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Scrolling_the_editor_sideways_leaves_the_zone_at_the_left_edge));
        var (editor, tab) = InlineResultsTests.Setup(shell, "select 1; -- " + new string('x', 600) + "\nselect 2;\n");
        Place(shell, tab, Editable());
        var zone = Assert.Single(InlineResultsTests.Zones(editor));
        var textView = editor.TextArea.TextView;
        var left = zone.TranslatePoint(new Point(0, 0), textView)!.Value.X;

        // The caret at the far end of the long line: the editor scrolls sideways to keep it in view.
        editor.CaretOffset = editor.Document.GetLineByNumber(1).EndOffset;
        editor.TextArea.Caret.BringCaretToView();
        shell.Pump();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        shell.Pump();

        Assert.True(textView.HorizontalOffset > 0,
            $"the editor did scroll (offset {textView.HorizontalOffset}, editor {editor.HorizontalOffset}, extent {editor.ExtentWidth}, viewport {editor.ViewportWidth})");
        var now = zone.TranslatePoint(new Point(0, 0), textView)!.Value.X;
        Assert.True(System.Math.Abs(now - left) < 1, $"zone left edge moved from {left} to {now}");   // sub-pixel rounding only
    });

    [Fact]
    public Task A_tab_switch_brings_back_the_same_view_with_its_selection() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_tab_switch_brings_back_the_same_view_with_its_selection));
        var (editor, tab) = InlineResultsTests.Setup(shell);
        var set = Editable();
        Place(shell, tab, set);
        var zone = Assert.Single(InlineResultsTests.Zones(editor));
        zone.Results.Selection.SelectAll(set);

        shell.Vm.Workspace.NewTab("select 42;");
        shell.Pump();
        Assert.Empty(InlineResultsTests.Zones(editor));
        shell.Vm.Workspace.SelectedTab = tab;
        shell.Pump();

        var back = Assert.Single(InlineResultsTests.Zones(editor));
        Assert.Same(zone, back);
        Assert.True(back.Results.Selection.HasSelection(set));
    });

    /// <summary>Flipping Stacked / Tabs rebuilds only the zones whose layout depends on it, and only once they
    /// are on screen: a single-set zone keeps its grid and selection, and a multi-set zone on another tab is
    /// left alone until it is shown again (spec §10, "comes back as it was left").</summary>
    [Fact]
    public Task Stacked_or_tabs_rebuilds_only_multi_set_zones_and_only_on_screen() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Stacked_or_tabs_rebuilds_only_multi_set_zones_and_only_on_screen));
        var (editor, tab) = InlineResultsTests.Setup(shell);
        shell.Vm.ResultsViewMode = ResultsViewMode.Stacked;
        var set = Editable();
        Place(shell, tab, set);
        var single = Assert.Single(InlineResultsTests.Zones(editor));
        var grid = Assert.Single(single.Results.Grids);
        single.Results.Selection.SelectAll(set);

        shell.Vm.ResultsViewMode = ResultsViewMode.Tabbed;
        shell.Pump();

        Assert.Same(grid, Assert.Single(single.Results.Grids));
        Assert.True(single.Results.Selection.HasSelection(set));

        Place(shell, tab, Editable(), Editable());
        var multi = Assert.Single(InlineResultsTests.Zones(editor));
        var content = multi.Results.Content;
        shell.Vm.Workspace.NewTab("select 42;");
        shell.Pump();

        shell.Vm.ResultsViewMode = ResultsViewMode.Stacked;
        shell.Pump();
        Assert.Same(content, multi.Results.Content);       // off screen: not rebuilt

        shell.Vm.Workspace.SelectedTab = tab;
        shell.Pump();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();   // the rebuild is posted, not run inside the measure pass
        shell.Pump();
        Assert.Same(multi, Assert.Single(InlineResultsTests.Zones(editor)));
        Assert.NotSame(content, multi.Results.Content);    // shown again: laid out in the new mode
        Assert.Equal(ResultsViewMode.Stacked, multi.Results.ViewMode);
    });

    /// <summary>A Stacked / Tabs flip and flip back while a zone is off screen leaves nothing to redo: shown
    /// again, it is the same content it was laid out with.</summary>
    [Fact]
    public Task A_mode_flipped_and_back_off_screen_does_not_rebuild_the_zone() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_mode_flipped_and_back_off_screen_does_not_rebuild_the_zone));
        var (editor, tab) = InlineResultsTests.Setup(shell);
        shell.Vm.ResultsViewMode = ResultsViewMode.Stacked;
        Place(shell, tab, Editable(), Editable());
        var multi = Assert.Single(InlineResultsTests.Zones(editor));
        var content = multi.Results.Content;
        shell.Vm.Workspace.NewTab("select 42;");
        shell.Pump();

        shell.Vm.ResultsViewMode = ResultsViewMode.Tabbed;
        shell.Pump();
        shell.Vm.ResultsViewMode = ResultsViewMode.Stacked;
        shell.Pump();
        shell.Vm.Workspace.SelectedTab = tab;
        shell.Pump();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        shell.Pump();

        Assert.Same(multi, Assert.Single(InlineResultsTests.Zones(editor)));
        Assert.Same(content, multi.Results.Content);
    });
}
