using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Controls;

/// <summary>
/// The line that shows where a dragged tab will land — "release here and it goes in this gap".
/// <para>
/// It lives in the window's overlay layer, like <see cref="DragGhost"/> and for the same reason: the gap it
/// marks is between two arranged tabs, so anything drawn inside the strip would have to be given a place in
/// the layout the strip has already decided. The overlay is painted over the top of it and measures nothing.
/// Hit-test invisible, so it can never be what the pointer is over.
/// </para>
/// <para>
/// A line rather than a gap opened up in the strip: sliding the tabs apart under the pointer re-arranges the
/// strip on every move, which moves the very tabs the drop position is read from.
/// </para>
/// </summary>
internal sealed class InsertionCaret : IDisposable
{
    /// <summary>Line width. Two pixels reads as a caret at every scaling; one disappears on a dark strip.</summary>
    private const double Thickness = 2;

    private readonly Visual _owner;
    private OverlayLayer? _layer;
    private Border? _line;

    public InsertionCaret(Visual owner) => _owner = owner;

    /// <summary>
    /// Put the caret at <paramref name="gap"/>, a zero-width rectangle in <paramref name="space"/>'s own
    /// coordinates — normally a tab's leading or trailing edge, and its height.
    /// </summary>
    public void MoveTo(Visual space, Rect gap)
    {
        if (gap.Height <= 0) { Hide(); return; }

        _layer ??= OverlayLayer.GetOverlayLayer(_owner);
        if (_layer is null) return;
        if (space.TranslatePoint(gap.TopLeft, _layer) is not { } at) { Hide(); return; }

        _line ??= Build();
        if (_line is null) return;

        // Centred on the gap, so the caret sits between two tabs rather than over the edge of one.
        Canvas.SetLeft(_line, at.X - Thickness / 2);
        Canvas.SetTop(_line, at.Y);
        _line.Height = gap.Height;
        _line.IsVisible = true;
    }

    private Border? Build()
    {
        if (_layer is null) return null;
        var line = new Border
        {
            Width = Thickness,
            Background = Res("Accent.Brand"),
            CornerRadius = new CornerRadius(1),
            IsHitTestVisible = false,
            IsVisible = false,
        };
        _layer.Children.Add(line);
        return line;
    }

    /// <summary>Take the caret off screen without discarding it — there is no gap under the pointer.</summary>
    public void Hide()
    {
        if (_line is not null) _line.IsVisible = false;
    }

    public void Dispose()
    {
        if (_line is not null) _layer?.Children.Remove(_line);
        _line = null;
        _layer = null;
    }
}
