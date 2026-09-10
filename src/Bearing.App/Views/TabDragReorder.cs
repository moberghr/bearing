using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Bearing.App.Controls;
using Bearing.App.Input;
using Bearing.App.ViewModels;

namespace Bearing.App.Views;

/// <summary>
/// Dragging a tab to a new position in the strip, and across the two rows to pin or unpin it.
/// <para>
/// A coordinator rather than more code-behind (§9.1): it owns a gesture with state — a press that may or may
/// not become a drag, a caret that follows it, a row it may cross into — and the window is already a
/// composition root. The arithmetic is <see cref="TabReorder"/>, which is where the two coordinate spaces
/// (a strip's item order versus the master <c>Tabs</c> list behind both rows) are reconciled.
/// </para>
/// <para>
/// <b>Pointer capture, not <c>DoDragDropAsync</c></b>, which is what the scripts and connections trees use.
/// Different problem: those carry an item from one surface to another and want the platform's drag session
/// (and its Esc, its cursor, its cross-window drops). This one never leaves the strip, wants a caret that
/// tracks every pixel of movement, and — since a platform drag grabs the pointer and runs its own modal loop
/// — is testable through synthetic input only this way (§4.5).
/// </para>
/// <para>
/// Nothing moves until the pointer is released. That is what makes a mis-aimed drag free: the tabs stay put,
/// so dragging back to where you started and letting go changes nothing, and no reorder happens while the
/// positions the drop is read from are still moving.
/// </para>
/// </summary>
internal sealed class TabDragReorder
{
    /// <summary>How far the pointer must travel before a press becomes a drag. Below this a press is a
    /// click — selecting a tab must not nudge it out of order because the hand moved a pixel.</summary>
    private const double Threshold = 4;

    /// <summary>How close to a scrolling row's edge the pointer auto-scrolls it, and by how much per move.
    /// Without this the tabs off the edge are positions you cannot drag to at all — the strip only scrolls
    /// itself for a *selection*, and dragging deliberately does not change one.</summary>
    private const double EdgeZone = 28;
    private const double EdgeStep = 14;

    /// <summary>One row of the strip: its scroller (the drop surface and the thing that pans), the strip
    /// itself (whose containers give the tab positions), and whether it is the pinned row.</summary>
    internal sealed record Row(ScrollViewer Scroller, TabStrip Strip, TabStripScroller Scrolling, bool Pinned);

    private readonly Visual _owner;
    private readonly IReadOnlyList<Row> _rows;
    private readonly Func<WorkspaceViewModel?> _workspace;

    private EditorTabViewModel? _pressed;   // pressed, not yet past the threshold
    private Point _origin;
    private EditorTabViewModel? _dragging;
    private InsertionCaret? _caret;

    public TabDragReorder(Visual owner, Func<WorkspaceViewModel?> workspace, params Row[] rows)
    {
        _owner = owner;
        _workspace = workspace;
        _rows = rows;

        foreach (var row in rows)
        {
            // Tunnel, like the window's own press handler and for the same reason: a TabStripItem marks a
            // press handled as it selects itself, so a bubbling handler on the strip never runs.
            row.Strip.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
            row.Strip.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
            row.Strip.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
            // However the capture ends — another control taking it, the window losing activation — the tab
            // must not stay dimmed with a caret on screen.
            row.Strip.PointerCaptureLost += (_, _) => Cancel();
        }
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = null;
        if (e.GetCurrentPoint(null).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        // The ✕ and the pin toggle own their presses, and the rename box owns its own selection gesture —
        // dragging inside a text box selects text.
        if (e.Source is TextBox || TabHeaderParts.IsCloseAffordance(e.Source)
            || TabHeaderParts.IsPinAffordance(e.Source)) return;
        if (TabHeaderParts.Tab(e.Source) is not (_, var tab) || tab.IsRenaming) return;

        _pressed = tab;
        _origin = e.GetPosition(_owner);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        // The button coming up outside a release we saw (a drag that ended over another window) leaves the
        // gesture stranded otherwise.
        if (!e.GetCurrentPoint(_owner).Properties.IsLeftButtonPressed) { Cancel(); return; }

        if (_dragging is null)
        {
            if (_pressed is null || _workspace() is null) return;
            var moved = e.GetPosition(_owner) - _origin;
            if (Math.Abs(moved.X) <= Threshold && Math.Abs(moved.Y) <= Threshold) return;

            _dragging = _pressed;
            _pressed = null;
            _dragging.IsDragging = true;
            // Captured to the strip the press came from, so the moves keep arriving once the pointer is over
            // the editor, the other row, or nothing at all.
            e.Pointer.Capture(sender as IInputElement);
            _caret = new InsertionCaret(_owner);
        }

        if (Resolve(e, _dragging) is not { } at) { _caret?.Hide(); return; }
        _caret?.MoveTo(at.Row.Strip, at.Caret);
        AutoScroll(at.Row, e);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging is not { } dragged) { _pressed = null; return; }

        // Read the drop while the strip is still arranged as the drag left it, then tear the drag down
        // before committing — the commit re-splits the rows, which re-arranges the containers this was read
        // from.
        var at = Resolve(e, dragged);
        Cancel();
        e.Pointer.Capture(null);

        if (at is not { } drop || _workspace() is not { } workspace) return;
        if (workspace.MoveTab(dragged, drop.Row.Pinned, drop.Slot)) e.Handled = true;
    }

    /// <summary>Drop the gesture without committing anything.</summary>
    private void Cancel()
    {
        if (_dragging is not null) _dragging.IsDragging = false;
        _dragging = null;
        _pressed = null;
        _caret?.Dispose();
        _caret = null;
    }

    /// <summary>
    /// Where the tab would land right now: which row, which slot in it, and where to draw the caret.
    /// </summary>
    private (Row Row, int Slot, Rect Caret)? Resolve(PointerEventArgs e, EditorTabViewModel dragged)
    {
        if (Nearest(e) is not { } row) return null;

        var bounds = new List<Rect>();
        var draggedIndex = -1;
        for (var i = 0; i < row.Strip.ItemCount; i++)
        {
            var container = row.Strip.ContainerFromIndex(i);
            bounds.Add(container?.Bounds ?? default);
            if (container is { DataContext: EditorTabViewModel tab } && ReferenceEquals(tab, dragged))
                draggedIndex = i;
        }

        var gap = TabReorder.Hit(bounds, e.GetPosition(row.Strip));
        return (row, TabReorder.SlotWithoutDragged(gap.Slot, draggedIndex), gap.Caret);
    }

    /// <summary>
    /// The row the pointer is on, or — once it has left the strip entirely — the nearer of them.
    /// <para>
    /// Nearest rather than "inside or nothing", because a row is under 30px tall: a drag travelling along it
    /// strays above or below constantly, and a caret that blinks out every time the hand wobbles reads as a
    /// broken gesture. Crossing to the other row still takes deliberately moving onto it, and a pointer far
    /// below (in the editor) resolves to the unpinned row, where an unpinned tab already was.
    /// </para>
    /// </summary>
    private Row? Nearest(PointerEventArgs e)
    {
        Row? nearest = null;
        var best = double.MaxValue;
        foreach (var row in _rows)
        {
            // The pinned row is collapsed entirely when nothing is pinned, so it is not a target then — the
            // pin toggle is how the first tab gets pinned.
            if (!row.Scroller.IsVisible || row.Scroller.Bounds.Height <= 0) continue;
            var p = e.GetPosition(row.Scroller);
            var distance = p.Y < 0 ? -p.Y
                : p.Y > row.Scroller.Bounds.Height ? p.Y - row.Scroller.Bounds.Height
                : 0;
            if (distance >= best) continue;
            best = distance;
            nearest = row;
        }
        return nearest;
    }

    private static void AutoScroll(Row row, PointerEventArgs e)
    {
        var width = row.Scroller.Bounds.Width;
        if (width <= 0) return;
        var x = e.GetPosition(row.Scroller).X;
        if (x < EdgeZone) row.Scrolling.ScrollBy(-EdgeStep);
        else if (x > width - EdgeZone) row.Scrolling.ScrollBy(EdgeStep);
    }
}
