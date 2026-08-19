using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;   // not System.IO.Path (ImplicitUsings)

namespace Bearing.App.Controls;

/// <summary>
/// One line-icon geometry from <c>Themes/Icons.axaml</c>, rendered on the 24×24 grid it was drawn on.
/// <para>
/// This exists because <c>Stretch="Uniform"</c> fits a <see cref="Path"/> to the <i>geometry's own ink
/// bounds</i>, not to the authoring viewport: a 12×18 document and a 16×16 clock asked to fill the same
/// 20×20 box each get their own scale and their own offset inside it, so a row of icons that were drawn
/// aligned renders visibly unaligned (#5 — the Scripts glyph was the worst of them). Pinning the host
/// <see cref="Panel"/> to 24×24 and leaving the <see cref="Path"/> unstretched keeps every glyph in the
/// coordinate space it was authored in, which is the only thing that makes them agree. The connection toggle
/// in <c>MainWindow.axaml</c> already pins its own 24×24 panel inline for exactly this reason; this is that
/// pattern as a control, so the rail doesn't spell it out at every tile.
/// </para>
/// <para>
/// The <see cref="Viewbox"/> scales the whole viewport — stroke included — so <c>StrokeThickness</c> here is
/// in 24-unit space. It is 1.6, and the default size is the viewport itself: at 1:1 that reproduces exactly
/// the stroke weight the rail already had, so this stays a fix for the alignment and nothing else. A call
/// site that wants a smaller icon sets <c>Width</c>/<c>Height</c>, and the weight scales down with it (which
/// is why the connection toggle, drawn at the same 2-unit weight but rendered at 15, looks lighter).
/// </para>
/// <para>
/// The <b>stroke brush</b> is deliberately not set here: it comes from a style over the owning control
/// (<c>RadioButton.rail Path, Button.rail Path</c> binds it to the tile's Foreground, which is what recolors
/// the glyph on hover and when the tile is active). A <see cref="LineIcon"/> placed somewhere without such a
/// style has to supply one, or it renders nothing.
/// </para>
/// </summary>
public sealed class LineIcon : Viewbox
{
    /// <summary>The 24×24 grid every geometry in <c>Icons.axaml</c> is drawn on (see its header comment).</summary>
    private const double Viewport = 24;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));

    private readonly Path _path = new()
    {
        StrokeThickness = 1.6,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
    };

    public LineIcon()
    {
        Width = Viewport;
        Height = Viewport;
        Child = new Panel { Width = Viewport, Height = Viewport, Children = { _path } };
    }

    /// <summary>The icon geometry, e.g. <c>{StaticResource Icon.Scripts}</c>.</summary>
    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty) _path.Data = Data;
    }
}
