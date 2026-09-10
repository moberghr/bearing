using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Bearing.App.Controls;
using Bearing.App.Input;
using Bearing.App.ViewModels;

namespace Bearing.App.Views;

/// <summary>
/// The button on the end of the tab strip: a dropdown of every open tab (Visual Studio's document-well
/// dropdown), carrying the count of tabs that are off the edge, and owning the strip's expanded state.
/// <para>
/// Pressing it opens the list — that is the affordance a button beside the tabs should have. It used to open
/// the modal picker, which is the same answer through a dialog that reads paths instead of showing tabs;
/// that list is still on Ctrl+E and is offered at the bottom of this menu, along with expanding the strip.
/// </para>
/// <para>
/// A coordinator rather than window code-behind (§9.1) because it is a small piece of state with three
/// consequences — what the button says, whether each row scrolls or wraps, and how tall the strip may get —
/// and the count that feeds it arrives from a layout callback, where the rules below are load-bearing.
/// </para>
/// </summary>
internal sealed class TabStripOverflow
{
    /// <summary>
    /// The button's own width, reserved from the strips whether it is showing or not. Otherwise showing it
    /// narrows the very viewport its count was read from, the count changes <i>because</i> the button
    /// appeared, and the answer oscillates — which threw <c>Infinite layout loop detected</c> out of the
    /// render pass and never drew (pinned by <c>WiringTests.An_overflowing_strip_renders_instead_of_looping</c>).
    /// The XAML fixes the button at the same width; the two numbers have to agree.
    /// </summary>
    private const double ChevronWidth = 40;

    /// <summary>
    /// How tall the expanded strip may grow before it scrolls vertically instead — about five rows. A strip
    /// with fifty tabs must not eat the editor to show them all, and beyond a handful of rows the dropdown
    /// and the picker are the better tools anyway.
    /// </summary>
    private const double ExpandedMaxHeight = 160;

    private readonly Button _chevron;
    // Plain Bottom, the placement the sidebar's own button menu uses. An edge-aligned mode is tempting for
    // a button in the corner and is one more thing to be wrong about a popup you cannot see in a test.
    private readonly MenuFlyout _menu = new() { Placement = PlacementMode.Bottom };
    private readonly IReadOnlyList<(ScrollViewer Scroller, TabStripScroller Scrolling)> _rows;

    private bool _visible;
    private string _label = "";
    private string _tip = "";

    /// <param name="workspace">Read at open time, not captured: the tab list this lists is the live one.</param>
    /// <param name="toggleExpand">The window's toggle, which also scrolls the selection back into view on
    /// the way in. Passed in rather than calling <see cref="Toggle"/> directly so the menu item, the command
    /// and any future caller all go through one route.</param>
    /// <param name="openPicker">Open the modal tab picker (<c>tab.pick</c>).</param>
    /// <param name="gesture">The keymap's gesture for a command id, for the menu's right-hand column. A
    /// command that ships unbound simply gets none.</param>
    public TabStripOverflow(
        Button chevron,
        Func<WorkspaceViewModel?> workspace,
        Action toggleExpand,
        Action openPicker,
        Func<string, KeyGesture?> gesture,
        params (ScrollViewer Scroller, TabStripScroller Scrolling)[] rows)
    {
        _chevron = chevron;
        _rows = rows;

        // Attached as the button's own Flyout, the shape ResultExportButton already uses here, so Avalonia
        // owns the open/close and the second press that dismisses it.
        _chevron.Flyout = _menu;

        // Filled on the way **in**, from the press — not from Opening, which is too late.
        //
        // That is the whole of the bug this shape exists to avoid: filling during `Opening` left the menu
        // reporting IsOpen with every item present, and drew an empty popup. The presenter is created and
        // measured around the open, so items added inside that window are not in what it drew — a flyout
        // test that asserts `IsOpen` and item count passes the entire time (§4.3: a property is not a
        // rendered thing; `The_dropdown_renders_its_items_not_just_an_empty_popup` is the assertion that
        // catches it).
        //
        // Tunnel, so it runs before the button starts handling the press, and PointerPressed rather than
        // Click because the framework has already opened the flyout by the time Click is raised.
        _chevron.AddHandler(
            InputElement.PointerPressedEvent,
            (_, _) => Refill(workspace, toggleExpand, openPicker, gesture),
            RoutingStrategies.Tunnel);

        // The keyboard opens it too (Space/Enter on the focused button), and that path has no press — so
        // the menu also carries a filled list from the start, and is refreshed whenever the one thing this
        // class itself changes moves (Toggle, below). What a keyboard open can be stale about is a dirty
        // dot; what it can never be is empty.
        _refill = () => Refill(workspace, toggleExpand, openPicker, gesture);
        _refill();
    }

    private readonly Action _refill;

    private void Refill(
        Func<WorkspaceViewModel?> workspace,
        Action toggleExpand,
        Action openPicker,
        Func<string, KeyGesture?> gesture)
    {
        if (workspace() is not { } ws) return;
        TabOverflowMenu.Fill(
            _menu, ws.PinnedTabs, ws.UnpinnedTabs, ws.SelectedTab,
            // Guarded, because a menu can outlive its list: a background close (a deleted script, a
            // project switch) can take a tab away while the dropdown is open, and selecting a tab that is
            // no longer in the strip would leave the workspace pointing at nothing on screen.
            go: tab => { if (ws.Tabs.Contains(tab)) ws.SelectedTab = tab; },
            expanded: IsExpanded,
            toggleExpand: toggleExpand,
            openPicker: openPicker,
            pickerGesture: gesture(CommandIds.TabPick),
            expandGesture: gesture(CommandIds.TabExpandStrip));
    }

    /// <summary>Whether the strip is showing every tab, wrapped onto as many rows as it takes.</summary>
    public bool IsExpanded { get; private set; }

    /// <summary>Flip between the one-row strip and the wrapped one. Returns the new state.</summary>
    public bool Toggle()
    {
        IsExpanded = !IsExpanded;

        foreach (var (scroller, _) in _rows)
        {
            // Disabled, not Hidden: a disabled scrollbar is what constrains the strip to the viewport width,
            // and that constraint is what makes the WrapPanel wrap. Hidden leaves it measured at infinite
            // width — which is the whole reason the tabs used to be laid out past the edge and clipped (#65).
            scroller.HorizontalScrollBarVisibility = IsExpanded
                ? ScrollBarVisibility.Disabled
                : ScrollBarVisibility.Hidden;
            scroller.VerticalScrollBarVisibility = IsExpanded
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled;
            scroller.MaxHeight = IsExpanded ? ExpandedMaxHeight : double.PositiveInfinity;
            // Collapsing from a wrapped strip: the horizontal offset is stale (it was frozen at 0 while
            // wrapped), and the window scrolls the selection back into view straight after.
            if (!IsExpanded) scroller.Offset = scroller.Offset.WithX(0);
        }

        Sync();
        _refill();   // the menu's last-but-one item says what it will do next, which just changed
        return IsExpanded;
    }

    /// <summary>
    /// Bring the button into line with the current overflow. Driven by <c>TabStripScroller.OverflowChanged</c>,
    /// which fires only when the number actually changes, so this is not per-frame work — but it still runs
    /// from a layout callback, so it writes nothing unless something changed. An unconditional write
    /// invalidates layout on every pass, and the next pass arrives with the same values to write again.
    /// </summary>
    public void Sync()
    {
        // Summed across the rows rather than shown per row: one button answering "where is my tab" matches
        // the question, and two counts would make the user do the addition.
        var hidden = 0;
        foreach (var (_, scrolling) in _rows) hidden += scrolling.HiddenCount(ChevronWidth);

        var (visible, label, tip) = TabOverflow.Chevron(hidden);
        if (visible == _visible && label == _label && tip == _tip) return;
        (_visible, _label, _tip) = (visible, label, tip);

        _chevron.IsVisible = visible;
        _chevron.Content = label;
        ToolTip.SetTip(_chevron, tip);
    }
}
