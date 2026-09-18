using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The results grid's Set value ▸ menu (#149, and the shape DBeaver's has). What only a realized grid can
/// answer is that the menu is <em>wired</em> — that the item the user clicks reaches the same
/// <c>SetCell</c> path typing the expression would, rather than being a label beside a callback nobody
/// connected. What each cell then does with the text is the pure plan's business
/// (<c>GridSelectionOpsTests</c>), which is where that is tested.
/// </summary>
[Collection(UiTestCollection.Name)]
public class SetValueMenuTests
{
    private readonly UiTestSession _ui;

    public SetValueMenuTests(UiTestSession ui) => _ui = ui;

    /// <summary>NULL first, then the expressions below a separator: the two are different claims (#33's one
    /// value that is not typeable as itself, versus SQL the server evaluates), under one item because they
    /// answer the same question — "put a value in these cells".</summary>
    [Fact]
    public Task The_menu_offers_null_and_then_the_expressions() => _ui.Run(() =>
    {
        var result = ResultsHarness.ForeignKeyResult([[1, 7, "one"]], editable: true);
        var (window, view) = ResultsHarness.Show(result);

        var items = SetValueItems(view);

        Assert.Equal("NULL", Assert.IsType<MenuItem>(items[0]).Header);
        Assert.IsType<Separator>(items[1]);
        Assert.Equal(
            EditExpression.Offered,
            items.Skip(2).Cast<MenuItem>().Select(i => (string)i.Header!).ToList());
        window.Close();
    });

    /// <summary>The wiring, end to end: the click reaches the selection and the cell holds the expression —
    /// which is what makes everything downstream (the function colour, the SQL in place of a parameter,
    /// the read-back) follow without the menu knowing about any of it.</summary>
    [Fact]
    public Task Clicking_an_expression_stages_it_in_the_selected_cell() => _ui.Run(() =>
    {
        // customer_id is an int column, so it is one the server would evaluate an expression for; note is
        // text, which is the cell the plan refuses (GridSelectionOpsTests covers that half).
        var result = ResultsHarness.ForeignKeyResult([[1, 7, "one"]], editable: true);
        var rows = result.Rows;
        var (window, view) = ResultsHarness.Show(result);
        var grid = ResultsHarness.Grid(view);

        view.Selection.MoveActive(grid, result, rows[0], col: 1, extend: false);
        ResultsHarness.Pump(window);
        Assert.Equal(0, result.PendingCount);

        var now = SetValueItems(view).OfType<MenuItem>().First(i => (string)i.Header! == "now()");
        now.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ResultsHarness.Pump(window);

        Assert.Equal("now()", rows[0][1]);
        Assert.Equal(1, result.PendingCount);
        window.Close();
    });

    /// <summary>A read-only result offers none of it, the rule Paste already follows: a locked grid must not
    /// advertise a write it will refuse.</summary>
    [Fact]
    public Task A_read_only_result_has_no_set_value_item() => _ui.Run(() =>
    {
        // Not editable: no EditTarget, which is what a result the grid may not write looks like.
        var result = ResultsHarness.ForeignKeyResult([[1, 7, "one"]]);
        var (window, view) = ResultsHarness.Show(result);

        Assert.DoesNotContain(
            MenuItems(view), i => i is MenuItem { Header: "Set value" });
        window.Close();
    });

    // ---- helpers ---------------------------------------------------------------------------------

    private static IReadOnlyList<object?> MenuItems(Bearing.App.Controls.ResultView view)
        => Assert.IsType<MenuFlyout>(ResultsHarness.Grid(view).ContextFlyout).Items.ToList();

    private static IReadOnlyList<object?> SetValueItems(Bearing.App.Controls.ResultView view)
        => Assert.IsType<MenuItem>(
               MenuItems(view).First(i => i is MenuItem { Header: "Set value" })).Items.ToList();
}
