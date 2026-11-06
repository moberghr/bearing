using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Controls;

/// <summary>
/// One inline result block, hosted inside the editor beneath its statement (spec:
/// docs/specs/2026-11-06-inline-results.md). Read-only and deliberately small: a header, at most
/// <see cref="VisibleRows"/> rows before it scrolls, a meta line, and two actions — close, and open in the
/// results pane, which is where editing, FK navigation and export live.
/// <para>
/// Plain text cells rather than a <c>DataGrid</c>: the editor measures inline objects with infinite space,
/// and nothing in here may take keyboard focus — focus inside the text view would route typing and the
/// editor's key bindings somewhere the user isn't looking. Clicks are swallowed at the root so they never
/// move the caret or start a text selection underneath.
/// </para>
/// </summary>
public sealed class InlineResultView : Border
{
    /// <summary>Rows visible before the table scrolls inside the editor.</summary>
    public const int VisibleRows = 10;

    /// <summary>Rows built at most; the rest are a click away in the pane.</summary>
    public const int MaxRenderedRows = 200;

    private const double MaxCellWidth = 320;

    /// <param name="showInPane">The placement toggle: stop showing results inline and show these in the pane.
    /// Null hides the button.</param>
    public InlineResultView(InlineResultZone zone, Action close, Action openInPane, Action? showInPane = null)
    {
        Zone = zone;
        Focusable = false;
        Cursor = new Cursor(StandardCursorType.Arrow);
        Background = Res("Bg.Chrome");
        BorderBrush = Res("Border");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Margin = new Thickness(0, 2, 0, 6);
        Padding = new Thickness(0, 0, 0, 4);
        HorizontalAlignment = HorizontalAlignment.Left;

        CloseButton = ActionButton("M2,2 L10,10 M10,2 L2,10", "Close these results", close);
        OpenInPaneButton = ActionButton("M1,6 V1 H6 M1,1 L6,6 M11,6 V11 H6 M11,11 L6,6",
            "Open in the results pane — edit, follow foreign keys, export", openInPane);
        PlacementButton = showInPane is null ? null
            : ActionButton(PaneGlyph, "Show results in the pane instead of inline", showInPane);

        var body = new StackPanel { Orientation = Orientation.Vertical };
        body.Children.Add(TopBar(zone));
        for (var i = 0; i < zone.Sets.Count; i++)
        {
            var set = zone.Sets[i];
            if (zone.Sets.Count > 1)
            {
                body.Children.Add(Meta(set, $"Result {i + 1} · "));
            }
            body.Children.Add(SetBody(set));
        }
        ResizeGrip = Grip();
        body.Children.Add(ResizeGrip);
        Child = body;
        ApplyTableHeight();

        // Never let a press reach the text area: it would move the caret under the table and start a text
        // selection. The buttons have already acted (they mark their own presses handled).
        AddHandler(PointerPressedEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
        AddHandler(TextInputEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
    }

    /// <summary>The zone this block shows.</summary>
    public InlineResultZone Zone { get; }

    /// <summary>✕ — removes the zone.</summary>
    public Button CloseButton { get; }

    /// <summary>⤢ — shows the zone's sets in the results pane.</summary>
    public Button OpenInPaneButton { get; }

    /// <summary>The placement toggle (back to the pane), or null when the host offered none.</summary>
    public Button? PlacementButton { get; }

    /// <summary>The strip along the bottom edge that drags the table taller or shorter.</summary>
    public Border ResizeGrip { get; }

    /// <summary>Raised while the user drags the table to a new height — the editor re-measures the line.</summary>
    public event Action? HeightChanged;

    /// <summary>The results-pane glyph: a window with its lower part filled — results below, not inline.</summary>
    internal const string PaneGlyph = "M1,1 H11 V11 H1 Z M1,7 H11";

    private double DefaultTableHeight => ResultGridChrome.RowMinHeight * VisibleRows;

    private const int MinVisibleRows = 2;
    private const int MaxVisibleRows = 60;

    /// <summary>The rows' scroll areas, one per grid set — what the grip resizes.</summary>
    private readonly System.Collections.Generic.List<ScrollViewer> _rowScrolls = new();

    private Control TopBar(InlineResultZone zone)
    {
        var bar = new DockPanel { Margin = new Thickness(0, 2, 2, 2), LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        if (PlacementButton is not null)
        {
            actions.Children.Add(PlacementButton);
        }
        actions.Children.Add(OpenInPaneButton);
        actions.Children.Add(CloseButton);
        DockPanel.SetDock(actions, Dock.Right);
        bar.Children.Add(actions);
        bar.Children.Add(zone.Caption is { } caption
            ? Label(caption, Res("Text.Dim"))
            : Meta(zone.Sets[0], prefix: ""));
        return bar;
    }

    private static TextBlock Meta(ResultSetViewModel set, string prefix)
    {
        var elapsed = ResultSetBuilder.FormatElapsed(set.Duration.TotalMilliseconds);
        string what;
        if (!set.Success)
        {
            what = "error";
        }
        else if (!set.HasGrid)
        {
            what = "done";
        }
        else
        {
            var rows = set.Loaded;
            what = set.HasMore ? $"first {rows:N0} rows · more in pane" : rows == 1 ? "1 row" : $"{rows:N0} rows";
            if (rows > MaxRenderedRows)
            {
                what += $" · {MaxRenderedRows:N0} shown";
            }
        }
        return Label($"{prefix}{what} · {elapsed}", Res("Text.Dim"));
    }

    private static TextBlock Label(string text, IBrush brush) => new()
    {
        Text = text,
        Foreground = brush,
        FontSize = Metric("Font.Small"),
        Margin = new Thickness(ResultGridChrome.HeaderPadding + 2, 2),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private Control SetBody(ResultSetViewModel set)
    {
        if (!set.Success)
        {
            return Message($"Error: {set.Error?.Message}", Res("Error.Red"));
        }
        if (!set.HasGrid)
        {
            return Message(set.Message ?? "Statement executed.", Res("Text.Code"));
        }
        return Table(set);
    }

    private static TextBlock Message(string text, IBrush brush) => new()
    {
        Text = text,
        Foreground = brush,
        FontSize = ResultGridChrome.CellFontSize,
        FontFamily = MonoFont,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(ResultGridChrome.HeaderPadding + 2, 4),
    };

    /// <summary>Header and rows share column widths through shared size groups, so the header can stay put
    /// while the rows scroll under it.</summary>
    private Control Table(ResultSetViewModel set)
    {
        var rowHeight = ResultGridChrome.RowMinHeight;
        var columns = set.Columns.Count;

        Grid NewGrid()
        {
            var g = new Grid();
            for (var c = 0; c < columns; c++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = $"c{c}" });
            }
            return g;
        }

        var header = NewGrid();
        header.Background = Res("Bg.Window");
        for (var c = 0; c < columns; c++)
        {
            var name = new TextBlock
            {
                Text = set.Columns[c].Name,
                FontSize = ResultGridChrome.HeaderFontSize,
                FontWeight = FontWeight.SemiBold,
                Foreground = Res("Text.Dim"),
                Margin = new Thickness(ResultGridChrome.HeaderPadding, 4),
                MaxWidth = MaxCellWidth,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, c);
            header.Children.Add(name);
        }

        var rows = NewGrid();
        var shown = set.Rows.Take(MaxRenderedRows).ToList();
        for (var r = 0; r < shown.Count; r++)
        {
            rows.RowDefinitions.Add(new RowDefinition(new GridLength(rowHeight)));
            if (r % 2 == 1)
            {
                var stripe = new Border { Background = Res("Bg.Hover") };
                Grid.SetRow(stripe, r);
                Grid.SetColumnSpan(stripe, Math.Max(1, columns));
                rows.Children.Add(stripe);
            }
            for (var c = 0; c < columns; c++)
            {
                var row = shown[r];
                var isNull = c >= row.Length || row[c] is null;
                var cell = ResultChrome.ValueText(
                    isNull ? "NULL" : ResultCellFactory.DisplayText(set, row, c),
                    isNull, numeric: CellStats.IsNumeric(set.Columns[c].ClrType));
                cell.FontSize = ResultGridChrome.CellFontSize;
                cell.MaxWidth = MaxCellWidth;
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                rows.Children.Add(cell);
            }
        }

        var rowScroll = new ScrollViewer
        {
            Content = rows,
            MaxHeight = rowHeight * VisibleRows,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Focusable = false,
        };
        _rowScrolls.Add(rowScroll);
        var table = new StackPanel { Orientation = Orientation.Vertical };
        Grid.SetIsSharedSizeScope(table, true);
        table.Children.Add(header);
        table.Children.Add(rowScroll);

        return new ScrollViewer
        {
            Content = table,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Focusable = false,
        };
    }

    private void ApplyTableHeight()
    {
        var height = Zone.TableHeight ?? DefaultTableHeight;
        foreach (var scroll in _rowScrolls)
        {
            scroll.MaxHeight = height;
        }
    }

    /// <summary>
    /// The bottom-edge grip. Dragging sets <see cref="InlineResultZone.TableHeight"/> (between
    /// <see cref="MinVisibleRows"/> and <see cref="MaxVisibleRows"/> rows) and applies it to every table in the
    /// zone. The pointer is captured, so the drag keeps going when it leaves the strip — it stays attached
    /// because the editor reuses this control's host while its zones are unchanged.
    /// </summary>
    private Border Grip()
    {
        var grip = new Border
        {
            Height = 6,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
            Margin = new Thickness(0, 2, 0, -4),
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
        double startHeight = 0;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
            {
                return;
            }
            startY = e.GetPosition(this).Y;
            startHeight = Zone.TableHeight ?? DefaultTableHeight;
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (startY is not { } y0)
            {
                return;
            }
            var row = ResultGridChrome.RowMinHeight;
            var height = Math.Clamp(startHeight + e.GetPosition(this).Y - y0, row * MinVisibleRows, row * MaxVisibleRows);
            if (Math.Abs(height - (Zone.TableHeight ?? DefaultTableHeight)) >= 1)
            {
                Zone.TableHeight = height;
                ApplyTableHeight();
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

    private static Button ActionButton(string glyph, string tip, Action action)
    {
        var button = new Button
        {
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(5),
            MinWidth = 0,
            MinHeight = 0,
            Content = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(glyph),
                Stroke = Res("Text.Muted"),
                StrokeThickness = 1.4,
                Width = 12,
                Height = 12,
                Stretch = Stretch.None,
            },
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        return button;
    }
}
