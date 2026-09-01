using System;
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

    /// <summary>
    /// Smallest a set is allowed to be: its meta bar, its column headers, and two rows of data. Derived rather
    /// than picked, because the number that matters is "does a small set still show any of its rows" — the
    /// first cut at 72px was under the chrome alone, so a three-row set rendered as a header and nothing else.
    /// A divider that can erase a set is also a way to lose a result you meant to keep.
    /// </summary>
    private const double MinSetHeight =
        MetaBarHeight + HeaderRowHeight + (2 * ResultGridChrome.RowMinHeight);

    /// <summary>The meta bar as <see cref="ResultView"/> builds it: content, its 5px padding, and the rule.</summary>
    private const double MetaBarHeight = ResultChrome.MetaRowContentHeight + (2 * 5) + 1;

    /// <summary>The grid's column-header row. Not a constant of the theme, so this is the measured height at
    /// <see cref="ResultGridChrome.HeaderFontSize"/> — a floor being a couple of pixels out costs nothing.</summary>
    private const double HeaderRowHeight = 30;

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

            // A set with no grid is one line of text — a statement message or an error. It takes an Auto row,
            // because a star row would hold a share of the pane open for a single line, and a message has
            // nothing to scroll: there is no size here for the user to want to trade for.
            var weight = ResultStackWeights.For(result);
            var row = result.HasGrid
                ? new RowDefinition(weight, GridUnitType.Star) { MinHeight = MinSetHeight }
                : new RowDefinition(GridLength.Auto);
            _grid.RowDefinitions.Add(row);
            _rows[result] = row;
            _weights[result] = weight;

            Grid.SetRow(container, _grid.RowDefinitions.Count - 1);
            _grid.Children.Add(container);
        }

        Content = _grid;

        // The floor depends on how much pane there is to share and on what the dividers and collapsed sets
        // took, none of which is known until a layout pass has run — so it is recomputed after each one
        // rather than on resize, which would miss a collapse.
        LayoutUpdated += (_, _) => ApplyFloor();
    }

    /// <summary>
    /// Hold every open set to <see cref="MinSetHeight"/> — unless the pane is too short for all of them to
    /// have it, in which case they share what there is equally.
    /// <para>
    /// A floor a <see cref="Grid"/> cannot honour is worse than no floor: it clamps the row and then arranges
    /// the rest as if it had not, so the sets overflow the pane and the last one is simply not on screen. Nine
    /// sets in a short pane genuinely cannot all be legible; being equally cramped and all present beats being
    /// comfortable and truncated.
    /// </para>
    /// </summary>
    private void ApplyFloor()
    {
        var open = _rows.Values.Where(r => r.Height.IsStar).ToList();
        if (open.Count == 0) return;

        // Dividers and collapsed sets are Auto rows: their height is spoken for before the stars divide up
        // what is left.
        var spoken = _grid.RowDefinitions.Where(r => !r.Height.IsStar).Sum(r => r.ActualHeight);
        // Floored to whole pixels: the floor is only a floor, and rounding it up is how nine equal sets add
        // up to two pixels more pane than there is.
        var share = Math.Floor((Bounds.Height - spoken) / open.Count);
        var floor = Math.Clamp(share, 0, MinSetHeight);

        // Only on a change: this runs after every layout pass, and writing the floor schedules another one.
        // The floor does not depend on the star rows' own heights, so it settles rather than oscillating.
        foreach (var row in open)
            if (Math.Abs(row.MinHeight - floor) > 0.5) row.MinHeight = floor;
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

        // A set that never had a star row does not gain one by being reopened — a message goes back to Auto,
        // which is what it already was.
        if (!result.HasGrid) return;

        // Left at zero for the layout pass to set: reopening changes what there is to share, and ApplyFloor
        // is what knows the answer.
        row.MinHeight = 0;
        // Back to the weight it opened at, not to whatever a drag had left it — a set coming back from
        // collapsed has no size of its own to restore, and the opening proportion is the honest default.
        row.Height = new GridLength(_weights[result], GridUnitType.Star);
    }
}
