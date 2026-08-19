using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using static Bearing.App.Controls.Tokens;
using Path = Avalonia.Controls.Shapes.Path;   // not System.IO.Path (ImplicitUsings)

namespace Bearing.App.Controls;

/// <summary>
/// The small labelled box that follows the pointer while a script is being dragged — "you are dragging this,
/// and it's going where the pointer is".
/// <para>
/// It is drawn rather than being a cursor because Avalonia offers no way to supply one: the cursor during a
/// drag belongs to the platform drag source, which sets it through an <c>internal</c> cursor override on the
/// presentation source (X11 sets it on the pointer grab instead), and an app-set <c>Cursor</c> is simply
/// overridden. So the affordance lives in the window's overlay layer, the same place the palette and
/// quick-pick overlays use, and is hit-test invisible so it can never take the drop.
/// </para>
/// </summary>
internal sealed class DragGhost : IDisposable
{
    /// <summary>Clearance from the pointer hotspot, so the box sits beside the arrow rather than under it.</summary>
    private const double Gap = 14;

    private readonly Visual _owner;
    private OverlayLayer? _layer;
    private Border? _box;

    public DragGhost(Visual owner) => _owner = owner;

    /// <summary>Put the box on screen, labelled with what's being dragged. Off-screen until the first
    /// <see cref="FollowPointer"/> positions it, so it can't flash at the origin.</summary>
    public void Show(string label)
    {
        if (_box is not null) return;
        _layer = OverlayLayer.GetOverlayLayer(_owner);
        if (_layer is null) return;

        _box = new Border
        {
            Background = Res("Bg.TileActive"),
            BorderBrush = Res("Accent.Brand"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3),
            IsHitTestVisible = false,   // the ghost must never be what a drop lands on
            IsVisible = false,
            BoxShadow = BoxShadows.Parse("0 2 8 #66000000"),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Path
                    {
                        Data = Application.Current?.FindResource("Icon.File") as Geometry,
                        Stroke = Res("Text.Dim"),
                        StrokeThickness = 1.4,
                        StrokeLineCap = PenLineCap.Round,
                        StrokeJoin = PenLineJoin.Round,
                        Stretch = Stretch.Uniform,
                        Width = 12,
                        Height = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = label,
                        FontSize = 11,
                        Foreground = Res("Text.Primary"),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };
        _layer.Children.Add(_box);
    }

    /// <summary>Move the box to the pointer for this drag event. Also reveals it — the pointer coming back
    /// over a drop surface after leaving is what un-hides it.</summary>
    public void FollowPointer(DragEventArgs e)
    {
        if (_layer is null || _box is null) return;

        _box.Measure(Size.Infinity);
        var at = Place(e.GetPosition(_layer), _box.DesiredSize, _layer.Bounds.Size);
        Canvas.SetLeft(_box, at.X);
        Canvas.SetTop(_box, at.Y);
        _box.IsVisible = true;
    }

    /// <summary>Hide the box without discarding it — the pointer has left every drop surface.</summary>
    public void Hide()
    {
        if (_box is not null) _box.IsVisible = false;
    }

    /// <summary>
    /// Pure: where the box's top-left goes for a pointer at <paramref name="pointer"/>. Below-right of the
    /// pointer normally, flipped to the other side of it when that would push the box past the edge of
    /// <paramref name="layer"/> — a label clipped by the window edge is where the file name matters most
    /// (the deepest folder rows are the ones furthest right).
    /// </summary>
    internal static Point Place(Point pointer, Size ghost, Size layer)
    {
        var x = pointer.X + Gap;
        if (x + ghost.Width > layer.Width) x = pointer.X - Gap - ghost.Width;
        var y = pointer.Y + Gap;
        if (y + ghost.Height > layer.Height) y = pointer.Y - Gap - ghost.Height;
        return new Point(Math.Max(0, x), Math.Max(0, y));
    }

    public void Dispose()
    {
        if (_box is not null) _layer?.Children.Remove(_box);
        _box = null;
        _layer = null;
    }
}
