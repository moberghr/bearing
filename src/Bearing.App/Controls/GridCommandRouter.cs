using System;
using Avalonia.Controls;

namespace Bearing.App.Controls;

/// <summary>
/// Which <see cref="ResultView"/> the grid commands act on. The commands are registered once, into the one
/// registry, but there is a results view in the pane and one in every inline zone (spec §8): a keystroke or a
/// palette command acts on the view the user last worked in — the one whose grid has the keystroke in flight
/// or last took focus — and on the pane's when that view is no longer on screen.
/// </summary>
public sealed class GridCommandRouter(ResultView fallback)
{
    // Weak: a zone closed or replaced while its view was the active one must not be kept alive by the router.
    private WeakReference<ResultView>? _active;

    /// <summary>The view grid commands go to now.</summary>
    public ResultView Current
        => _active is not null && _active.TryGetTarget(out var active) && TopLevel.GetTopLevel(active) is not null
            ? active
            : fallback;

    /// <summary>A grid in <paramref name="view"/> took focus or is handling a key.</summary>
    public void Activate(ResultView view) => _active = new WeakReference<ResultView>(view);
}
