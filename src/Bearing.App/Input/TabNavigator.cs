using System;
using System.Collections.Generic;
using Avalonia.Input;
using Bearing.App.ViewModels;

namespace Bearing.App.Input;

/// <summary>
/// Tab switching: visual-order stepping (Ctrl+PageUp/Down, and the wheel over the strip), go-to-tab-N, and
/// the held-modifier most-recently-used cycle (Ctrl+Tab). Owns the MRU list and the in-flight cycle flag —
/// the three fields <c>MainWindow</c> used to keep for this, which had to be read from four unrelated handlers
/// (<c>OnKeyUp</c>, the selected-tab property change, and both cycle commands).
/// <para>
/// The index arithmetic is exposed as statics so the wrap and clamp rules are testable.
/// </para>
/// </summary>
public sealed class TabNavigator
{
    private readonly MruList<EditorTabViewModel> _mru = new();
    private readonly Func<Keymap> _keymap;
    private int _cycleIndex;

    /// <param name="keymap">Reads the live keymap — which modifier ends an MRU cycle is rebindable, and the
    /// keymap can be replaced at runtime by the shortcuts editor.</param>
    public TabNavigator(Func<Keymap> keymap) => _keymap = keymap;

    /// <summary>True while a held-modifier MRU cycle is in flight (nothing should promote a tab to
    /// most-recent until it ends).</summary>
    public bool IsCycling { get; private set; }

    /// <summary>Rebuild the MRU list against the current tab set (tabs opened/closed elsewhere).</summary>
    public void Sync(IEnumerable<EditorTabViewModel> tabs) => _mru.Sync(tabs);

    /// <summary>Record a tab as most-recently used. Skipped mid-cycle: the cycle commits on release.</summary>
    public void Promote(EditorTabViewModel tab)
    {
        if (!IsCycling) _mru.Use(tab);
    }

    /// <summary>tab.next / tab.prev: move to the adjacent tab in visual (strip) order, wrapping around.</summary>
    public void SelectAdjacent(WorkspaceViewModel workspace, int dir)
    {
        // Visual order, not Tabs order: pinned tabs are drawn in their own row above the strip (#67), so
        // stepping has to follow what is on screen or "next tab" jumps rows unpredictably.
        var order = VisualOrder(workspace);
        if (order.Count == 0) return;
        var i = workspace.SelectedTab is { } t ? Math.Max(0, order.IndexOf(t)) : 0;
        workspace.SelectedTab = order[AdjacentIndex(order.Count, i, dir)];
    }

    /// <summary>
    /// The wheel over a tab strip: move one tab along the drawn order, and say whether it moved.
    /// <para>
    /// Deliberately <b>not</b> wrapping, unlike <see cref="SelectAdjacent"/>. A keystroke is one discrete
    /// press, so wrapping off the end is a shortcut; a wheel is a continuous gesture that overshoots, and
    /// wrapping there teleports you to the other end of the strip while you are still turning it — which is
    /// how you lose the tab you were looking for. Stopping at the end is what a scroll means everywhere else.
    /// </para>
    /// </summary>
    public bool StepSelection(WorkspaceViewModel workspace, int dir)
    {
        var order = VisualOrder(workspace);
        if (order.Count == 0) return false;
        var i = workspace.SelectedTab is { } t ? Math.Max(0, order.IndexOf(t)) : 0;
        var next = order[SteppedIndex(order.Count, i, dir)];
        if (ReferenceEquals(next, workspace.SelectedTab)) return false;
        workspace.SelectedTab = next;
        return true;
    }

    /// <summary>The tabs in the order they are drawn: the pinned row first, then the strip, each keeping its
    /// own relative order.</summary>
    internal static List<EditorTabViewModel> VisualOrder(WorkspaceViewModel workspace)
    {
        var order = new List<EditorTabViewModel>(workspace.Tabs.Count);
        foreach (var tab in workspace.Tabs) if (tab.IsPinned) order.Add(tab);
        foreach (var tab in workspace.Tabs) if (!tab.IsPinned) order.Add(tab);
        return order;
    }

    /// <summary>tab.goto{n}: jump to tab n (1-based); n=9 is "last tab" (browser convention). Clamps.</summary>
    public void SelectByIndex(WorkspaceViewModel workspace, int n)
    {
        // Also visual order: Alt+1 means "the first tab I can see", which is the first pinned one when there
        // are any.
        var order = VisualOrder(workspace);
        if (order.Count == 0) return;
        workspace.SelectedTab = order[GotoIndex(order.Count, n)];
    }

    /// <summary>tab.mruNext / tab.mruPrev: step through tabs in most-recently-used order while the binding's
    /// modifier is held; releasing it (see <see cref="EndsCycle"/>) commits the landed tab as most-recent.</summary>
    public void CycleMru(WorkspaceViewModel workspace, int dir)
    {
        _mru.Sync(workspace.Tabs);
        var items = _mru.Items;
        if (items.Count < 2) return;
        if (!IsCycling) { IsCycling = true; _cycleIndex = 0; }
        _cycleIndex = AdjacentIndex(items.Count, _cycleIndex, dir);
        workspace.SelectedTab = items[_cycleIndex];

        // A binding that holds no modifier (someone rebinds this to F6) gets no key-up to end the cycle, so
        // commit immediately — each press then steps from the current tab, the only coherent reading of a
        // modifier-less cycle, instead of leaving the flag stuck and MRU order frozen.
        if (MruCycle.ModifiersOf(_keymap()) == KeyModifiers.None) EndCycle(workspace);
    }

    /// <summary>Whether releasing <paramref name="key"/> ends an in-flight cycle.</summary>
    public bool EndsCycle(Key key) => IsCycling && MruCycle.EndsCycle(MruCycle.ModifiersOf(_keymap()), key);

    /// <summary>Finish an MRU cycle: stop cycling and record the landed tab as the most-recently used.</summary>
    public void EndCycle(WorkspaceViewModel workspace)
    {
        IsCycling = false;
        if (workspace.SelectedTab is { } t) _mru.Use(t);
    }

    /// <summary>The index <paramref name="dir"/> steps from <paramref name="current"/>, wrapping in both
    /// directions.</summary>
    public static int AdjacentIndex(int count, int current, int dir)
        => (current + dir + count) % count;

    /// <summary>The index one step from <paramref name="current"/>, clamped at both ends — the wheel's rule
    /// (see <see cref="StepSelection"/>), where <see cref="AdjacentIndex"/>'s wrap would overshoot.</summary>
    public static int SteppedIndex(int count, int current, int dir)
        => Math.Clamp(current + dir, 0, count - 1);

    /// <summary>The 0-based index for a 1-based "go to tab n" request; n≥9 means the last tab, and anything
    /// past the end clamps to it.</summary>
    public static int GotoIndex(int count, int n)
        => n >= 9 ? count - 1 : Math.Clamp(n - 1, 0, count - 1);
}
