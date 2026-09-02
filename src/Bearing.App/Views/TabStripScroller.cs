using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bearing.App.Input;

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
/// <item>the wheel over the strip switches tabs (see <see cref="SelectionStep"/>), with Shift+wheel left as
/// the pan — a vertical wheel over a horizontal strip has to mean <i>something</i>, and "the next tab" is
/// the one people reach for it expecting (it is Firefox's and every IDE's gesture). Panning the strip is
/// still what Shift does, and what the chevron and the selection-follows-scroll below cover;</item>
/// <item>the selected tab is scrolled into view, so keyboard switching (Ctrl+PageUp/Down, Ctrl+Tab's MRU
/// cycle, goto-N) cannot land on a tab that is off screen — the tabs were never lost to the keyboard, only
/// to the eye, and this is what closes that gap;</item>
/// <item>it counts how many tabs are off the edge, which is what the strip's chevron shows.</item>
/// </list>
/// <para>
/// The scrollbar itself is deliberately <b>not</b> the affordance: it was the first fix for #65 and the wrong
/// one. On a strip this short it is a thin target, and it answers "there is more" without answering "more of
/// what" — so finding a particular tab still meant cycling blind. The chevron and its list answer the second
/// question, which is the one the user actually has.
/// </para>
/// </summary>
internal sealed class TabStripScroller
{
    /// <summary>Horizontal pixels per wheel notch for the Shift+wheel pan. About one narrow tab, so a notch
    /// reads as "one tab along" rather than as a jump.</summary>
    private const double WheelStep = 48;

    private readonly ScrollViewer _scroller;
    private readonly TabStrip _strip;

    /// <summary>Banks trackpad fractions so a swipe is a couple of tabs and not the whole strip; a mouse
    /// notch arrives as ±1 and spends immediately.</summary>
    private readonly WheelNotches _notches = new();

    /// <summary>
    /// Move the selection <c>±1</c> tab, returning whether it moved. Set by the window, which is the only
    /// thing that knows the two rows are one strip: the drawn order spans both (<c>TabNavigator</c>), so a
    /// wheel that runs off the end of the pinned row carries on into the unpinned one instead of stopping at
    /// a row boundary the user never drew.
    /// <para>
    /// Left null the strip only pans, which is what the bare fixtures in the tests do.
    /// </para>
    /// </summary>
    public Func<int, bool>? SelectionStep { get; set; }

    public TabStripScroller(ScrollViewer scroller, TabStrip strip)
    {
        _scroller = scroller;
        _strip = strip;
        // Tunnel: the strip's items would otherwise take the wheel first and the gesture would do nothing.
        _scroller.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);

        // Three things move the count, and all three have to be watched or the chevron goes stale: scrolling
        // (a tab leaves the far edge as another arrives at the near one), resizing the window, and the strip's
        // contents changing. The last is why LayoutUpdated is here rather than a collection-changed handler —
        // a new tab has no bounds until it is arranged, so the count would be one short if read any earlier.
        _scroller.ScrollChanged += (_, _) => { RaiseOverflowChanged(); SyncEdgeFade(); };
        _scroller.LayoutUpdated += (_, _) => { RaiseOverflowChanged(); SyncEdgeFade(); };
    }

    /// <summary>How many pixels the cut edge fades over. Enough to read as a dissolve rather than a hard
    /// stop, and short enough that it never eats a whole label.</summary>
    private const double FadeWidth = 26;

    /// <summary>
    /// Fade the edge that has tabs beyond it, so a tab at the boundary dissolves instead of ending
    /// mid-glyph (<c>store-health</c> cut to <c>store-he</c> reads as a broken label, not as a tab that
    /// carries on off-screen).
    /// <para>
    /// An <c>OpacityMask</c> and not a layout change, deliberately: it is consumed in the paint, so it
    /// measures nothing and cannot feed back into the layout that produced it — which is the trap the
    /// chevron's own width fell into. Which edges qualify is <see cref="TabOverflow.FadeEdges"/>.
    /// </para>
    /// </summary>
    private void SyncEdgeFade()
    {
        var (left, right) = TabOverflow.FadeEdges(
            _scroller.Offset.X, _scroller.Extent.Width, _scroller.Viewport.Width);

        // The fade is a fixed number of pixels, but gradient stops are fractions of the vector — so the
        // fraction depends on the current width. Quantised to keep the brush cache small: a strip resizing by
        // a pixel does not need a new brush, and the visual difference is invisible.
        var span = Math.Max(_scroller.Viewport.Width, 1);
        var fraction = Math.Round(Math.Min(0.4, FadeWidth / span), 3);

        var mask = EdgeFade(left, right, fraction);
        if (ReferenceEquals(_scroller.OpacityMask, mask)) return;
        _scroller.OpacityMask = mask;
    }

    // Cached and immutable. Immutable is not tidiness: a mutable brush is an AvaloniaObject that binds to the
    // dispatcher of whichever thread built it, so a static cache filled on a test worker thread throws
    // VerifyAccess out of the compositor the moment a visual on the UI thread uses it (§4.5).
    private static readonly Dictionary<(bool, bool, double), IImmutableBrush?> FadeCache = new();

    private static IImmutableBrush? EdgeFade(bool left, bool right, double fraction)
    {
        var key = (left, right, fraction);
        if (FadeCache.TryGetValue(key, out var cached)) return cached;

        IImmutableBrush? brush = null;
        if (left || right)
        {
            var stops = new GradientStops();
            // Alpha only: the mask multiplies whatever the strip painted, so the colour is irrelevant and
            // only the opacity ramp matters.
            stops.Add(new GradientStop(left ? Colors.Transparent : Colors.White, 0));
            if (left) stops.Add(new GradientStop(Colors.White, fraction));
            if (right) stops.Add(new GradientStop(Colors.White, 1 - fraction));
            stops.Add(new GradientStop(right ? Colors.Transparent : Colors.White, 1));

            brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops = stops,
            }.ToImmutable();
        }

        FadeCache[key] = brush;
        return brush;
    }

    private int _lastHidden = -1;

    /// <summary>Announce a change only when the number actually changed. LayoutUpdated fires constantly, and
    /// re-running the chevron's binding on every frame is how a strip with many tabs starts to feel heavy.</summary>
    private void RaiseOverflowChanged()
    {
        var hidden = HiddenCount();
        if (hidden == _lastHidden) return;
        _lastHidden = hidden;
        OverflowChanged?.Invoke();
    }

    /// <summary>Raised when the number of tabs off the edge may have changed — a scroll, a resize, a tab
    /// opening or closing. The chevron listens.</summary>
    public event Action? OverflowChanged;

    /// <summary>
    /// How many tabs are not <b>fully</b> visible, and so belong in the chevron's list.
    /// <para>
    /// Read from the realized containers rather than estimated from <c>Extent - Viewport</c>: the count has to
    /// name tabs, not pixels, and a strip of unequal tab widths (one long filename beside three short ones)
    /// has no fixed pixels-per-tab to divide by. The arithmetic is <see cref="TabOverflow"/>.
    /// </para>
    /// <para>
    /// <paramref name="reserve"/> is width the caller intends to take away from the strip — the chevron's own
    /// footprint. It exists because the chevron is docked beside the strip, so showing it narrows the viewport
    /// this count is read from: the count would then change <i>because</i> the chevron appeared, re-run, and
    /// change again. That is not hypothetical, it threw <c>Infinite layout loop detected</c> on the first
    /// render capture. Reserving the width unconditionally makes the answer independent of its own effect.
    /// </para>
    public int HiddenCount(double reserve = 0)
        => TabOverflow.HiddenCount(Spans(), _scroller.Offset.X, _scroller.Viewport.Width - reserve);

    /// <summary>Each realized tab's (start, width) along the strip, in the strip's own coordinates.</summary>
    private IReadOnlyList<(double Start, double Width)> Spans()
    {
        var spans = new List<(double, double)>();
        for (var i = 0; i < _strip.ItemCount; i++)
        {
            // An unrealized container has no bounds to judge, and the DataGrid-style virtualization that
            // would produce one does not apply to a TabStrip of this size — but a container is also null
            // during the first pass, before anything is arranged, so this is not a theoretical branch.
            if (_strip.ContainerFromIndex(i) is not { Bounds.Width: > 0 } container) continue;
            spans.Add((container.Bounds.X, container.Bounds.Width));
        }
        return spans;
    }

    /// <summary>Scroll the strip by <paramref name="delta"/> horizontal pixels, clamped to what exists.
    /// A strip that fits entirely does not move.</summary>
    /// <returns>Whether the strip actually moved — false when it already fits, or is already at that end.</returns>
    public bool ScrollBy(double delta)
    {
        var maximum = _scroller.Extent.Width - _scroller.Viewport.Width;
        if (maximum <= 0) return false;
        var target = Clamp(_scroller.Offset.X + delta, 0, maximum);
        if (target == _scroller.Offset.X) return false;
        _scroller.Offset = _scroller.Offset.WithX(target);
        return true;
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
        // A tilt wheel reports X; an ordinary wheel reports Y, and over a strip that runs sideways that is
        // still what the user means. Down/away means forward — the next tab, the direction the strip used to
        // scroll.
        var delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
        if (delta == 0) return;

        // Shift is the pan: peek along a long strip without dragging the editor, the results and the
        // connection to another tab on the way. It is the old gesture, kept rather than deleted, because
        // moving the selection is not always what you want from a strip you are only reading.
        if (SelectionStep is not { } step || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Handled only if the strip moved. This runs in the tunnel phase, so swallowing a wheel that did
            // nothing would leave the gesture dead over a strip that already fits.
            if (ScrollBy(-delta * WheelStep)) e.Handled = true;
            return;
        }

        var notches = _notches.Add(-delta);
        if (notches == 0)
        {
            // A trackpad fraction, banked toward the next tab. Consumed rather than passed on: unhandled it
            // reaches the ScrollViewer's own wheel logic, and the strip would then drift sideways under a
            // gesture that means "switch tabs" while the selection waited for a whole notch.
            e.Handled = true;
            return;
        }

        // One tab at a time even for a burst, so a fast flick lands on the tab it passed through last rather
        // than skipping the ones between — and so the end of the strip stops it (StepSelection does not wrap).
        var direction = Math.Sign(notches);
        var moved = false;
        for (var i = 0; i < Math.Abs(notches); i++) moved |= step(direction);
        // At either end nothing moved, so the wheel is left for whoever else wants it — the same rule as the
        // pan above. Nothing else does want it: a strip whose selection cannot move that way is either at
        // its own scroll limit or short enough to fit, so this reads as inert rather than as a dead gesture.
        if (moved) e.Handled = true;
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}
