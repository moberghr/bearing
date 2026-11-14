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
using Path = Avalonia.Controls.Shapes.Path;   // not System.IO.Path (ImplicitUsings)

namespace Bearing.App.Controls;

/// <summary>
/// The sort affordance at the end of a result column's header (#106): ⇅ while the pointer is over the header,
/// ▲ / ▼ in the accent colour while the column is sorted. A click cycles ascending → descending → the query's
/// order.
/// <para>
/// Its own target rather than the header's click, because the header's click selects the column (#6).
/// <see cref="GridSelectionController.TrySelectFromHeader"/> recognises this type and leaves the selection
/// alone, and the press is marked handled here so the header does not start a column drag from it.
/// </para>
/// <para>
/// Hidden by opacity, not <c>IsVisible</c>, so the column's width (which reserves <see cref="Reserve"/>) is
/// the same sorted or not, and the header never shifts when the glyph appears. Invisible and clickable is not
/// a trap: it is only invisible while the pointer is elsewhere.
/// </para>
/// <para>
/// Subscribes to the result's sort and to its header only while attached: a result outlives every grid built
/// from it (§9.9b), so a subscription held past detach would pin a discarded header.
/// </para>
/// </summary>
public sealed class SortGlyph : Border
{
    // Drawn, not typed: ▲▼⇅ are not in every mono face (see ResultChrome's chevrons). 8×12 box.
    private const string BothData = "M0,5 L4,1 L8,5 Z M0,7 L4,11 L8,7 Z";
    private const string AscendingData = "M0,8 L4,3 L8,8 Z";
    private const string DescendingData = "M0,4 L4,9 L8,4 Z";

    private const double GlyphWidth = 10;
    private const double Gap = 5;

    /// <summary>Header width the glyph takes, gap included — reserved by the column width arithmetic.</summary>
    public const double Reserve = GlyphWidth + Gap;

    private readonly ResultSetViewModel _result;
    private readonly int _column;
    private readonly Action<SortDirection> _sort;
    private readonly Path _path = new()
    {
        Stretch = Stretch.None,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private DataGridColumnHeader? _header;

    /// <param name="sort">Asked for the direction one click moves to; the caller sorts and reports.</param>
    public SortGlyph(ResultSetViewModel result, int column, Action<SortDirection> sort)
    {
        _result = result;
        _column = column;
        _sort = sort;
        Width = GlyphWidth;
        Height = 14;
        Margin = new Thickness(Gap, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Background = Brushes.Transparent;   // the whole box is the target, not just the triangles' ink
        Cursor = new Cursor(StandardCursorType.Hand);
        Child = _path;
        // handledEventsToo: the DataGrid marks pointer presses handled in the tunnel phase.
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        Sync();
    }

    /// <summary>The direction this glyph's column is sorted in.</summary>
    public SortDirection Direction => _result.Sort.DirectionOf(_column);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _result.Sort.Changed += Sync;
        _header = this.FindAncestorOfType<DataGridColumnHeader>();
        if (_header is not null)
        {
            _header.PropertyChanged += OnHeaderPropertyChanged;
        }
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _result.Sort.Changed -= Sync;
        if (_header is not null)
        {
            _header.PropertyChanged -= OnHeaderPropertyChanged;
            _header = null;
        }
    }

    private void OnHeaderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == InputElement.IsPointerOverProperty)
        {
            Sync();
        }
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        e.Handled = true;
        _sort(ResultSort.Next(Direction));
    }

    private void Sync()
    {
        var direction = Direction;
        _path.Data = Geometry.Parse(direction switch
        {
            SortDirection.Ascending => AscendingData,
            SortDirection.Descending => DescendingData,
            _ => BothData,
        });
        _path.Fill = direction == SortDirection.None ? Res("Text.Faint") : Res("Accent.Brand");
        Opacity = direction != SortDirection.None || _header?.IsPointerOver == true ? 1 : 0;
        ToolTip.SetTip(this, ResultSort.Next(direction) switch
        {
            SortDirection.Ascending => "Sort ascending",
            SortDirection.Descending => "Sort descending",
            _ => "Clear sort — back to the query's order",
        });
    }
}
