using System;
using Bearing.App.ViewModels;

namespace Bearing.App.Results;

/// <summary>
/// How much of the results pane each set gets when a run returns several (#81). Stacked view used to give
/// every set the same flat 360px cap, so a three-row set and a nine-hundred-row set were the same height and
/// the only way to favour one was to collapse the others — all or nothing, not a resize.
/// <para>
/// The weight grows with the <i>logarithm</i> of the row count, not the count itself. Proportional weights
/// read well as a formula and badly on screen: three rows beside two hundred is a 60:1 share, which pins the
/// small set at its floor, and a floored star row is a row the grid no longer redistributes — the last set in
/// the stack was pushed off the bottom of the pane. Compressing the range keeps the ordering (more rows is
/// always taller) while keeping every set inside one screen, which is the property the stack needs.
/// </para>
/// <para>
/// Pure so the shape of the answer is a test rather than something to eyeball: the interesting part is what
/// happens at the extremes, and those are exactly the runs that are awkward to produce by hand.
/// </para>
/// </summary>
public static class ResultStackWeights
{
    /// <summary>Floor, for an empty set or a statement message. There is no data to be proportional to, but
    /// the meta row and the header still have to be legible.</summary>
    public const double Min = 8;

    /// <summary>Ceiling. Past <see cref="Cap"/> rows the extra ones cost the other sets more than they gain:
    /// a 900-row set and a 20,000-row set both scroll internally, so a bigger share would only starve the
    /// neighbours without making either any more readable.</summary>
    public const double Max = 20;

    /// <summary>Row count at which a set is already as tall as the layout will make it.</summary>
    public const int Cap = 200;

    /// <summary>
    /// The star weight for one set, from its loaded row count. Row count rather than the reported total,
    /// because the total may still be unknown (a pageable set counts on demand) and because what is on screen
    /// is what the height has to serve.
    /// </summary>
    public static double For(ResultSetViewModel result)
    {
        var rows = result.HasGrid ? result.Rows.Count : 0;
        if (rows <= 0) return Min;

        var fraction = Math.Log(1 + Math.Min(rows, Cap)) / Math.Log(1 + Cap);
        return Math.Round(Min + ((Max - Min) * fraction), 2);
    }
}
