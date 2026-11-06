using System;
using System.Collections.Generic;
using System.Linq;

namespace Bearing.App.ViewModels;

/// <summary>
/// One block of results shown inline in the editor, beneath the statement that produced it. Belongs to a tab
/// (<see cref="EditorTabViewModel.InlineZones"/>); never persisted.
/// <para>
/// A zone is its own results context (<see cref="IResultFrames"/>): FK navigation inside it replaces its
/// contents, and its Back returns — the pane and other zones are not touched.
/// </para>
/// </summary>
public sealed class InlineResultZone : IResultFrames
{
    private readonly ResultHistory _history = new();

    /// <param name="fromWholeBuffer">The zone holds a whole Run All's sets that couldn't be mapped to their
    /// statements — re-running it from the zone re-runs the batch.</param>
    /// <param name="statementCount">How many statements the run that made this zone covered — more than one
    /// for a multi-statement selection, whose sets all land here; re-running from the zone runs as many.</param>
    public InlineResultZone(int offset, IReadOnlyList<ResultSetViewModel> sets, string? caption,
        bool fromWholeBuffer = false, int statementCount = 1)
    {
        Offset = offset;
        Sets = sets;
        _caption = caption;
        FromWholeBuffer = fromWholeBuffer;
        StatementCount = Math.Max(1, statementCount);
    }

    /// <summary>The statements behind this zone, ending at the one it hangs under (see the constructor).</summary>
    public int StatementCount { get; }

    /// <summary>The sets are a whole batch's, not one statement's (see the constructor).</summary>
    public bool FromWholeBuffer { get; }

    /// <summary>Identity for the view's control cache — offsets move, sets are replaced by re-runs.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Buffer offset the zone hangs under: it is drawn below the line holding this offset. Kept current
    /// by the editor while the tab is shown (it follows edits), so it is right again when the tab comes back.</summary>
    public int Offset { get; set; }

    /// <summary>The result sets shown now: the run's, or the ones FK navigation put on top of them.</summary>
    public IReadOnlyList<ResultSetViewModel> Sets { get; private set; }

    /// <summary>The cap on a table's height the user dragged the grip to, in pixels, or null for the default
    /// (ten rows). Kept on the zone so it survives the view being rebuilt.</summary>
    public double? TableHeight { get; set; }

    private readonly string? _caption;

    /// <summary>A line naming what the zone holds when the run gave several sets ("3 result sets from this
    /// batch") — only while the run's own sets are shown, not an FK frame on top of them.</summary>
    public string? Caption => _history.CanGoBack ? null : _caption;

    /// <summary>The text this zone hung under was deleted (or replaced) while it held unsaved edits, so it was
    /// kept where the text was rather than dropped with it. Its header says so.</summary>
    public bool StatementRemoved { get; set; }

    /// <summary>Raised when the zone is orphaned (<see cref="StatementRemoved"/>). FK navigation and Back are
    /// redrawn by whoever drove them (the zone's results view), so they don't raise it — a second render of
    /// the same frame would rebuild every grid twice.</summary>
    public event Action? Changed;

    /// <summary>Tell the view to re-render (after <see cref="StatementRemoved"/> is set).</summary>
    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>Raised once the zone has left its tab (✕, replaced by a run, cleared with new text), so its
    /// view can let go of the sets — they may outlive it, held by the pane.</summary>
    public event Action? Retired;

    /// <summary>The tab dropped this zone.</summary>
    internal void Retire() => Retired?.Invoke();

    // ---- IResultFrames -----------------------------------------------------------------------------

    IReadOnlyList<ResultSetViewModel> IResultFrames.Current => Sets;

    public bool CanGoBack => _history.CanGoBack;

    public void Push(IReadOnlyList<ResultSetViewModel> results)
    {
        _history.Push(Sets);
        Sets = results;
    }

    public void GoBack()
    {
        if (_history.Pop() is not { } previous)
        {
            return;
        }
        Sets = previous;
    }

    public IEnumerable<ResultSetViewModel> AllSets => _history.Sets.Concat(Sets);

    /// <summary>The frames Back would return to, oldest first — carried to the pane by the placement toggle.</summary>
    public IReadOnlyList<IReadOnlyList<ResultSetViewModel>> StashedFrames => _history.Frames;

    /// <summary>Unsaved grid edits anywhere in the zone, its Back stack included.</summary>
    public int PendingCount => PendingEdits.Count(AllSets);
}
