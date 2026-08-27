using Avalonia;
using Avalonia.Collections;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Bearing.App.Connections;
using Path = Avalonia.Controls.Shapes.Path;   // not System.IO.Path (ImplicitUsings)

namespace Bearing.App.Controls;

/// <summary>
/// The connection-state beacon: the single mark that answers "is this session up", wherever that question is
/// asked — the toolbar status group, the status bar, every editor tab header, and the schema tree's server
/// rows (CONNECTION_STATUS rev. 2026-08-27 §2/§3).
///
/// <para>It replaced a chain / broken-chain pair whose two states differed only by a small gap in the middle
/// bar; at the 12–14px these actually render at, that gap was invisible and both states read as "a chain".
/// The beacon carries state in the <b>silhouette</b> instead, so it survives greyscale, colour-blind viewing
/// and small sizes — the disconnected mark loses its ring entirely and collapses to about a third of the
/// footprint, which is legible before any colour is:</para>
/// <list type="bullet">
///   <item><b>Connected</b> — filled core (r 3.4) inside a closed ring (r 8).</item>
///   <item><b>Connecting</b> — same filled core, ring dashed and pulsing 1 → .3 → 1 over a second.</item>
///   <item><b>Disconnected</b> — hollow core struck through by one diagonal, <i>no ring at all</i>.</item>
/// </list>
///
/// <para>Colour comes from the <c>Status.*</c> tokens and never from the connection's environment hue: the
/// two palettes are deliberately disjoint (see Tokens.axaml), so a disconnected production session is a red
/// beacon on a rose wash and neither borrows the other's meaning.</para>
///
/// <para>Built in code on the same 24×24 grid as <see cref="LineIcon"/>, and for the same reason: a
/// <c>Stretch</c> that fits each geometry to its own ink bounds would scale the ringless disconnected mark up
/// to the size of the connected one and throw away the loss-of-mass the design depends on. The
/// <see cref="Viewbox"/> scales the viewport, stroke included, so the weights below are in 24-unit space.</para>
/// </summary>
public sealed class ConnectionBeacon : Viewbox
{
    /// <summary>The 24×24 grid every geometry in the app is drawn on (see Icons.axaml's header).</summary>
    private const double Viewport = 24;

    // Circles as paths rather than EllipseGeometry so the whole mark lives in one coordinate vocabulary and
    // reads against the SVG in the handoff prototype without translation.
    private static readonly Geometry Core = Geometry.Parse("M12,8.6 A3.4,3.4 0 1 0 12,15.4 A3.4,3.4 0 1 0 12,8.6");
    private static readonly Geometry Ring = Geometry.Parse("M12,4 A8,8 0 1 0 12,20 A8,8 0 1 0 12,4");
    private static readonly Geometry Strike = Geometry.Parse("M6.5,17.5 L17.5,6.5");

    /// <summary>Avalonia's dash array is in units of <see cref="Shape.StrokeThickness"/>, unlike SVG's
    /// <c>stroke-dasharray</c>, which is in user units. The spec's "3 4" at stroke-width 2 is therefore
    /// 1.5, 2 here — writing 3, 2 would double the dash and close the ring back up.</summary>
    private static readonly AvaloniaList<double> RingDashes = new() { 1.5, 2 };

    public static readonly StyledProperty<ConnectionState> StateProperty =
        AvaloniaProperty.Register<ConnectionBeacon, ConnectionState>(nameof(State));

    // Two cores rather than one re-stroked core: the connected/connecting core is a fill with no stroke and
    // the disconnected one is a stroke with no fill, and swapping both on one Path per state change is more
    // moving parts than showing the right one.
    private readonly Path _filledCore = new() { Data = Core };
    private readonly Path _hollowCore = new()
    {
        Data = Core,
        StrokeThickness = 2.2,
        StrokeLineCap = PenLineCap.Round,
    };
    private readonly Path _ring = new()
    {
        Data = Ring,
        StrokeThickness = 2,
        StrokeLineCap = PenLineCap.Round,
    };
    private readonly Path _strike = new()
    {
        Data = Strike,
        StrokeThickness = 2,
        StrokeLineCap = PenLineCap.Round,
    };

    public ConnectionBeacon()
    {
        Width = Viewport;
        Height = Viewport;
        Child = new Panel
        {
            Width = Viewport,
            Height = Viewport,
            Children = { _ring, _filledCore, _hollowCore, _strike },
        };

        // The connecting pulse is a style animation on the ring rather than a timer this control owns: it
        // starts and stops with the pseudo-class, so nothing keeps ticking behind a tab that has settled.
        Styles.Add(new Style(x => x.OfType<ConnectionBeacon>().Class(":connecting").Descendant().Name("Ring"))
        {
            Animations =
            {
                new Animation
                {
                    Duration = TimeSpan.FromSeconds(1),
                    IterationCount = IterationCount.Infinite,
                    Easing = new LinearEasing(),
                    Children =
                    {
                        new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, 1d) } },
                        new KeyFrame { Cue = new Cue(0.5d), Setters = { new Setter(OpacityProperty, 0.3d) } },
                        new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, 1d) } },
                    },
                },
            },
        });
        _ring.Name = "Ring";

        Apply();
    }

    /// <summary>Which state to draw. Everything else — geometry, colour, the pulse — follows from it.</summary>
    public ConnectionState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty) Apply();
    }

    private void Apply()
    {
        var connected = State == ConnectionState.Connected;
        var connecting = State == ConnectionState.Connecting;
        var live = connected || connecting;

        _filledCore.IsVisible = live;
        _hollowCore.IsVisible = !live;
        _ring.IsVisible = live;              // the ring is the whole difference: gone when disconnected
        _strike.IsVisible = !live;

        var brush = Tokens.Res(connected ? "Status.Connected"
            : connecting ? "Status.Connecting"
            : "Status.Disconnected");

        _filledCore.Fill = brush;
        _hollowCore.Stroke = brush;
        _ring.Stroke = brush;
        _strike.Stroke = brush;
        _ring.StrokeDashArray = connecting ? RingDashes : null;

        PseudoClasses.Set(":connecting", connecting);
    }
}
