using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Controls;

/// <summary>
/// The quick-stats strip under a result grid (design RESULTS_GRID §7): count / sum / avg / min / max over the
/// selected cells, hidden until at least two measure cells of <em>this</em> result are selected. One instance
/// per rendered result set; <see cref="Sync"/> is driven by <see cref="GridSelectionController.Changed"/>.
/// </summary>
public sealed class QuickStatsBar
{
    private readonly ResultSetViewModel _result;
    private readonly GridSelectionController _selection;
    private readonly Border _bar;

    public QuickStatsBar(ResultSetViewModel result, GridSelectionController selection)
    {
        _result = result;
        _selection = selection;
        _bar = new Border
        {
            Background = Res("Bg.Hover"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = SeparatorBrush,
            Padding = new Thickness(8, 4),
            IsVisible = false,
        };
    }

    /// <summary>Dock the (initially hidden) bar under <paramref name="content"/>.</summary>
    public Control Wrap(Control content)
    {
        DockPanel.SetDock(_bar, Dock.Bottom);
        var panel = new DockPanel();
        panel.Children.Add(_bar);
        panel.Children.Add(content);
        return panel;
    }

    /// <summary>Recompute from the current selection: show the aggregates when this result owns a selection
    /// of two or more measure cells, otherwise hide.</summary>
    public void Sync()
    {
        var model = _selection.Model;
        var mine = ReferenceEquals(_result, model.Result) && model.Cells.Count >= 2;
        if (mine && CellStats.Aggregate(GridSelectionOps.MeasureValues(_result, model.Cells)) is { } stats)
        {
            _bar.Child = BuildContent(model.Cells.Count, stats);
            _bar.IsVisible = true;
        }
        else
        {
            _bar.IsVisible = false;
        }
    }

    private Control BuildContent(int count, CellStatistics stats)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Stat($"{count} cells", "Text.Dim"));
        stack.Children.Add(Sep());
        stack.Children.Add(Stat($"count {stats.Count}", "Text.Primary"));
        stack.Children.Add(Sep());
        stack.Children.Add(Stat($"sum {CellStats.Format(stats.Sum)}", "Ok.Green")); // sum highlighted green
        stack.Children.Add(Sep());
        stack.Children.Add(Stat($"avg {CellStats.Format(stats.Avg)}", "Text.Primary"));
        stack.Children.Add(Sep());
        stack.Children.Add(Stat($"min {CellStats.Format(stats.Min)}", "Text.Primary"));
        stack.Children.Add(Sep());
        stack.Children.Add(Stat($"max {CellStats.Format(stats.Max)}", "Text.Primary"));

        var clear = ResultChrome.IconTextButton("Clear", "Clear selection");
        clear.Margin = new Thickness(12, 0, 0, 0);
        clear.Click += (_, _) => { if (ReferenceEquals(_selection.Model.Result, _result)) _selection.ClearAndNotify(); };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(stack, 0);
        Grid.SetColumn(clear, 1);
        grid.Children.Add(stack);
        grid.Children.Add(clear);
        return grid;
    }

    private static Control Stat(string text, string colorKey) => new TextBlock
    {
        Text = text,
        Foreground = Res(colorKey),
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = Metric("Font.Body"),
    };

    private static Control Sep() => new TextBlock
    {
        Text = " · ",
        Foreground = Res("Text.Faint"),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
