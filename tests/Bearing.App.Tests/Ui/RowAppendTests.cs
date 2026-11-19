using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Rows appended to a shown grid (<c>ResultSetViewModel.AppendPage</c> — Load more, Fetch all, a streamed batch)
/// arrive as one notification per batch (<c>RowCollection</c>). One-per-row made the grid's view pay an
/// <c>IndexOf</c> over every loaded row for each new one: a 150,000-row stream pinned the UI thread for minutes.
/// </summary>
[Collection(UiTestCollection.Name)]
public class RowAppendTests
{
    private readonly UiTestSession _ui;

    public RowAppendTests(UiTestSession ui) => _ui = ui;

    private static object?[][] Rows(int from, int count)
        => Enumerable.Range(from, count).Select(n => new object?[] { n }).ToArray();

    private static double VerticalOffset(DataGrid grid)
        => grid.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == Orientation.Vertical).Value;

    /// <summary>The invariant, rather than a timing: one notification per batch. A timing threshold can't tell
    /// the two apart on a one-column headless grid (per-row took 14 s there, against minutes in the app).</summary>
    [Fact]
    public Task A_batch_reaches_the_grid_as_one_notification_and_all_its_rows_arrive() => _ui.Run(() =>
    {
        var rs = ResultsHarness.SingleColumn("n", "int4", typeof(int), primaryKey: false, 0);
        var (window, view) = ResultsHarness.Show(rs);
        ResultsHarness.Pump(window);
        var notifications = 0;
        rs.Rows.CollectionChanged += (_, _) => notifications++;

        for (var batch = 0; batch < 300; batch++)
        {
            rs.AppendPage(Rows(batch * 500 + 1, 500), hasMore: true);
            ResultsHarness.Pump(window);
        }

        Assert.Equal(300, notifications);
        Assert.Equal(150_001, rs.Loaded);
        Assert.Equal(150_001, ResultsHarness.Grid(view).ItemsSource!.Cast<object>().Count());
        Assert.Equal(Enumerable.Range(0, 150_001), rs.Rows.Select(r => (int)r[0]!));
    });

    /// <summary>The cost of one notification per batch is a <c>Reset</c>. It must not throw the reader back to
    /// the top — Load more fires while they are scrolled to the bottom.</summary>
    [Fact]
    public Task Appending_a_page_keeps_the_scroll_position() => _ui.Run(() =>
    {
        var rs = ResultsHarness.SingleColumn("n", "int4", typeof(int), primaryKey: false,
            Enumerable.Range(1, 200).Cast<object?>().ToArray());
        var (window, view) = ResultsHarness.Show(rs);
        var grid = ResultsHarness.Grid(view);
        ResultsHarness.Pump(window);
        grid.ScrollIntoView(rs.Rows[150], grid.Columns[0]);
        ResultsHarness.Pump(window);
        var before = VerticalOffset(grid);
        Assert.True(before > 0, "fixture must be scrolled");

        rs.AppendPage(Rows(201, 500), hasMore: true);
        ResultsHarness.Pump(window);

        Assert.Equal(before, VerticalOffset(grid));
    });
}
