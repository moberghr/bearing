using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Puts a real <see cref="ResultView"/> on a real (headless) window and hands back the realized cells.
/// <para>
/// The view is used the way the app uses it — assign <c>Results</c> and it rebuilds — rather than
/// re-assembling a DataGrid here. A harness that built its own grid would let <c>ResultView.BuildGrid</c>
/// drift out from under the tests, which is the one failure mode a UI test suite must not have.
/// </para>
/// <para>
/// The DataGrid virtualizes, so a cell only exists once the window has a size and a layout pass has run —
/// hence the explicit dimensions in <see cref="Show"/> and the pumped layout. Rows below the fold have no
/// visuals at all: keep fixtures small, or scroll before asserting.
/// </para>
/// </summary>
internal static class ResultsHarness
{
    /// <summary>Show the given result sets in a window big enough to realize them. Keep the window alive for
    /// the length of the assertions — closing it discards the visuals being asserted on.</summary>
    public static (Window Window, ResultView View) Show(params ResultSetViewModel[] results)
    {
        var view = new ResultView { Results = results };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Pump(window);
        return (window, view);
    }

    /// <summary>Run layout to completion, then drain the dispatcher queue, twice over. Both are needed: cell
    /// visuals appear in the layout pass, and the grid's own corrections (scroll-into-view, current-cell
    /// adoption) are posted at <see cref="DispatcherPriority.Loaded"/> and land in the frame after.</summary>
    public static void Pump(Window window)
    {
        for (var i = 0; i < 2; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        }
    }

    /// <summary>The selection border wrapping one realized cell, or null when that cell is not realized.
    /// Found by the (row, column) tag <c>ResultCellFactory.MakeSelectable</c> already stamps on it for drag
    /// hit-testing, so this needs no test-only hook in the production visual.</summary>
    public static Border? Cell(Visual root, object?[] row, int column)
        => root.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Tag is ValueTuple<object?[], int> tag
                && ReferenceEquals(tag.Item1, row) && tag.Item2 == column);

    /// <summary>The realized cell at (row, column), failing loudly rather than returning null — an
    /// un-realized cell is a harness problem (window too small, layout not pumped), not a result.</summary>
    public static Border RequireCell(Visual root, object?[] row, int column)
        => Cell(root, row, column)
           ?? throw new InvalidOperationException(
               $"No realized cell for column {column} of row [{string.Join(", ", row)}]. "
               + "The DataGrid virtualizes: give the window more room, or pump layout again.");

    /// <summary>The text a realized cell is actually showing — the <see cref="TextBlock"/> inside it, whether
    /// the cell is bare text or text alongside an affordance in a dock panel.</summary>
    public static TextBlock CellText(Border cell)
        => cell.GetVisualDescendants().OfType<TextBlock>().First();

    /// <summary>An editable result wide and tall enough that the grid scrolls in both directions: an
    /// <c>id</c> primary key plus <paramref name="columns"/> text columns of padded values.</summary>
    public static (ResultSetViewModel Result, ObservableCollection<object?[]> Rows) WideEditableResult(
        int columns = 12, int rows = 40)
    {
        var descriptors = new List<ColumnDescriptor> { new("id", "int4", typeof(int)) };
        var editable = new List<EditableColumn> { new(0, "id", IsPrimaryKey: true, NotNull: true) };
        for (var c = 1; c <= columns; c++)
        {
            descriptors.Add(new ColumnDescriptor($"col{c}", "text", typeof(string)));
            editable.Add(new EditableColumn(c, $"col{c}", IsPrimaryKey: false));
        }

        var data = new List<object?[]>(rows);
        for (var r = 0; r < rows; r++)
        {
            var row = new object?[columns + 1];
            row[0] = r + 1;
            for (var c = 1; c <= columns; c++) row[c] = $"r{r + 1}c{c}-value";
            data.Add(row);
        }

        var result = new QueryResult(descriptors, data, data.Count, TimeSpan.Zero, null, null, false);
        var rs = new ResultSetViewModel(result, "select * from wide", pageable: false)
        {
            PrimaryKeyColumns = [0],
            EditTarget = new EditTarget("public", "wide", editable),
        };
        rs.CaptureOriginals();
        return (rs, rs.Rows);
    }

    /// <summary>The single grid a one-result view built (its region-focus target).</summary>
    public static DataGrid Grid(ResultView view)
        => (DataGrid)(view.FocusableGrid ?? throw new InvalidOperationException("the view built no grid"));

    /// <summary>Where a realized cell sits inside the grid's own coordinate space — the position that must not
    /// move when the viewport is meant to stay put (#60).</summary>
    public static Point PositionIn(Border cell, Visual grid)
        => cell.TranslatePoint(default, grid)
           ?? throw new InvalidOperationException("cell is not connected to the grid's visual tree");

    /// <summary>A result over (id int, customer_id int, note text) where column 1 is a foreign key. FK-ness is
    /// what the grid renders from, not the CLR type, so it is declared rather than inferred.</summary>
    public static ResultSetViewModel ForeignKeyResult(IReadOnlyList<object?[]> rows, bool editable = false)
    {
        var columns = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int)),
            new ColumnDescriptor("customer_id", "int4", typeof(int)),
            new ColumnDescriptor("note", "text", typeof(string)),
        };
        var result = new QueryResult(columns, rows, rows.Count, TimeSpan.Zero, null, null, false);
        var rs = new ResultSetViewModel(result, "select id, customer_id, note from orders", pageable: false)
        {
            ForeignKeyColumns = [1],
            PrimaryKeyColumns = [0],
            EditTarget = editable
                ? new EditTarget("public", "orders",
                [
                    new EditableColumn(0, "id", IsPrimaryKey: true, NotNull: true),
                    new EditableColumn(1, "customer_id", IsPrimaryKey: false),
                    new EditableColumn(2, "note", IsPrimaryKey: false),
                ])
                : null,
        };
        if (editable) rs.CaptureOriginals();
        return rs;
    }
}
