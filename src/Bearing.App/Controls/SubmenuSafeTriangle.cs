using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Bearing.App.Controls;

/// <summary>
/// Makes cascading menus forgiving: while the pointer travels from a menu item toward its open submenu, the
/// items it crosses on the way don't take over (which would close the submenu under the pointer).
/// <para>
/// Avalonia's <c>DefaultMenuInteractionHandler</c> closes a sibling's submenu when the pointer enters another
/// item, and — with <c>MenuShowDelay</c> at zero (see <c>App.Initialize</c>) — does so at once. The item
/// enter/exit events carry no position, so the aim is read from the last few pointer moves instead: if the
/// pointer was heading for the submenu's near edge, the sibling's enter (and the owner's exit) is swallowed
/// before Avalonia's handler on the menu sees it. A deliberate move elsewhere behaves as before.
/// </para>
/// Installed once for the process (class handlers are per type, not per <c>Application</c>).
/// </summary>
internal static class SubmenuSafeTriangle
{
    /// <summary>How long after leaving the owner item the aim keeps protecting the path.</summary>
    private const long GraceMs = 500;
    /// <summary>Vertical slack, in screen pixels, around the submenu's edge when testing the aim.</summary>
    private const double Slack = 24;
    private const int MaxSamples = 8;

    private static bool _installed;
    private static MenuItem? _owner;
    private static long _lastSeen;
    private static readonly (double X, double Y, long T)[] Samples = new (double, double, long)[MaxSamples];
    private static int _count;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        InputElement.PointerMovedEvent.AddClassHandler<MenuItem>(OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        MenuItem.PointerEnteredItemEvent.AddClassHandler<MenuItem>(OnEnteredItem, RoutingStrategies.Bubble);
        MenuItem.PointerExitedItemEvent.AddClassHandler<MenuItem>(OnExitedItem, RoutingStrategies.Bubble);
    }

    private static void OnMoved(MenuItem item, RoutedEventArgs re)
    {
        if (re is not PointerEventArgs e || !IsNearest(item, e)) return;
        var s = item.PointToScreen(e.GetPosition(item));
        Samples[_count++ % MaxSamples] = (s.X, s.Y, Stopwatch.GetTimestamp());
        if (item.HasSubMenu && item.IsSubMenuOpen)
        {
            _owner = item;
            _lastSeen = Stopwatch.GetTimestamp();
        }
    }

    private static void OnEnteredItem(MenuItem item, RoutedEventArgs e)
    {
        if (IsNearest(item, e) && _owner is { } owner && owner != item && owner.Parent == item.Parent && Aims(owner))
            e.Handled = true;
    }

    private static void OnExitedItem(MenuItem item, RoutedEventArgs e)
    {
        // The owner itself being left toward its submenu must not close it.
        if (IsNearest(item, e) && item == _owner && Aims(item)) e.Handled = true;
    }

    private static bool Aims(MenuItem owner)
    {
        if (!owner.IsSubMenuOpen || _count == 0) return false;
        if (Stopwatch.GetElapsedTime(_lastSeen).TotalMilliseconds > GraceMs) return false;

        var popup = owner.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
        if (popup?.Child is not { } child || TopLevel.GetTopLevel(child) is not { } top) return false;

        var scale = top.RenderScaling;
        var tl = child.PointToScreen(default);
        var left = tl.X;
        var right = tl.X + child.Bounds.Width * scale;
        var topY = tl.Y;
        var bottom = tl.Y + child.Bounds.Height * scale;

        // Newest sample, and the oldest recent one far enough away to give a direction.
        var n = Math.Min(_count, MaxSamples);
        var last = Samples[(_count - 1) % MaxSamples];
        double? dx = null, dy = null;
        for (var i = 2; i <= n; i++)
        {
            var older = Samples[(_count - i) % MaxSamples];
            if (Stopwatch.GetElapsedTime(older.T, last.T).TotalMilliseconds > 200) break;
            var ddx = last.X - older.X;
            var ddy = last.Y - older.Y;
            if (ddx * ddx + ddy * ddy >= 9) { dx = ddx; dy = ddy; break; }
        }
        if (dx is null || dy is null) return false;

        return SafeTriangle.Aims(last.X, last.Y, dx.Value, dy.Value, left, right, topY - Slack, bottom + Slack);
    }

    private static bool IsNearest(MenuItem item, RoutedEventArgs e)
        => e.Source is Visual v && ReferenceEquals(v.FindAncestorOfType<MenuItem>(includeSelf: true), item);
}

/// <summary>The pure geometry of <see cref="SubmenuSafeTriangle"/> (§2.5).</summary>
internal static class SafeTriangle
{
    /// <summary>
    /// Whether a pointer at (x, y) moving by (dx, dy) reaches the nearer vertical edge of the box
    /// [left, right] × [top, bottom] within that box's vertical extent — i.e. it is heading for the submenu.
    /// </summary>
    public static bool Aims(double x, double y, double dx, double dy, double left, double right, double top, double bottom)
    {
        var edge = x <= (left + right) / 2 ? left : right;
        if (dx == 0) return false;
        var t = (edge - x) / dx;
        if (t < 0) return false;
        var yAtEdge = y + t * dy;
        return yAtEdge >= top && yAtEdge <= bottom;
    }

    /// <summary>Whether (px, py) lies inside, or on the edge of, the triangle (ax,ay)-(bx,by)-(cx,cy).</summary>
    public static bool Contains(double ax, double ay, double bx, double by, double cx, double cy, double px, double py)
    {
        var d1 = Cross(px, py, ax, ay, bx, by);
        var d2 = Cross(px, py, bx, by, cx, cy);
        var d3 = Cross(px, py, cx, cy, ax, ay);
        var hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        var hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    private static double Cross(double px, double py, double ax, double ay, double bx, double by)
        => (px - bx) * (ay - by) - (ax - bx) * (py - by);
}
