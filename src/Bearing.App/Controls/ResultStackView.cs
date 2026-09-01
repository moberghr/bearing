using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Controls;

/// <summary>
/// Stacked results: every set of a run laid out vertically with a draggable divider between them (#81).
/// <para>
/// This replaced a <c>StackPanel</c> inside one <c>ScrollViewer</c>, where each set was clamped to a flat
/// 360px. That gave a three-row set and a nine-hundred-row set the same height, left no way to trade space
/// between them short of collapsing one, and — with zero spacing — no seam to show where one set ended and
/// the next began. The splitter is that seam as well as the handle.
/// </para>
/// <para>
/// A <see cref="Grid"/> of star rows rather than a scrolling stack, because star rows and an unbounded-height
/// scroll parent do not compose: the grid fills the pane and each set scrolls internally, which the result
/// grids already do. Initial heights come from <see cref="ResultStackWeights"/>, so a run opens roughly in
/// proportion to what each set returned instead of uniformly.
/// </para>
/// <para>
/// Its own type per §9.1 — <see cref="ResultView"/> is a composition root and this is a layout with state
/// (row sizes, which sets are collapsed) that would have grown its layout partial.
/// </para>
/// </summary>
internal sealed class ResultStackView : UserControl
{
    /// <summary>Thickness of the divider, matching the editor/results splitter in the shell so the two read
    /// as the same affordance.</summary>
    private const double DividerThickness = 4;

    /// <summary>Smallest a set can be dragged to: its meta row plus a little data. Below this the set stops
    /// saying anything, and a divider that can erase a set is a way to lose a result you meant to keep.</summary>
    private const double MinSetHeight = 72;

    private readonly Grid _grid;
    private readonly Dictionary<ResultSetViewModel, RowDefinition> _rows = new();
    private readonly Dictionary<ResultSetViewModel, double> _weights = new();

    /// <param name="sets">Each result and the container <see cref="ResultView"/> built for it, in order.</param>
    public ResultStackView(IReadOnlyList<(ResultSetViewModel Result, Control Container)> sets)
    {
        _grid = new Grid();

        for (var i = 0; i < sets.Count; i++)
        {
            var (result, container) = sets[i];

            // A divider before every set but the first. Auto-sized, so it is never part of the share the
            // star rows divide.
            if (i > 0)
            {
                _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var divider = new GridSplitter
                {
                    ResizeDirection = GridResizeDirection.Rows,
                    Height = DividerThickness,
                    Background = SeparatorBrush,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                Grid.SetRow(divider, _grid.RowDefinitions.Count - 1);
                _grid.Children.Add(divider);
            }

            var weight = ResultStackWeights.For(result);
            var row = new RowDefinition(weight, GridUnitType.Star) { MinHeight = MinSetHeight };
            _grid.RowDefinitions.Add(row);
            _rows[result] = row;
            _weights[result] = weight;

            Grid.SetRow(container, _grid.RowDefinitions.Count - 1);
            _grid.Children.Add(container);
        }

        Content = _grid;
    }

    /// <summary>The dividers, in order. Exposed for tests — nothing in the app reads them.</summary>
    internal IReadOnlyList<GridSplitter> Dividers => _grid.Children.OfType<GridSplitter>().ToList();

    /// <summary>The star weight a set's row currently holds, or null when it is collapsed. Also for tests.</summary>
    internal double? WeightOf(ResultSetViewModel result)
        => _rows.TryGetValue(result, out var row) && row.Height.IsStar ? row.Height.Value : null;

    /// <summary>
    /// Give a collapsed set's space back to its neighbours, and return it when the set reopens.
    /// <para>
    /// A collapsed set keeps a star row otherwise: the body hides but the row holds its share of the pane
    /// open, so collapsing would free nothing — which is the one thing the chevron is for.
    /// </para>
    /// </summary>
    public void SetCollapsed(ResultSetViewModel result, bool collapsed)
    {
        if (!_rows.TryGetValue(result, out var row)) return;

        if (collapsed)
        {
            // Auto, so the row is exactly its meta row; MinHeight has to go with it or the floor keeps the
            // space the collapse was meant to release.
            row.MinHeight = 0;
            row.Height = GridLength.Auto;
            return;
        }

        row.MinHeight = MinSetHeight;
        // Back to the weight it opened at, not to whatever a drag had left it — a set coming back from
        // collapsed has no size of its own to restore, and the opening proportion is the honest default.
        row.Height = new GridLength(_weights[result], GridUnitType.Star);
    }
}
