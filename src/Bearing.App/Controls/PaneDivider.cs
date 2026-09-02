using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Controls;

/// <summary>
/// The one draggable seam: between stacked result sets, and between the editor and the results pane.
/// <para>
/// Both were a 4px <see cref="GridSplitter"/> painted in <c>Border</c> — the same brush as every static rule
/// in the app — with no cursor and no hover state. Avalonia's <c>GridSplitter</c> sets no resize cursor of its
/// own (there is no <c>SizeNorthSouth</c> string anywhere in <c>Avalonia.Controls</c>), so something you could
/// drag looked exactly like something you could not, and the only way to find out was to try.
/// </para>
/// <para>
/// A subclass that draws itself rather than a splitter wrapped in visuals, because a <c>GridSplitter</c>
/// resolves the rows it resizes from its own <c>Grid.Row</c> and its parent — it has to stay a direct child of
/// the grid, so it cannot be nested inside a panel that draws the seam for it.
/// </para>
/// <para>
/// The resting state stays quiet on purpose. A window can hold one of these per result set, and a divider
/// that announced itself all the time would be noise; the affordance is the cursor, and the grip appears
/// under the pointer.
/// </para>
/// </summary>
internal sealed class PaneDivider : GridSplitter
{
    /// <summary>The visible seam — unchanged, so no pane height is spent on the affordance.</summary>
    public const double Thickness = 4;

    /// <summary>
    /// What the row actually occupies, and what the pointer can grab. Wider than the seam because a 4px
    /// target is a miss more often than a hit; the extra pixels are transparent.
    /// </summary>
    public const double GrabThickness = 9;

    private const double GripWidth = 28;

    private bool _hovered;
    private bool _dragging;

    public PaneDivider()
    {
        ResizeDirection = GridResizeDirection.Rows;
        Height = GrabThickness;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        // Transparent rather than unset: an unset background is not hit-testable, which would shrink the grab
        // area back to the seam.
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);

        PointerEntered += (_, _) => Set(ref _hovered, true);
        PointerExited += (_, _) => Set(ref _hovered, false);
        // Hover and drag are tracked separately: the pointer leaves the divider during a drag as often as
        // not, and the seam going quiet mid-drag would read as having lost the grip.
        DragStarted += (_, _) => Set(ref _dragging, true);
        DragCompleted += (_, _) => Set(ref _dragging, false);
    }

    private bool Active => _hovered || _dragging;

    private void Set(ref bool field, bool value)
    {
        if (field == value) return;
        field = value;
        InvalidateVisual();
    }

    /// <summary>
    /// The seam, centred in the taller grab area, plus two hairlines while the divider is live.
    /// <para>
    /// Two rules rather than dots: the app draws no dotted anything, and a pair of lines says "this moves"
    /// without introducing a new mark.
    /// </para>
    /// </summary>
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        if (width <= 0) return;

        // Three states, graduated on purpose. A full-width accent bar on mere hover is a lot of colour for
        // passing the pointer over a boundary — and with one divider per result set, several of them lighting
        // up as the pointer crosses the pane would be worse than the invisibility this replaced. So hover is a
        // quiet brighten and the accent is reserved for the drag actually happening.
        var seam = _dragging ? Res("Accent.Brand") : _hovered ? Res("Text.Dim") : SeparatorBrush;
        var top = (Bounds.Height - Thickness) / 2;
        context.FillRectangle(seam, new Rect(0, top, width, Thickness));

        if (!Active) return;

        // On the seam itself, in the surface colour, so the grip reads as cut *out* of the highlighted bar
        // rather than drawn on top of it.
        var grip = Res("Bg.Editor");
        var left = (width - GripWidth) / 2;
        if (left < 0) return;
        context.FillRectangle(grip, new Rect(left, top + 1, GripWidth, 1));
        context.FillRectangle(grip, new Rect(left, top + Thickness - 2, GripWidth, 1));
    }
}
