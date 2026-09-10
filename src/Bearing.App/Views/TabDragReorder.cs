using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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

    /// <summary>How close to a scrolling row's edge the pointer auto-scrolls it, how far each tick moves it,
    /// and how often a tick comes. Without this the tabs off the edge are positions you cannot drag to at
    /// all — the strip only scrolls itself for a *selection*, and dragging deliberately does not change one.
    /// <para>
    /// On a <b>timer</b>, not per pointer-move: a pointer held still inside the zone is the ordinary way to
    /// ask for a long scroll, and moving it one step per move event meant the only way to travel was to
    /// jiggle the mouse — about three dozen wiggles to cross a strip 500px wider than its viewport.
    /// </para></summary>
    private const double EdgeZone = 28;
    private const double EdgeStep = 14;
    private static readonly TimeSpan EdgeTick = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// How far outside the strip a release still counts as a drop on it.
    /// <para>
    /// Not unlimited, which is what "the nearest row, always" amounted to: a tab dragged down into the
    /// editor and let go was filed at the end of the strip, from a gesture that never moved sideways.
    /// Beyond this the drag simply ends and nothing moves, which is what makes a mis-aimed one free. Wide
    /// enough that a hand wobbling over a 28px row is still aiming at it.
    /// </para>
    /// </summary>
    private const double DropTolerance = 48;

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
    private IPointer? _pointer;          // held so a cancel can release the capture it took
    private DispatcherTimer? _edgeTimer;
    private Row? _edgeRow;               // …and what it is panning, re-read on every move
    private int _edgeDirection;

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

        // Escape abandons a drag, which is the one thing a platform drag session would have given for free
        // and the reason this had none: nothing moves until the release, so an escape simply ends the
        // gesture. On the window, because the keyboard focus during a drag is wherever it was — the editor,
        // usually — and in the tunnel phase ahead of the window's own Escape (which cancels a running
        // query). Marking it handled is what keeps that from also firing.
        if (owner is InputElement window)
            window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (_dragging is null || e.Key != Key.Escape) return;
                Cancel();
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
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
            // the editor, the other row, or nothing at all. Held, so a cancel can give it back.
            _pointer = e.Pointer;
            e.Pointer.Capture(sender as IInputElement);
            _caret = new InsertionCaret(_owner);
        }

        if (Resolve(e, _dragging) is not { } at) { _caret?.Hide(); StopEdgeScroll(); return; }
        _caret?.MoveTo(at.Row.Strip, at.Caret);
        EdgeScroll(at.Row, e.GetPosition(at.Row.Scroller).X);
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
        StopEdgeScroll();
        // Giving the capture back is what ends the drag for the strip as well: without it the next press
        // anywhere still arrives here, and an escaped drag would silently resume on the next move.
        _pointer?.Capture(null);
        _pointer = null;
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
    /// The row the pointer is on, the nearer of the two once it has left one, or <b>null</b> once it is
    /// further than <see cref="DropTolerance"/> outside the strip altogether.
    /// <para>
    /// Nearest rather than "inside or nothing", because a row is under 30px tall: a drag travelling along
    /// one strays above or below it constantly, and a caret that blinks out every time the hand wobbles
    /// reads as a broken gesture.
    /// </para>
    /// <para>
    /// But bounded, because "nearest, always" means every release is a drop: a tab dragged down into the
    /// editor and let go was filed at the end of the strip. Past the tolerance the drag is simply over.
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
        return best <= DropTolerance ? nearest : null;
    }

    /// <summary>
    /// Keep panning <paramref name="row"/> while the pointer sits in one of its edge zones. The row and the
    /// direction are re-read on every move; the ticking is what makes holding still work.
    /// <para>
    /// They are fields rather than captured in the tick handler, because a handler subscribed per move
    /// cannot be unsubscribed — each closure is a different delegate — and the timer would end up with one
    /// handler per pointer move, all of them panning at once.
    /// </para>
    /// </summary>
    private void EdgeScroll(Row row, double x)
    {
        _edgeRow = row;
        _edgeDirection = TabReorder.EdgeScroll(x, row.Scroller.Bounds.Width, EdgeZone);
        if (_edgeDirection == 0) { StopEdgeScroll(); return; }
        if (_edgeTimer?.IsEnabled == true) return;   // already panning; the fields above steer it

        _edgeTimer ??= new DispatcherTimer(EdgeTick, DispatcherPriority.Normal, OnEdgeTick);
        // One step as it starts, so the first pixel into the zone moves rather than waiting out a tick.
        row.Scrolling.ScrollBy(EdgeStep * _edgeDirection);
        _edgeTimer.Start();
    }

    private void OnEdgeTick(object? sender, EventArgs e)
    {
        if (_dragging is null || _edgeRow is not { } row || _edgeDirection == 0) { StopEdgeScroll(); return; }
        row.Scrolling.ScrollBy(EdgeStep * _edgeDirection);
    }

    private void StopEdgeScroll()
    {
        _edgeTimer?.Stop();
        _edgeDirection = 0;
        _edgeRow = null;
    }
}
