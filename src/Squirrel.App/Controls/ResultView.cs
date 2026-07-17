using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Squirrel.Core.Data;

namespace Squirrel.App.Controls;

/// <summary>
/// Renders a query run's result sets: a single grid for one set, sub-tabs for several,
/// and inline text for empty/non-query/error results. Self-contained and reusable —
/// assign <see cref="Results"/> and it rebuilds its content.
/// </summary>
public sealed class ResultView : UserControl
{
    private IReadOnlyList<QueryResult>? _results;

    /// <summary>The result sets to display. Assigning replaces the rendered content.</summary>
    public IReadOnlyList<QueryResult>? Results
    {
        get => _results;
        set { _results = value; Rebuild(); }
    }

    private void Rebuild()
    {
        var results = _results;
        if (results is null || results.Count == 0) { Content = null; return; }
        if (results.Count == 1) { Content = BuildResultSet(results[0]); return; }

        var tabs = new TabControl();
        for (var i = 0; i < results.Count; i++)
            tabs.Items.Add(new TabItem { Header = TabHeader(i, results[i]), Content = BuildResultSet(results[i]) });
        tabs.SelectedIndex = 0;
        Content = tabs;
    }

    private static string TabHeader(int index, QueryResult result)
    {
        if (!result.Success) return $"Result {index + 1} · error";
        if (result.Columns.Count == 0) return $"Result {index + 1} · {result.Message}";
        return $"Result {index + 1} ({result.RowCount})";
    }

    private static Control BuildResultSet(QueryResult result)
    {
        if (!result.Success)
            return new TextBlock { Text = $"Error: {result.Error?.Message}", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };

        if (result.Columns.Count == 0)
            return new TextBlock { Text = result.Message ?? "Statement executed.", Margin = new Thickness(8) };

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserResizeColumns = true,
            CanUserReorderColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            HeadersVisibility = DataGridHeadersVisibility.All, // row-number gutter + column headers
        };
        grid.LoadingRow += (_, e) => e.Row.Header = (e.Row.GetIndex() + 1).ToString();
        for (var i = 0; i < result.Columns.Count; i++)
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = result.Columns[i].Name,
                Binding = new Binding($"[{i}]"),
            });
        grid.ItemsSource = result.Rows;
        return grid;
    }
}
