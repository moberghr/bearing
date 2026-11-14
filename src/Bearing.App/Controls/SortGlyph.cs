using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using static Bearing.App.Controls.Tokens;
using Path = Avalonia.Controls.Shapes.Path;   // not System.IO.Path (ImplicitUsings)

namespace Bearing.App.Controls;

/// <summary>
/// The sort affordance at the end of a result column's header (#106): ⇅ while the pointer is over the header,
/// ▲ / ▼ in the accent colour while the column is sorted. A click cycles ascending → descending → the query's
/// order.
/// <para>
/// Its own target rather than the header's click, because the header's click selects the column (#6).
/// <see cref="GridSelectionController.TrySelectFromHeader"/> recognises this type and leaves the selection
/// alone; the press is marked handled here so the header does not start a column drag from it, and the
/// double-tap so two quick clicks are two sorts rather than an auto-fit.
/// </para>
/// <para>
/// Shown by style, not code (<see cref="ResultGridChrome"/>): transparent by default, opaque under a hovered
/// header or while <c>:sorted</c>. Opacity rather than <c>IsVisible</c>, so the column's width (which reserves
/// <see cref="Reserve"/>) is the same either way and the header never shifts when the glyph appears.
/// </para>
/// <para>
/// Subscribes to the result's sort only while attached: a result outlives every grid built from it (§9.9b),
/// so a subscription held past detach would pin a discarded header.
/// </para>
/// </summary>
public sealed class SortGlyph : Border
{
    // Drawn, not typed: ▲▼⇅ are not in every mono face (see ResultChrome's chevrons). 8×12 box.
    private static readonly Geometry Both = Geometry.Parse("M0,5 L4,1 L8,5 Z M0,7 L4,11 L8,7 Z");
    private static readonly Geometry Ascending = Geometry.Parse("M0,8 L4,3 L8,8 Z");
    private static readonly Geometry Descending = Geometry.Parse("M0,4 L4,9 L8,4 Z");
    private static readonly Cursor Hand = new(StandardCursorType.Hand);

    private const double GlyphWidth = 10;
    private const double Gap = 5;

    /// <summary>Header width the glyph takes, gap included — reserved by the column width arithmetic.</summary>
    public const double Reserve = GlyphWidth + Gap;

    private readonly ResultSetViewModel _result;
    private readonly int _column;
    private readonly Action<ResultSetViewModel, int, SortDirection> _sort;
    private readonly Path _path = new()
    {
        Stretch = Stretch.None,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private SortDirection? _shown;

    /// <param name="sort">Asked for the direction one click moves to; the caller sorts and reports.</param>
    public SortGlyph(ResultSetViewModel result, int column, Action<ResultSetViewModel, int, SortDirection> sort)
    {
        _result = result;
        _column = column;
        _sort = sort;
        Width = GlyphWidth;
        Height = 14;
        Margin = new Thickness(Gap, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Background = Brushes.Transparent;   // the whole box is the target, not just the triangles' ink
        Cursor = Hand;
        Child = _path;
        // handledEventsToo: the DataGrid marks pointer presses handled in the tunnel phase.
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        DoubleTapped += (_, e) => e.Handled = true;
        Sync();
    }

    /// <summary>The direction this glyph's column is sorted in.</summary>
    public SortDirection Direction => _result.Sort.DirectionOf(_column);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _result.Sort.Changed += Sync;
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _result.Sort.Changed -= Sync;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        e.Handled = true;
        _sort(_result, _column, ResultSort.Next(Direction));
    }

    private void Sync()
    {
        var direction = Direction;
        if (direction == _shown)
        {
            return;
        }
        _shown = direction;
        _path.Data = direction switch
        {
            SortDirection.Ascending => Ascending,
            SortDirection.Descending => Descending,
            _ => Both,
        };
        _path.Fill = direction == SortDirection.None ? Res("Text.Faint") : Res("Accent.Brand");
        PseudoClasses.Set(":sorted", direction != SortDirection.None);
        ToolTip.SetTip(this, ResultSort.Next(direction) switch
        {
            SortDirection.Ascending => "Sort ascending",
            SortDirection.Descending => "Sort descending",
            _ => "Clear sort — back to the query's order",
        });
    }
}
