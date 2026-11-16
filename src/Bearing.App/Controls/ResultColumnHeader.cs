using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Controls;

/// <summary>
/// One results-grid column header (#106, design v4 §1 and §3): the name and its badges, then — right-aligned —
/// a dot while the column has a filter and the sort indicator; under it, while the column-filter row is open,
/// the column's filter input.
/// <para>
/// <b>The indicator is its own click target.</b> A click anywhere else on the header still selects the whole
/// column (#6); <c>GridSelectionController</c> recognises the indicator and the input by
/// <see cref="ChromeClass"/> and leaves the selection alone. <c>▲</c>/<c>▼</c> show on the sorted column;
/// elsewhere <c>⇅</c> appears only while the pointer is over the header. Document columns (json, jsonb, xml)
/// have no indicator: they can be filtered, not sorted.
/// </para>
/// </summary>
internal sealed class ResultColumnHeader : StackPanel
{
    /// <summary>Marks the controls inside a header that act on their own, so a press on them is not a
    /// column-selecting header click.</summary>
    public const string ChromeClass = "headerChrome";

    /// <summary>The indicator's width — also added to a sortable column's opening width, since headers are
    /// sized to their name and the glyph would otherwise sit on its last letters.</summary>
    public const double SortGlyphWidth = 14;

    private readonly ResultFilterViewModel _filter;
    private readonly int _index;
    private readonly string _name;
    private readonly TextBlock _title;
    private readonly Border _dot;
    private readonly Button? _sort;
    private readonly TextBox _input;
    private bool _hovered;

    // The indicator is drawn, not typed. ⇅ ▲ ▼ as text came from whichever font had them — an arrow in one,
    // a triangle cropped by the 14px box in another. Geometry is the same everywhere, in an 8×10 box.
    private static readonly Geometry Ascending = Geometry.Parse("M0,7 L4,2 L8,7 Z");
    private static readonly Geometry Descending = Geometry.Parse("M0,3 L4,8 L8,3 Z");
    private static readonly Geometry Unsorted = Geometry.Parse("M1,4 L4,0.5 L7,4 Z M1,6 L4,9.5 L7,6 Z");

    private readonly Avalonia.Controls.Shapes.Path _glyph = new()
    {
        Width = 8,
        Height = 10,
        Stretch = Stretch.None,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public ResultColumnHeader(ResultSetViewModel result, int index, IEnumerable<(string Text, string Color)> badges)
    {
        _filter = result.Filter;
        _index = index;
        _name = result.Columns[index].Name;
        Orientation = Orientation.Vertical;
        VerticalAlignment = VerticalAlignment.Center;
        // Left, with a width of its own (TrackHeaderWidth): the template gives content a "*" column and keeps
        // an Auto column of ≥32px on the right for its own sort arrow, which this grid never shows. The
        // indicator belongs in that strip — so this starts at the content's left edge and runs over it.
        HorizontalAlignment = HorizontalAlignment.Left;

        var line = new DockPanel { LastChildFill = true, Background = Brushes.Transparent };

        if (_filter.IsSortable(index))
        {
            _sort = new Button
            {
                Classes = { ChromeClass },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                // A fixed, small target at the right edge. Left to size itself, the button grew across the
                // header and took the clicks that select the column (#6) — and at opacity 0, invisibly.
                Width = SortGlyphWidth,
                Height = SortGlyphWidth,
                MinHeight = 0,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                MinWidth = 0,
                Content = _glyph,
                HorizontalAlignment = HorizontalAlignment.Right,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = true,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            _sort.Click += (_, e) => { _filter.CycleSort(_index); e.Handled = true; };
            DockPanel.SetDock(_sort, Dock.Right);
            line.Children.Add(_sort);
        }
        else
        {
            ToolTip.SetTip(line, $"{_name} ({result.Columns[index].DataTypeName.ToLowerInvariant()} — not sortable; filter it instead)");
        }

        _dot = new Border
        {
            Width = 5,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = Res("Syntax.Func"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        DockPanel.SetDock(_dot, Dock.Right);
        line.Children.Add(_dot);

        var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _title = new TextBlock { Text = _name, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(_title);
        foreach (var (text, color) in badges) name.Children.Add(ResultChrome.Badge(text, color));
        line.Children.Add(name);
        Children.Add(line);

        _input = new TextBox
        {
            Classes = { ChromeClass },
            FontFamily = MonoFont,
            FontSize = 11,
            FontWeight = FontWeight.Normal,
            Padding = new Thickness(7, 2),
            MinHeight = 0,
            Margin = new Thickness(0, 3, 0, 2),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(0),
            Background = Res("Bg.Window"),
            PlaceholderText = Placeholder(result, index),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ToolTip.SetTip(_input,
            "Plain text matches anywhere in the value. Start with =, !=, >, <, >= or <= to compare — numerically when both sides are numbers.");
        _input.TextChanged += (_, _) => _filter.SetColumnFilter(_index, _input.Text);
        // The input lives inside the DataGrid, whose handlers read a TextBox source as an open cell editor and
        // whose own key handling would move the current row on Enter. Keys that mean something to the grid stop
        // here; Escape clears the text first, then leaves the input.
        _input.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape when !string.IsNullOrEmpty(_input.Text):
                    _input.Text = "";
                    e.Handled = true;
                    break;
                case Key.Enter or Key.Up or Key.Down or Key.PageUp or Key.PageDown:
                    e.Handled = true;
                    break;
            }
        };
        Children.Add(_input);

        PointerEntered += (_, _) => { _hovered = true; Sync(); };
        PointerExited += (_, _) => { _hovered = false; Sync(); };
        _filter.PropertyChanged += OnFilterChanged;
        DetachedFromVisualTree += (_, _) => _filter.PropertyChanged -= OnFilterChanged;
        AttachedToVisualTree += (_, _) =>
        {
            _filter.PropertyChanged -= OnFilterChanged;
            _filter.PropertyChanged += OnFilterChanged;
            TrackHeaderWidth();
            Sync();
        };
        DetachedFromVisualTree += (_, _) => { _headerWidth?.Dispose(); _headerWidth = null; };
        // Attach can come before the header's template has put this under the header; Loaded is after.
        Loaded += (_, _) => { if (_headerWidth is null) TrackHeaderWidth(); };
        Sync();
    }

    private System.IDisposable? _headerWidth;

    /// <summary>
    /// Take the header's full inner width. The DataGridColumnHeader template keeps a ≥32px column at its right
    /// for a sort arrow we never show, so stretched content stopped 32px short — which put the indicator in the
    /// middle of a narrow header, exactly where a column-selecting click lands (#6). The header's own width
    /// comes from the column, never from this content, so following it cannot feed back into it.
    /// </summary>
    private void TrackHeaderWidth()
    {
        _headerWidth?.Dispose();
        if (this.FindAncestorOfType<DataGridColumnHeader>() is not { } header) return;
        void Apply()
        {
            var inner = header.Bounds.Width - header.Padding.Left - header.Padding.Right - 2;
            if (inner > 0 && (double.IsNaN(Width) || System.Math.Abs(Width - inner) > 0.5)) Width = inner;
        }
        void OnHeaderChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == BoundsProperty || e.Property == TemplatedControl.PaddingProperty) Apply();
        }
        header.PropertyChanged += OnHeaderChanged;
        _headerWidth = new Unsubscribe(() => header.PropertyChanged -= OnHeaderChanged);
        Apply();
    }

    private sealed class Unsubscribe(System.Action undo) : System.IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>The column's filter input — for tests and the keyboard path into the row.</summary>
    internal TextBox FilterInput => _input;

    internal Button? SortButton => _sort;

    /// <summary>The column name as drawn — for tests that the header actually shows it.</summary>
    internal TextBlock Title => _title;

    /// <summary>The indicator's shape — for tests.</summary>
    internal Avalonia.Controls.Shapes.Path Glyph => _glyph;

    internal static Geometry GlyphFor(RowSort? sort)
        => sort is null ? Unsorted : sort.Direction == SortDirection.Ascending ? Ascending : Descending;

    private void OnFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ResultFilterViewModel.Sort)
            or nameof(ResultFilterViewModel.ColumnFilters)
            or nameof(ResultFilterViewModel.IsColumnFilterRowOpen))
            Sync();
    }

    private void Sync()
    {
        var sort = _filter.Sort is { } s && s.Column == _index ? s : null;
        // Cleared, never set to null, when unsorted: a local null is "no brush", not "inherit" — every unsorted
        // header name drew invisibly. Clearing hands it back to the header's own dim foreground.
        if (sort is not null) _title.Foreground = Res("Text.Primary");
        else _title.ClearValue(TextBlock.ForegroundProperty);

        var filterText = _filter.ColumnFilterText(_index);
        _dot.IsVisible = filterText.Length > 0;

        if (_sort is not null)
        {
            _glyph.Data = sort is null ? Unsorted : sort.Direction == SortDirection.Ascending ? Ascending : Descending;
            _glyph.Fill = sort is null ? Res("Text.Dim") : Res("Syntax.Func");
            // The idle glyph shows on hover or keyboard focus only — minimal at rest.
            _sort.Opacity = sort is not null || _hovered || _sort.IsFocused ? 1 : 0;
            var next = sort is null ? "ascending" : sort.Direction == SortDirection.Ascending ? "descending" : "unsorted";
            ToolTip.SetTip(_sort, $"Sort by {_name} — click for {next}");
        }

        _input.IsVisible = _filter.IsColumnFilterRowOpen;
        if (_input.Text != filterText && !_input.IsFocused) _input.Text = filterText;
        _input.Background = filterText.Length > 0 ? Tint("Syntax.Func", 0x1F) : Res("Bg.Window");
    }

    private static string Placeholder(ResultSetViewModel result, int index)
    {
        var column = result.Columns[index];
        if (CellStats.IsNumeric(column.ClrType)) return $"{column.Name} = …";
        return ColumnKinds.IsDocument(column.DataTypeName)
            ? $"{column.Name} contains… e.g. a key"
            : $"{column.Name} contains…";
    }
}
