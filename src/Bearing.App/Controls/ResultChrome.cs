using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.Core.Workspace;
using static Bearing.App.Controls.Tokens;
using Path = Avalonia.Controls.Shapes.Path;

namespace Bearing.App.Controls;

/// <summary>
/// The small, stateless visual atoms the results dock is assembled from — a cell's value text, badges,
/// borderless buttons, the drawn glyph affordances, the read-only lock chip, the back bar and the dock
/// header's Stacked/Tabbed toggle (design RESULTS_GRID). Each returns a fresh control and holds no state, so
/// the composition code in <see cref="ResultView"/> is left with layout decisions only.
/// <para>
/// Every icon here is a vector <see cref="Path"/> rather than a font glyph: symbol glyphs (▸ ▾ ↗ ⤢ 🔒)
/// render clipped in the app font.
/// </para>
/// </summary>
public static class ResultChrome
{
    /// <summary>
    /// Content height a result's meta row always reserves, whether or not the commit group is showing.
    /// <para>
    /// The group (● N pending · Discard · Save) appears on the first pending edit and is a pixel taller than
    /// the row's other buttons, so revealing it grew the meta row and re-measured the grid beneath it —
    /// moving the row the user had just edited (#60). Reserving the taller state costs the clean row one
    /// pixel and costs the dirty row nothing, which is the right way round.
    /// </para>
    /// <para>
    /// A measured number, not a guess: 22 is the commit group's own height at the button metrics in
    /// <see cref="ResultEditToolbar"/>. <c>Ui.ResultGridScrollTests</c> asserts the grid does not change
    /// height across the first edit, so if those metrics move this fails rather than drifts.
    /// </para>
    /// </summary>
    public const double MetaRowContentHeight = 22;

    // Filled collapse triangles. Right = collapsed, down = expanded.
    private const string ChevronRightData = "M0,0 L5,4 L0,8 Z";
    private const string ChevronDownData = "M0,0 L8,0 L4,5 Z";

    /// <summary>The collapse triangle's geometry for a given fold state (re-assigned as it toggles).</summary>
    public static Geometry ChevronGeometry(bool collapsed)
        => Geometry.Parse(collapsed ? ChevronRightData : ChevronDownData);

    /// <summary>A collapse chevron plus its padded hit target. The caller wires the click (it owns the
    /// fold state) and re-points <c>Glyph.Data</c> at <see cref="ChevronGeometry"/> when it flips.</summary>
    public static (Border Hit, Path Glyph) Chevron(bool collapsed, bool visible)
    {
        var glyph = new Path
        {
            Fill = Res("Text.Faint"),
            Data = ChevronGeometry(collapsed),
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var hit = new Border
        {
            Child = glyph,
            Background = Brushes.Transparent,
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = visible,
            Cursor = visible ? new Cursor(StandardCursorType.Hand) : Cursor.Default,
        };
        return (hit, glyph);
    }

    /// <summary>A 9px/700 tinted chip appended after a column name (teal PK, violet FK, mint jsonb) or used
    /// as the inspector's type badge.</summary>
    public static Control Badge(string text, string colorKey)
        => new Border
        {
            Background = Tint(colorKey, 0x33),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0),
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = Metric("Font.Caption"),
                FontWeight = FontWeight.Bold,
                Foreground = Res(colorKey),
            },
        };

    /// <summary>A borderless, dim, hand-cursor button for subtle inline actions. Each space-separated
    /// token (icon glyph, word) is its own vertically-centered TextBlock so a tall icon glyph doesn't
    /// enlarge the label's line-box and knock the words out of alignment with icon-less buttons.</summary>
    public static Button SubtleButton(string content, string tip)
    {
        var tokens = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var t in content.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            tokens.Children.Add(new TextBlock { Text = t, FontSize = Metric("Font.Body"), VerticalAlignment = VerticalAlignment.Center });

        var b = new Button
        {
            Content = tokens,
            FontSize = Metric("Font.Body"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2),
            Foreground = Res("Text.Dim"),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>A borderless text/glyph button used for inspector controls (copy, close, toggles) and the
    /// stats bar's Clear.</summary>
    public static Button IconTextButton(string content, string tip, double? fontSize = null)
    {
        var b = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2),
            Foreground = Res("Text.Dim"),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        if (fontSize is { } size) b.FontSize = size;
        ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>
    /// A button whose icon is <i>drawn</i> rather than typed. Symbol glyphs (⧉, ✕) are not in every UI font,
    /// and a fallback face renders them at the wrong advance width — clipped, or as tofu. A stroked path
    /// always looks the same. Same transparent chrome as <see cref="IconTextButton"/>.
    /// </summary>
    public static Button GlyphIconButton(string data, string tip, double size = 13)
    {
        var b = new Button
        {
            Content = new Path
            {
                Data = Geometry.Parse(data),
                Stroke = Res("Text.Dim"),
                StrokeThickness = 1.2,
                StrokeLineCap = PenLineCap.Round,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(7, 4),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>
    /// The text inside a value cell, in the one place that decides how a value looks: dimmed italic for a
    /// NULL, code colour for a number, primary text otherwise, with the grid's text inset and ellipsis
    /// trimming.
    /// <para>
    /// Shared rather than per-cell-kind because it drifted the moment it was not: the foreign-key cell built
    /// its own TextBlock and set neither <c>Foreground</c> nor <c>FontStyle</c>, so a NULL FK rendered as
    /// bright upright text — the one column where "(null)" looked like a real value (#61) — and a live FK
    /// value inherited the theme's plain white instead of <c>Text.Primary</c>. A third cell kind now cannot
    /// drift the same way.
    /// </para>
    /// </summary>
    /// <param name="numeric">Numbers get <c>Text.Code</c>. Foreign keys pass false even though they are
    /// usually integers: they are identifiers, and the grid already sets them apart with the FK badge and the
    /// jump glyph.</param>
    public static TextBlock ValueText(string text, bool isNull, bool numeric)
        => new()
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(ResultGridChrome.CellTextMargin, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = isNull ? NullBrush : (numeric ? Res("Text.Code") : Res("Text.Primary")),
            FontStyle = isNull ? FontStyle.Italic : FontStyle.Normal,
        };

    /// <summary>A small drawn "⤢" inspect icon that opens the cell inspector.</summary>
    public static Control InspectAffordance()
        => GlyphButton("M1,6 V1 H6 M1,1 L6,6 M13,8 V13 H8 M13,13 L8,8", Res("Syntax.Table"),
            width: 18, height: 16, margin: new Thickness(2, 0, 2, 0), tip: "Inspect value");

    /// <summary>A drawn "↗" jump icon that navigates to a foreign key's referenced row.</summary>
    public static Control JumpAffordance()
        => GlyphButton("M1,9 L9,1 M4,1 L9,1 L9,6", LinkBrush,
            width: 16, height: 16, margin: new Thickness(2, 0, 4, 0), tip: "Open referenced row");

    /// <summary>A bool cell's indicator, drawn rather than a Fluent <c>CheckBox</c>: at 14px it sits inside
    /// the 26px row instead of forcing it to Fluent's 32px minimum. Checked fills with the accent and a white
    /// tick, unchecked is an empty outline, NULL an outline with a dim dash — the CheckBox's own three states.
    /// <para>
    /// Its bounds are exactly the visible box, which is what the click-to-toggle gesture hit-tests against
    /// (<c>GridSelectionController.TryToggleBoolAtPointer</c>) — a Fluent CheckBox reserved room for a label
    /// this never has, so a click beside or above the box counted as a click on it.
    /// </para></summary>
    public static Control BoolIndicator(bool? value)
    {
        var box = new Border
        {
            Width = BoolBoxSize,
            Height = BoolBoxSize,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = value == true ? AccentFill : Res("Border.Control"),
            Background = value == true ? AccentFill : Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (value == false) return box;   // an empty outline; nothing to draw inside
        box.Child = new Path
        {
            // A tick on the accent fill, or the indeterminate dash a NULL shows.
            Data = Geometry.Parse(value == true ? "M0,3 L2.8,5.8 L7.6,0.6" : "M0,0 L6,0"),
            Stroke = value == true ? Brushes.White : Res("Text.Faint"),
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return box;
    }

    /// <summary>Side of the bool indicator's box. Kept under the grid's 26px row floor on purpose.</summary>
    private const double BoolBoxSize = 14;

    /// <summary>The checked fill: the platform accent, which is what the Fluent CheckBox this replaced was
    /// filling with. Deliberately not a Bearing token — the desktop's accent (GNOME's blue, a Windows user
    /// accent) is what the indicator looked like before it was drawn by hand, and hardcoding one blue would
    /// freeze whichever machine it was sampled on. Falls back to the palette's azure if the theme exposes no
    /// platform accent, so a missing key can't render an invisible box.</summary>
    private static IBrush AccentFill
        => Application.Current?.FindResource("SystemAccentColor") is Color accent
            ? new SolidColorBrush(accent)
            : Res("Syntax.Func");

    /// <summary>A stroked vector glyph wrapped in a transparent Border so the whole box is the hit target.</summary>
    private static Border GlyphButton(string data, IBrush stroke, double width, double height, Thickness margin, string tip)
    {
        var glyph = new Path
        {
            Data = Geometry.Parse(data),
            Stroke = stroke,
            StrokeThickness = 1.3,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new Border
        {
            Child = glyph,
            Background = Brushes.Transparent,
            Width = width,
            Height = height,
            Margin = margin,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(box, tip);
        return box;
    }

    /// <summary>An amber padlock chip for a locked (read-only) result; the reason lives in the tooltip
    /// (design RESULTS_GRID §8).</summary>
    /// <summary>
    /// The body of a result that has no grid — a statement message or an error. One line of text, so it takes
    /// the data font size rather than inheriting the frame's, and it is inset to the same left edge as a
    /// grid's first column so a run of mixed results lines up.
    /// </summary>
    public static Control ResultText(string text, IBrush foreground, bool wrap = false)
        => new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontSize = ResultGridChrome.CellFontSize,
            Margin = new Thickness(ResultGridChrome.HeaderPadding + 2, 8),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };

    public static Control LockChip(string reason)
    {
        var padlock = new Path
        {
            Data = Geometry.Parse("M2,5 h7 v6 h-7 z M3.5,5 v-1.5 a2,2 0 0 1 4,0 v1.5"),
            Stroke = Res("Accent.Brand"),
            StrokeThickness = 1.2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chip = new Border
        {
            Background = Tint("Accent.Brand", 0x1E),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Help),
            Child = padlock,
        };
        ToolTip.SetTip(chip, $"Read-only — {reason}");
        return chip;
    }

    /// <summary>A slim "‹ Back" bar that returns to the pre-navigation result (foreign-key history).</summary>
    public static Control BackBar(Action onBack)
    {
        var arrow = new Path
        {
            Data = Geometry.Parse("M5,1 L1,5 L5,9 M1,5 L10,5"),
            Stroke = LinkBrush,
            StrokeThickness = 1.4,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        inner.Children.Add(arrow);
        inner.Children.Add(new TextBlock { Text = "Back", Foreground = LinkBrush, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        var back = new Button
        {
            Content = inner,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        back.Click += (_, _) => onBack();

        return new Border
        {
            Background = Res("Bg.Window"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = SeparatorBrush,
            Padding = new Thickness(6, 2),
            Child = back,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    /// <summary>The persistent dock header: a RESULTS label plus the segmented Stacked/Tabbed toggle.
    /// <paramref name="onPick"/> fires only for a mode other than <paramref name="active"/>.</summary>
    public static Control DockHeader(ResultsViewMode active, Action<ResultsViewMode> onPick)
    {
        var label = new TextBlock
        {
            Text = "RESULTS",
            FontSize = Metric("Font.Small"),
            FontWeight = FontWeight.Bold,
            Foreground = Res("Text.Dim"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var toggle = ViewToggle(active, onPick);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(label);
        grid.Children.Add(toggle);

        return new Border
        {
            Background = Res("Bg.Window"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = SeparatorBrush,
            Padding = new Thickness(10, 5),
            Child = grid,
        };
    }

    /// <summary>Segmented Stacked/Tabbed control: the active segment is filled with the tile highlight.</summary>
    private static Control ViewToggle(ResultsViewMode active, Action<ResultsViewMode> onPick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Segment("▤ Stacked", ResultsViewMode.Stacked, active, onPick));
        row.Children.Add(Segment("▭ Tabbed", ResultsViewMode.Tabbed, active, onPick));

        return new Border
        {
            Background = Res("Bg.Window"),
            BorderThickness = new Thickness(1),
            BorderBrush = SeparatorBrush,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(2),
            Child = row,
        };
    }

    private static Control Segment(string text, ResultsViewMode mode, ResultsViewMode active, Action<ResultsViewMode> onPick)
    {
        var isActive = active == mode;
        var tb = new TextBlock
        {
            Text = text,
            FontSize = Metric("Font.Small"),
            FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = isActive ? Res("Text.Primary") : Res("Text.Dim"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var seg = new Border
        {
            Child = tb,
            Background = isActive ? Res("Bg.TileActive") : Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 2),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        seg.PointerPressed += (_, _) => { if (!isActive) onPick(mode); };
        return seg;
    }
}
