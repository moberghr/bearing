using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Controls;

/// <summary>
/// One inline zone in the editor (spec: docs/specs/2026-11-06-inline-results.md): the real
/// <see cref="ResultView"/> — editing, FK navigation with Back, export, paging, inspector — plus the grip that
/// drags its height, inside a frame that keeps the editor out of it.
/// <para>
/// <b>The editor stays out.</b> The zone sits inside the editor's text view, so everything it does not handle
/// would otherwise reach AvaloniaEdit: a key moving the caret or editing the SQL, typed text landing in the
/// document, a click placing the caret. Keys the grid leaves unhandled are offered to the window's global
/// commands (Run, Save, tab switching…) and then stopped here; text input and presses stop here outright. The
/// editor's own tunnel handlers run <i>before</i> the zone sees a key, so they skip zone keys themselves
/// (<see cref="IsInside"/>).
/// </para>
/// </summary>
public sealed class InlineZoneView : Border
{
    /// <param name="view">The zone's results view (built with <see cref="ResultView.IsInline"/>).</param>
    /// <param name="globalKeys">Offer a key the grid didn't take to the window's global commands; true when
    /// one ran.</param>
    /// <param name="leave">Esc outside a cell editor: give the keyboard back to the editor.</param>
    public InlineZoneView(InlineResultZone zone, ResultView view, Func<KeyEventArgs, bool> globalKeys, Action leave)
    {
        Zone = zone;
        Results = view;
        Background = Res("Bg.Chrome");
        BorderBrush = Res("Border");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Margin = new Thickness(0, 2, 0, 6);
        HorizontalAlignment = HorizontalAlignment.Left;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);

        view.HorizontalAlignment = HorizontalAlignment.Left;
        view.InlineCap = zone.TableHeight ?? InlineGridSizing.DefaultCap(ResultGridChrome.RowMinHeight);

        ResizeGrip = Grip();
        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(ResizeGrip, Dock.Bottom);
        body.Children.Add(ResizeGrip);
        body.Children.Add(view);
        Child = body;

        // Esc leaves the zone — but an open cell editor takes the first one (it cancels the edit, §9.15).
        // Tunnel, ahead of the grid's own Escape (clear selection), which would otherwise keep focus here.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox)
            {
                e.Handled = true;
                leave();
            }
        }, RoutingStrategies.Tunnel);

        // What the grid left: the window's global commands get it, then nothing above the zone does.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!e.Handled)
            {
                globalKeys(e);
            }
            e.Handled = true;
        }, RoutingStrategies.Bubble);
        AddHandler(TextInputEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
        // A press anywhere in the zone never reaches the text area (caret placement, a text selection). The
        // grid's own handlers run with handledEventsToo and the buttons mark their presses handled already.
        AddHandler(PointerPressedEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
        AddHandler(ContextRequestedEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
        // GotFocus bubbles, and AvaloniaEdit's TextEditor answers focus arriving anywhere inside it by focusing
        // its TextArea — which took the keyboard straight back from a grid the user had just clicked. The
        // grid's own GotFocus handlers have run by the time it gets here.
        AddHandler(GotFocusEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
    }

    /// <summary>The zone this shows.</summary>
    public InlineResultZone Zone { get; }

    /// <summary>The zone's results view.</summary>
    public ResultView Results { get; }

    /// <summary>The strip along the bottom edge that drags the grids taller or shorter.</summary>
    public Border ResizeGrip { get; }

    /// <summary>Raised while the user drags the grip — the editor re-measures the line.</summary>
    public event Action? HeightChanged;

    /// <summary>Whether <paramref name="source"/> (an event's source) is inside an inline zone — for the
    /// editor's own tunnel handlers, which see a zone's keys and presses before the zone does.</summary>
    public static bool IsInside(object? source)
    {
        for (var node = source as Visual; node is not null; node = node.GetVisualParent())
        {
            if (node is InlineZoneView)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The bottom-edge grip. Dragging sets <see cref="InlineResultZone.TableHeight"/> (clamped by
    /// <see cref="InlineGridSizing.ClampCap"/>) and re-sizes the grids in place. The pointer is captured, so the
    /// drag keeps going when it leaves the strip — it stays attached because the editor reuses this control's
    /// host while its zones are unchanged.
    /// </summary>
    private Border Grip()
    {
        var grip = new Border
        {
            Height = 6,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
            Child = new Border
            {
                Width = 28,
                Height = 2,
                CornerRadius = new CornerRadius(1),
                Background = Res("Border.Control"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(grip, "Drag to resize");
        double? startY = null;
        double startCap = 0;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
            {
                return;
            }
            startY = e.GetPosition(this).Y;
            startCap = Results.InlineCap;
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (startY is not { } y0)
            {
                return;
            }
            var cap = InlineGridSizing.ClampCap(startCap + e.GetPosition(this).Y - y0, ResultGridChrome.RowMinHeight);
            if (Math.Abs(cap - Results.InlineCap) >= 1)
            {
                Zone.TableHeight = cap;
                Results.InlineCap = cap;
                HeightChanged?.Invoke();
            }
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) =>
        {
            startY = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        };
        grip.PointerCaptureLost += (_, _) => startY = null;
        return grip;
    }
}
