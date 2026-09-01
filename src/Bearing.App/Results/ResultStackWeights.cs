using System;
using Bearing.App.ViewModels;

namespace Bearing.App.Results;

/// <summary>
/// How much of the results pane each set gets when a run returns several (#81). Stacked view used to give
/// every set the same flat 360px cap, so a three-row set and a nine-hundred-row set were the same height and
/// the only way to favour one was to collapse the others — all or nothing, not a resize.
/// <para>
/// Pure so the shape of the answer is a test rather than something to eyeball: the interesting part is what
/// happens at the extremes, and those are exactly the runs that are awkward to produce by hand.
/// </para>
/// </summary>
public static class ResultStackWeights
{
    /// <summary>Floor. A set with one row still needs its meta row, its header and a row of data to be worth
    /// looking at, so it never shrinks to a sliver just because a neighbour is huge.</summary>
    public const double Min = 3;

    /// <summary>Ceiling. Past this the extra rows cost the other sets more than they gain: a 900-row set and
    /// a 20,000-row set both scroll internally, so a proportional share would only starve the neighbours
    /// without making either any more readable.</summary>
    public const double Max = 40;

    /// <summary>
    /// The star weight for one set — its loaded row count, clamped. Row count rather than the total, because
    /// the total may still be unknown (a pageable set counts on demand) and because what is on screen is what
    /// the height has to serve.
    /// </summary>
    public static double For(ResultSetViewModel result)
        => Math.Clamp(result.HasGrid ? result.Rows.Count : 0, Min, Max);
}
