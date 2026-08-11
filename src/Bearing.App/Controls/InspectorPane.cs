using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Controls;

/// <summary>
/// The collapsible right-hand pane the cell inspector lives in: a draggable splitter plus a content column
/// that collapses to zero width when nothing is being inspected.
/// <para>
/// Owns two things that must outlive a results rebuild — the width the user dragged the splitter to, and
/// which content is currently open — so opening or closing the inspector never has to rebuild the grids
/// (which would throw away their scroll position). Call <see cref="Wrap"/> once per rebuild to re-host the
/// pane beside fresh content; anything already open re-renders itself.
/// </para>
/// </summary>
public sealed class InspectorPane
{
    private const double MinOpenWidth = 240;

    private double _width = 400;          // remembered across open/close (the user can drag the splitter)
    private Func<Control>? _content;      // what is open, so a rebuild can re-render it
    private ColumnDefinition? _column;
    private ContentControl? _host;
    private GridSplitter? _splitter;

    /// <summary>Whether the pane is currently showing something.</summary>
    public bool IsOpen => _content is not null;

    /// <summary>Host <paramref name="body"/> in a grid with the splitter and this pane beside it. The two
    /// trailing columns sit at zero width while the pane is closed, so a closed pane is invisible.</summary>
    public Control Wrap(Control body)
    {
        var outer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(body, 0);
        outer.Children.Add(body);

        _splitter = new GridSplitter
        {
            Width = 4,
            ResizeDirection = GridResizeDirection.Columns,
            Background = SeparatorBrush,
            IsVisible = false,
        };
        Grid.SetColumn(_splitter, 1);
        outer.Children.Add(_splitter);

        _column = outer.ColumnDefinitions[2];
        _host = new ContentControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(_host, 2);
        outer.Children.Add(_host);

        Render(); // re-open if a rebuild happened while the pane was showing
        return outer;
    }

    /// <summary>Open the pane on <paramref name="content"/> (replacing anything already open). The builder is
    /// kept so a later rebuild can re-render the same view without the caller re-supplying it.</summary>
    public void Show(Func<Control> content)
    {
        _content = content;
        Render();
    }

    /// <summary>Close the pane, remembering the width the user had dragged it to.</summary>
    public void Hide()
    {
        _content = null;
        Render();
    }

    /// <summary>Populate (or clear) the live pane without a full rebuild — keeps grid scroll.</summary>
    private void Render()
    {
        if (_host is null || _column is null) return;
        if (_content is { } build)
        {
            _host.Content = build();
            _column.Width = new GridLength(_width, GridUnitType.Pixel);
            _column.MinWidth = MinOpenWidth;
            if (_splitter is not null) _splitter.IsVisible = true;
        }
        else
        {
            // Remember the dragged width before collapsing so re-opening keeps it.
            if (_column.Width.IsAbsolute && _column.Width.Value > 0) _width = _column.Width.Value;
            _host.Content = null;
            _column.MinWidth = 0;
            _column.Width = new GridLength(0);
            if (_splitter is not null) _splitter.IsVisible = false;
        }
    }
}
