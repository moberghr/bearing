using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Bearing.App.Results;
using Bearing.App.Tests.Demo;
using Bearing.App.ViewModels;
using Bearing.App.Formatting;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The payoff of the demo fixtures (#63) meeting the headless harness (#62): a whole run rendered by the real
/// <c>ResultView</c>, with no Postgres and nothing hand-set on the view models.
/// <para>
/// The distinction that matters is where the affordances come from. These tests do not tell the view a column
/// is a foreign key — they declare the column's origin in <see cref="DemoData"/>'s catalog and let
/// <see cref="ResultSetBuilder"/> and the resolvers work it out, which is the path the app takes.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class DemoRenderTests
{
    private readonly UiTestSession _ui;

    public DemoRenderTests(UiTestSession ui) => _ui = ui;

    /// <summary>Build view models the way the app does — through the real builder, over the demo catalog.</summary>
    internal static List<ResultSetViewModel> Sets(string sql, params QueryResult[] results)
        => ResultSetBuilder.BuildResultSets(results, sql, DemoData.Snapshot());

    [Fact]
    public Task A_whole_demo_run_renders() => _ui.Run(() =>
    {
        var sets = Sets("select * from shop.store; select * from shop.payment", [.. DemoData.Run()]);
        var (window, view) = ResultsHarness.Show([.. sets]);

        // Every set in the run got a container, the grids among them included.
        var grids = view.GetVisualDescendants().OfType<DataGrid>().ToList();
        Assert.Equal(sets.Count(s => s.HasGrid), grids.Count);
        Assert.All(grids, g => Assert.True(g.Bounds.Height > 0, "a grid rendered with no height"));
        window.Close();
    });

    [Fact]
    public Task A_null_in_a_foreign_key_column_is_drawn_as_a_null() => _ui.Run(() =>
    {
        // #61, off the fixtures rather than a hand-set ForeignKeyColumns: payment.store_id is a real FK in the
        // demo catalog and every third row has none.
        var payments = Sets("select * from shop.payment", DemoData.Payments())[0];
        Assert.Contains(1, payments.ForeignKeyColumns);
        var (window, _) = ResultsHarness.Show(payments);

        // Row 3 is the first unattributed payment, so its store_id renders as the null token — dimmed and
        // italic, which is #61's actual complaint.
        var cell = ResultsHarness.RequireCell(window, payments.Rows[2], column: 1);
        var text = ResultsHarness.CellText(cell);
        Assert.Equal(CellFormat.NullToken, text.Text);
        Assert.Equal(FontStyle.Italic, text.FontStyle);
        window.Close();
    });

    [Fact]
    public Task A_wide_value_and_a_wide_column_name_both_fit() => _ui.Run(() =>
    {
        // #30 / #73 in both directions: a column whose name is far wider than its values, beside one whose
        // values are far wider than its name.
        var metrics = Sets("select * from shop.metric", DemoData.Metrics())[0];
        var (window, view) = ResultsHarness.Show(metrics);

        var grid = ResultsHarness.Grid(view);
        var wideName = grid.Columns[1];
        var wideValues = grid.Columns[2];
        Assert.True(wideName.ActualWidth > wideValues.ActualWidth * 0.5,
            $"the long column name got {wideName.ActualWidth}px against {wideValues.ActualWidth}px of values");
        // 110122 is the value that used to clip to 11012 (#73).
        Assert.Equal("110122", ResultsHarness.CellText(ResultsHarness.RequireCell(window, metrics.Rows[1], 1)).Text);
        window.Close();
    });

    [Fact]
    public Task An_error_result_renders_without_a_grid() => _ui.Run(() =>
    {
        var failure = Sets("select * from shop.paymnet", DemoData.Failure())[0];
        var (window, view) = ResultsHarness.Show(failure);

        Assert.Empty(view.GetVisualDescendants().OfType<DataGrid>());
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text?.Contains("does not exist") == true);
        window.Close();
    });

    [Fact]
    public Task A_rows_affected_message_renders_without_a_grid() => _ui.Run(() =>
    {
        var affected = Sets("update shop.payment set note = null", DemoData.Affected())[0];
        var (window, view) = ResultsHarness.Show(affected);

        Assert.Empty(view.GetVisualDescendants().OfType<DataGrid>());
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text?.Contains("UPDATE 3") == true);
        window.Close();
    });

    [Fact]
    public Task An_editable_demo_result_shows_its_edit_controls() => _ui.Run(() =>
    {
        // Editability is resolved from the catalog, not declared by the test — the meta row's commit group
        // only appears because payment has a primary key the fixtures gave it.
        var payments = Sets("select * from shop.payment", DemoData.Payments())[0];
        Assert.True(payments.IsEditable);
        var (window, view) = ResultsHarness.Show(payments);

        Assert.Null(payments.LockReason);
        Assert.NotEmpty(view.GetVisualDescendants().OfType<DataGrid>());
        window.Close();
    });

    [Fact]
    public Task A_read_only_demo_result_says_why() => _ui.Run(() =>
    {
        // The view is a real relation with real column origins and no primary key, so the lock chip's reason
        // comes out of resolution rather than a string a test set.
        var receipts = Sets("select * from shop.receipt", DemoData.ReceiptView())[0];
        var (window, view) = ResultsHarness.Show(receipts);

        Assert.False(receipts.IsEditable);
        Assert.NotNull(receipts.LockReason);
        // The chip is a padlock, so the reason is its tooltip rather than visible text.
        var tips = view.GetVisualDescendants()
            .OfType<Border>()
            .Select(b => ToolTip.GetTip(b) as string)
            .Where(t => t is not null);
        Assert.Contains(tips, t => t!.Contains(receipts.LockReason!));
        window.Close();
    });
}
