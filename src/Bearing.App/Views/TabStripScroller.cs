using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Bearing.App.Views;

/// <summary>
/// Keeps the tab strip reachable once it is wider than the window (#65). The strip used to sit in a
/// horizontal <see cref="StackPanel"/>, which measures its children at infinite width — so the tabs past the
/// right edge were laid out and then simply clipped, with no scrollbar, no wheel, and no chevrons. The
/// <c>+</c> button went with them.
/// <para>
/// The XAML now docks <c>+</c> outside a <see cref="ScrollViewer"/> that holds the strip; this owns the two
/// behaviours that a ScrollViewer does not give for free:
/// </para>
/// <list type="bullet">
/// <item>the wheel scrolls the strip sideways, since there is nothing to scroll vertically and a vertical
/// wheel over a horizontal strip should still move it;</item>
/// <item>the selected tab is scrolled into view, so keyboard switching (Ctrl+PageUp/Down, Ctrl+Tab's MRU
/// cycle, goto-N) cannot land on a tab that is off screen — the tabs were never lost to the keyboard, only
/// to the eye, and this is what closes that gap.</item>
/// </list>
/// </summary>
internal sealed class TabStripScroller
{
    /// <summary>Horizontal pixels per wheel notch. About one narrow tab, so a notch reads as "next tab"
    /// rather than as a jump.</summary>
    private const double WheelStep = 48;

    private readonly ScrollViewer _scroller;
    private readonly TabStrip _strip;

    public TabStripScroller(ScrollViewer scroller, TabStrip strip)
    {
        _scroller = scroller;
        _strip = strip;
        // Tunnel: the strip's items would otherwise take the wheel first and the gesture would do nothing.
        _scroller.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    /// <summary>Scroll the strip by <paramref name="delta"/> horizontal pixels, clamped to what exists.
    /// A strip that fits entirely does not move.</summary>
    public void ScrollBy(double delta)
    {
        var maximum = _scroller.Extent.Width - _scroller.Viewport.Width;
        if (maximum <= 0) return;
        _scroller.Offset = _scroller.Offset.WithX(Clamp(_scroller.Offset.X + delta, 0, maximum));
    }

    /// <summary>
    /// Put the selected tab on screen. Posted rather than called inline: a selection change that came from
    /// the keyboard is followed by a layout pass, and asking to be brought into view before the container has
    /// been arranged scrolls to where the tab used to be.
    /// </summary>
    public void BringSelectionIntoView()
    {
        // No guard out here on purpose: this is called from the view-model's SelectedTab change, and the
        // strip's own SelectedItem binding has no defined ordering against it — so reading -1 now would
        // cancel a scroll that is needed, which is the case this exists for. The posted body re-reads it.
        Dispatcher.UIThread.Post(() =>
        {
            if (_strip.SelectedIndex >= 0 && _strip.ContainerFromIndex(_strip.SelectedIndex) is { } container)
                container.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // A tilt wheel reports X; an ordinary wheel reports Y, and over a strip that only scrolls sideways
        // that is still what the user means. Down/away scrolls right, matching every horizontal strip.
        var notches = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
        if (notches == 0) return;
        ScrollBy(-notches * WheelStep);
        e.Handled = true;
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}
