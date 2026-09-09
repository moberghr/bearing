using System;
using System.Collections.Generic;
using System.Linq;

namespace Bearing.App.Results;

/// <summary>
/// Which of a result's columns are hidden, and how many are frozen (#118) — the state behind the column
/// header's Freeze / Hide menu.
/// <para>
/// Per result set, not per query: it answers "what am I looking at right now", and a re-run producing a
/// different shape has no business inheriting the last one's hidden columns.
/// </para>
/// <para>
/// Pure and free of the DataGrid on purpose (§2.5). The two rules that are easy to get wrong live here where
/// they can be tested without a window: hiding may never empty the grid, and freezing may never freeze
/// everything — a frozen pane that covers the whole width leaves nothing to scroll, which is the state a
/// user cannot get out of with the mouse.
/// </para>
/// </summary>
public sealed class ColumnLayout
{
    private readonly HashSet<int> _hidden = [];

    /// <param name="columnCount">The result's column count; the layout indexes against it and never past it.</param>
    public ColumnLayout(int columnCount) => ColumnCount = Math.Max(0, columnCount);

    public int ColumnCount { get; }

    /// <summary>Raised whenever the layout changes, so the grid and the meta row can re-read it.</summary>
    public event Action? Changed;

    /// <summary>
    /// How many handlers are attached.
    /// <para>
    /// Exposed because a leak here is invisible by construction: a layout outlives every grid built from it,
    /// and the results view re-renders on each tab switch — so a subscription the view forgot to detach
    /// showed up only as memory and as work done on grids nobody can see. This is the one observable that
    /// makes "the view detached what it attached" assertable.
    /// </para>
    /// </summary>
    internal int ChangedSubscribers => Changed?.GetInvocationList().Length ?? 0;

    /// <summary>The hidden columns, by source index.</summary>
    public IReadOnlyCollection<int> Hidden => _hidden;

    /// <summary>
    /// How many leading columns are frozen, in <b>display</b> order — which is what
    /// <c>DataGrid.FrozenColumnCount</c> means, and why this is a count rather than an index. The caller
    /// translates "freeze up to this header" into a count, because only the grid knows the display order
    /// after a user has reordered columns.
    /// </summary>
    public int FrozenCount { get; private set; }

    public bool IsHidden(int index) => _hidden.Contains(index);

    /// <summary>Visible columns, by source index, in source order.</summary>
    public IEnumerable<int> VisibleColumns => Enumerable.Range(0, ColumnCount).Where(i => !_hidden.Contains(i));

    public int VisibleCount => ColumnCount - _hidden.Count;

    /// <summary>
    /// Hide one column. Refused — returning false — for an out-of-range index, one already hidden, and the
    /// last visible column: a grid with no columns at all shows nothing, offers no header to right-click,
    /// and so cannot be undone from where the user is looking.
    /// </summary>
    public bool Hide(int index)
    {
        if (index < 0 || index >= ColumnCount) return false;
        if (_hidden.Contains(index)) return false;
        if (VisibleCount <= 1) return false;

        _hidden.Add(index);
        ClampFrozen();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Bring every column back. False when nothing was hidden.</summary>
    public bool ShowAll()
    {
        if (_hidden.Count == 0) return false;
        _hidden.Clear();
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Freeze the first <paramref name="count"/> columns in display order. Clamped so at least one column
    /// stays scrollable; 0 unfreezes. False when nothing changed.
    /// </summary>
    public bool Freeze(int count)
    {
        var clamped = Math.Clamp(count, 0, Math.Max(0, VisibleCount - 1));
        if (clamped == FrozenCount) return false;
        FrozenCount = clamped;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Unfreeze everything. False when nothing was frozen.</summary>
    public bool Unfreeze() => Freeze(0);

    /// <summary>
    /// The meta-row marker: "2 columns hidden", or null when none are.
    /// <para>
    /// Present because a hidden column must never be a <em>silent</em> omission. Someone reading a grid they
    /// narrowed an hour ago, or one narrowed on a colleague's machine, has no other way to tell that there is
    /// more in the result than is on screen — and an export, which deliberately includes hidden columns,
    /// would then arrive with columns they had never seen.
    /// </para>
    /// </summary>
    public string? HiddenText => _hidden.Count switch
    {
        0 => null,
        1 => "1 column hidden",
        var n => $"{n} columns hidden",
    };

    /// <summary>Keep the frozen count legal after a hide narrowed the grid.</summary>
    private void ClampFrozen() => FrozenCount = Math.Clamp(FrozenCount, 0, Math.Max(0, VisibleCount - 1));
}
