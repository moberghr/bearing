using System;
using System.Collections.Generic;
using System.Linq;

namespace Bearing.App.ViewModels;

/// <summary>
/// A place results are shown, with its own foreign-key navigation history: the tab (what the results pane
/// shows) and each inline zone (spec: docs/specs/2026-11-06-inline-results.md §4). FK navigation pushes onto the
/// context it was followed from, and Back pops it — so following a key in one zone never touches the pane or
/// another zone.
/// </summary>
public interface IResultFrames
{
    /// <summary>The result sets shown now.</summary>
    IReadOnlyList<ResultSetViewModel> Current { get; }

    /// <summary>True when FK navigation stacked earlier results that Back can return to.</summary>
    bool CanGoBack { get; }

    /// <summary>Stash the current results and show <paramref name="results"/> on top (FK navigation).</summary>
    void Push(IReadOnlyList<ResultSetViewModel> results);

    /// <summary>Drop the current (navigated) results and show the previous frame.</summary>
    void GoBack();

    /// <summary>The sets on the Back stack (every frame but the current one), oldest first.</summary>
    IEnumerable<ResultSetViewModel> StashedSets { get; }

    /// <summary>Every frame held — the current one and the stashed ones — oldest first. What a replacement
    /// would throw away, pending edits included.</summary>
    IEnumerable<ResultSetViewModel> AllSets { get; }
}

/// <summary>A stack of result frames over a current one — the history both contexts keep.</summary>
public sealed class ResultHistory
{
    private readonly Stack<IReadOnlyList<ResultSetViewModel>> _frames = new();

    public bool CanGoBack => _frames.Count > 0;

    public void Push(IReadOnlyList<ResultSetViewModel> current) => _frames.Push(current);

    public IReadOnlyList<ResultSetViewModel>? Pop() => _frames.Count > 0 ? _frames.Pop() : null;

    public void Clear() => _frames.Clear();

    /// <summary>The stashed frames' sets, oldest first.</summary>
    public IEnumerable<ResultSetViewModel> Sets => _frames.Reverse().SelectMany(f => f);

    /// <summary>The stashed frames, oldest first.</summary>
    public IReadOnlyList<IReadOnlyList<ResultSetViewModel>> Frames => _frames.Reverse().ToList();
}

/// <summary>Counting unsaved grid edits across result sets — what a replacement or a close would lose.</summary>
public static class PendingEdits
{
    /// <summary>Pending changes across <paramref name="sets"/>, each set counted once even when it is held in
    /// two places (a run's sets are both the tab's results and a zone's).</summary>
    public static int Count(IEnumerable<ResultSetViewModel> sets)
        => sets.Distinct().Where(s => s.HasPendingChanges).Sum(s => s.PendingCount);

    /// <summary>The changes the user is about to lose, as a short phrase ("3 unsaved changes in 2 results").</summary>
    public static string Describe(int changes, int places)
    {
        var what = changes == 1 ? "1 unsaved change" : $"{changes} unsaved changes";
        return places <= 1 ? what : $"{what} in {places} results";
    }
}
