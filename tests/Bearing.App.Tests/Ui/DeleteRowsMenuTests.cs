using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The results grid's Delete rows menu item: the right-click path to the action grid.delete (Delete) and the
/// toolbar already run. What only a realized grid answers is that the item is wired to the selection and
/// that a result the grid may not write (a read-only connection among them) does not offer it.
/// </summary>
[Collection(UiTestCollection.Name)]
public class DeleteRowsMenuTests
{
    private readonly UiTestSession _ui;

    public DeleteRowsMenuTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task Clicking_delete_rows_marks_the_selected_row_for_deletion() => _ui.Run(() =>
    {
        var result = ResultsHarness.ForeignKeyResult([[1, 7, "one"], [2, 8, "two"]], editable: true);
        var rows = result.Rows;
        var (window, view) = ResultsHarness.Show(result);
        var grid = ResultsHarness.Grid(view);

        view.Selection.MoveActive(grid, result, rows[1], col: 0, extend: false);
        ResultsHarness.Pump(window);
        Assert.Equal(0, result.PendingCount);

        var delete = Assert.IsType<MenuItem>(MenuItems(view).First(i => i is MenuItem { Header: "Delete rows" }));
        delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ResultsHarness.Pump(window);

        Assert.False(result.IsRowDeleted(rows[0]));
        Assert.True(result.IsRowDeleted(rows[1]));
        Assert.Equal(1, result.PendingCount);
        window.Close();
    });

    /// <summary>A read-only result offers no Delete rows, the rule Paste and Set value follow: a locked grid
    /// must not advertise a write it will refuse.</summary>
    [Fact]
    public Task A_read_only_result_has_no_delete_rows_item() => _ui.Run(() =>
    {
        var result = ResultsHarness.ForeignKeyResult([[1, 7, "one"]]);
        var (window, view) = ResultsHarness.Show(result);

        Assert.DoesNotContain(MenuItems(view), i => i is MenuItem { Header: "Delete rows" });
        window.Close();
    });

    private static IReadOnlyList<object?> MenuItems(Bearing.App.Controls.ResultView view)
        => Assert.IsType<MenuFlyout>(ResultsHarness.Grid(view).ContextFlyout).Items.ToList();
}
