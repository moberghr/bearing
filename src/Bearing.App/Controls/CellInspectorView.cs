using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using static Bearing.App.Controls.Tokens;
using Avalonia.Input.Platform;

namespace Bearing.App.Controls;

/// <summary>
/// The cell inspector: one cell's full value, shown either as formatted JSON or as the raw stored text
/// (design RESULTS_GRID §6). Header names the cell and offers copy + close; the JSON toolbar adds a
/// Formatted/Raw toggle, collapse/expand-all, and a find box that highlights matches in place.
/// <para>
/// Formatted used to be a <c>TreeView</c>; issue #34 replaced it with the actual JSON document,
/// syntax-coloured and selectable, because the tree was harder to navigate than the text it stood for.
/// Objects and arrays still fold — the chevrons sit in a gutter beside the lines they close, editor-style,
/// so folding happens *in* the document rather than instead of it. Rendering, fold arithmetic and find all
/// live in the pure <see cref="JsonText"/>; this class draws the result and holds which paths are folded.
/// </para>
/// <para>
/// Self-contained — it takes the value and a close callback, and never reaches back into the grid. The
/// collapsible pane it lives in is <see cref="InspectorPane"/>.
/// </para>
/// </summary>
public sealed class CellInspectorView : UserControl
{
    /// <summary>Above this many lines the value renders as uncoloured, unfoldable text: the coloured view
    /// lays out every line on each open and fold (~0.3 s at 4,000 lines), ruinous for a 50k-line one.</summary>
    private const int MaxColouredLines = 4000;

    /// <summary>Zoom range, kept in step with the <c>results.inspectorFontSize</c> setting's own limits so
    /// a zoom can always be written back to it.</summary>
    private const double MinFontSize = 8;
    private const double MaxFontSize = 32;

    private readonly string _raw;
    private readonly bool _isJson;
    private readonly Action<double>? _onFontSize;
    private double _fontSize;

    // The fold gutter is positioned by arithmetic, so the text's line box has to be a known height rather
    // than whatever the font asks for — and every metric has to follow the zoom.
    private double LineHeight => Math.Ceiling(_fontSize * 1.45);
    private double GutterWidth => Math.Ceiling(_fontSize) + 5;
    private double ChevronSize => Math.Round(_fontSize);

    private readonly IReadOnlyList<JsonLine> _lines;
    private readonly HashSet<string> _folded = new();
    private readonly ContentControl _bodyHost = new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    // The formatted view's three controls live as long as the inspector does, and every re-render refills
    // them in place. Handing the ScrollViewer fresh content instead would reset its offset to zero for a
    // frame before it could be restored, so folding a node halfway down a value visibly bounced off the top.
    private readonly ScrollViewer _jsonScroller = new()
    {
        Padding = new Thickness(4, 8, 8, 8),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private readonly JsonTextBlock _jsonText = new(kind => Res(ColorKeyForSpan(kind)))
    {
        FontFamily = MonoFont,
        TextWrapping = TextWrapping.NoWrap,   // structure over reflow; Raw wraps if that's what you want
        VerticalAlignment = VerticalAlignment.Top,
        Foreground = Res(ColorKeyForSpan(JsonSpanKind.Punctuation)),
    };

    private readonly Cursor _handCursor = new(StandardCursorType.Hand);

    /// <summary>Top, not the default Stretch: a Canvas with an explicit Height gets *centred* in the
    /// leftover space, which parked the chevrons halfway down the pane whenever the document was short.</summary>
    private readonly Canvas _jsonGutter = new() { VerticalAlignment = VerticalAlignment.Top };

    private readonly FindHighlightLayer _findLayer;

    /// <summary>What the formatted view drew last — find marks positions on these without redrawing.</summary>
    private IReadOnlyList<JsonRow> _rows = Array.Empty<JsonRow>();

    private TextBlock? _matchCount;
    private bool _formatted = true;
    private string _find = "";

    /// <summary>The hits on <see cref="_rows"/>, and which one Enter / the arrows are on.</summary>
    private IReadOnlyList<JsonMatch> _matches = Array.Empty<JsonMatch>();
    private int _current;

    /// <summary>Build an inspector for a result's cell.</summary>
    public static CellInspectorView For(
        ResultSetViewModel result, int index, object?[] row, Action onClose,
        double fontSize = 13, Action<double>? onFontSize = null)
        => new(
            title: ResultMetaText.InspectorTitle(result, index, row),
            raw: GridSelectionOps.CellText(row, index),
            typeName: result.Columns[index].DataTypeName,
            onClose: onClose,
            fontSize: fontSize,
            onFontSize: onFontSize);

    public CellInspectorView(
        string title, string raw, string typeName, Action onClose,
        double fontSize = 13, Action<double>? onFontSize = null)
    {
        _raw = raw;
        _fontSize = Math.Clamp(fontSize, MinFontSize, MaxFontSize);
        _onFontSize = onFontSize;
        var parsed = JsonTree.Parse(raw);
        // A declared json/jsonb column, or any value whose text opens like a document.
        _isJson = parsed is not null && (ColumnKinds.IsJson(typeName) || ColumnKinds.LooksJson(raw));
        _lines = _isJson ? JsonText.Render(parsed!) : Array.Empty<JsonLine>();
        _findLayer = new FindHighlightLayer(_jsonText)
        {
            Brush = Tint("Accent.Brand", 0x40),
            CurrentBrush = Tint("Accent.Brand", 0xA0),
        };

        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(BuildHeader(title, typeName, onClose));
        if (BuildJsonToolbar() is { } toolbar) panel.Children.Add(toolbar);
        panel.Children.Add(_bodyHost);
        RenderBody();

        Content = new Border
        {
            Background = Res("Bg.Editor"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = SeparatorBrush,
            Child = panel, // width comes from the resizable grid column
        };

        // Tunnelling: the ScrollViewer inside would otherwise scroll instead of zooming.
        AddHandler(PointerWheelChangedEvent, OnPointerWheel, RoutingStrategies.Tunnel);
    }

    /// <summary>Ctrl+wheel zooms the value text, a point per notch, and reports the new size so it sticks
    /// for the next value you open (and shows up in Settings ▸ Results).</summary>
    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;

        var next = Math.Clamp(_fontSize + Math.Sign(e.Delta.Y), MinFontSize, MaxFontSize);
        e.Handled = true;
        if (next == _fontSize) return;

        _fontSize = next;
        RenderBody();          // in-place: folds and scroll position survive a zoom
        _onFontSize?.Invoke(next);
    }

    /// <summary>Header: <c>film[42].description</c> + a type badge, then copy and close.</summary>
    private Control BuildHeader(string title, string typeName, Action onClose)
    {
        var caption = new TextBlock
        {
            Text = title,
            Foreground = Res("Text.Primary"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // The declared type wins where there is one worth naming (json, jsonb, xml); "json" is for a value
        // that only *looks* like a document, and "text" for everything else. An xml value lands on the
        // third arm rather than the second: it is a document, so it keeps its type name, but it is not JSON
        // and _isJson is false for it (the tree would have nothing to render).
        var badge = ResultChrome.Badge(
            ColumnKinds.IsDocument(typeName) ? typeName.ToLowerInvariant() : _isJson ? "json" : "text",
            _isJson || ColumnKinds.IsDocument(typeName) ? "Syntax.Table" : "Text.Dim");

        // Drawn, not typed: the ⧉ and ✕ glyphs aren't in every UI font and came out clipped.
        var copy = ResultChrome.GlyphIconButton("M4,4 H12 V12 H4 Z M1.5,9 V1.5 H9", "Copy value");
        // The whole document, from the same renderer the view uses — never the folded placeholders.
        copy.Click += (_, _) => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(
            _isJson ? JsonText.Plain(_lines) : _raw);
        var close = ResultChrome.GlyphIconButton("M1.5,1.5 L11.5,11.5 M11.5,1.5 L1.5,11.5", "Close", size: 11);
        close.Click += (_, _) => onClose();

        var titleWrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleWrap.Children.Add(caption);
        titleWrap.Children.Add(badge);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        Grid.SetColumn(titleWrap, 0);
        Grid.SetColumn(copy, 2);
        Grid.SetColumn(close, 3);
        row.Children.Add(titleWrap);
        row.Children.Add(copy);
        row.Children.Add(close);

        var header = new Border
        {
            Background = Res("Bg.Window"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = SeparatorBrush,
            Padding = new Thickness(10, 6),
            Child = row,
        };
        DockPanel.SetDock(header, Dock.Top);
        return header;
    }

    /// <summary>Formatted/Raw toggle + fold-all + find with a match count. Null for a non-JSON value.</summary>
    private Control? BuildJsonToolbar()
    {
        if (!_isJson) return null;

        var fmtToggle = ResultChrome.IconTextButton("Formatted", "Show indented JSON");
        var rawToggle = ResultChrome.IconTextButton("Raw", "Show the stored text");
        void SyncToggles()
        {
            fmtToggle.Foreground = _formatted ? Res("Accent.Brand") : Res("Text.Dim");
            rawToggle.Foreground = _formatted ? Res("Text.Dim") : Res("Accent.Brand");
        }
        fmtToggle.Click += (_, _) => { _formatted = true; SyncToggles(); RenderBody(); };
        rawToggle.Click += (_, _) => { _formatted = false; SyncToggles(); RenderBody(); };
        SyncToggles();

        var collapseAll = ResultChrome.IconTextButton("⊟", "Collapse all", fontSize: 17);
        collapseAll.Click += (_, _) =>
        {
            _folded.Clear();
            // Not the root: collapsing it hides the whole value, and the user can still fold it by hand.
            foreach (var path in JsonText.FoldablePaths(_lines, includeRoot: false)) _folded.Add(path);
            _formatted = true;
            SyncToggles();
            RenderBody();
        };
        var expandAll = ResultChrome.IconTextButton("⊞", "Expand all", fontSize: 17);
        expandAll.Click += (_, _) =>
        {
            _folded.Clear();
            _formatted = true;
            SyncToggles();
            RenderBody();
        };

        var findBox = new TextBox { PlaceholderText = "Find in value…", Width = 150, Margin = new Thickness(8, 0, 0, 0) };
        findBox.TextChanged += (_, _) =>
        {
            _find = findBox.Text ?? "";
            var before = _folded.Count;
            _folded.ExceptWith(JsonText.PathsToReveal(_lines, _find));   // a match can't hide inside a fold
            if (!_formatted) return;
            _current = 0;   // a new query starts at its first hit
            // Only an opened fold changes the document; otherwise the query only moves highlights.
            if (_folded.Count != before) RenderBody();
            else ShowMatches();
            RevealCurrent();
        };
        // Field-local, like the sidebar's search boxes: Enter steps through the hits, Shift+Enter back.
        findBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Step(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        };

        var previous = ResultChrome.IconTextButton("↑", "Previous match (Shift+Enter)");
        previous.Click += (_, _) => Step(-1);
        var next = ResultChrome.IconTextButton("↓", "Next match (Enter)");
        next.Click += (_, _) => Step(1);

        _matchCount = new TextBlock
        {
            Foreground = Res("Text.Dim"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var tb = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tb.Children.Add(fmtToggle);
        tb.Children.Add(rawToggle);
        tb.Children.Add(collapseAll);
        tb.Children.Add(expandAll);
        tb.Children.Add(findBox);
        tb.Children.Add(previous);
        tb.Children.Add(next);
        tb.Children.Add(_matchCount);

        var toolbar = new Border
        {
            Background = Res("Bg.Chrome"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = SeparatorBrush,
            Padding = new Thickness(8, 4),
            Child = tb,
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        return toolbar;
    }

    private void RenderBody()
    {
        if (_isJson && _formatted)
        {
            _bodyHost.Content = BuildJsonView();
            return;
        }
        if (_matchCount is not null) _matchCount.Text = "";     // find only applies to the formatted view
        // Wrapped text runs to the full width, so an overlay bar covers the last characters of every line
        // it crosses. The inspector exists to read a value in full; that is the one pane where losing the
        // end of a line matters most.
        var wrapped = new ScrollViewer { Content = PlainText(_raw, TextWrapping.Wrap) };
        ScrollViewer.SetAllowAutoHide(wrapped, false);
        _bodyHost.Content = wrapped;
    }

    /// <summary>
    /// The value as indented JSON: one coloured string in a single selectable block,
    /// with a fold chevron beside every line that opens a container. Refills the standing controls, which
    /// is what keeps the scroll position across a fold or a keystroke in find.
    /// </summary>
    private Control BuildJsonView()
    {
        _rows = JsonText.Flatten(_lines, _folded);
        ShowMatches();

        if (_lines.Count > MaxColouredLines)
            return new ScrollViewer
            {
                Content = PlainText(JsonText.Plain(_lines), TextWrapping.NoWrap),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };

        if (_jsonScroller.Content is null)
        {
            var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(_jsonGutter, 0);
            Grid.SetColumn(_findLayer, 1);
            Grid.SetColumn(_jsonText, 1);
            body.Children.Add(_jsonGutter);
            body.Children.Add(_findLayer);   // before the text, so it draws behind it
            body.Children.Add(_jsonText);
            _jsonScroller.Content = body;
        }

        FillText(_rows);
        FillGutter(_rows);
        return _jsonScroller;
    }

    /// <summary>Count the hits on the drawn rows and hand them to the highlight layer. The huge-value
    /// fallback has no layer, so it still counts but marks nothing — as before.</summary>
    private void ShowMatches()
    {
        _matches = JsonText.Find(_rows, _find);
        _current = _matches.Count == 0 ? 0 : Math.Clamp(_current, 0, _matches.Count - 1);
        if (_matchCount is not null)
            _matchCount.Text = _find.Trim().Length == 0 ? ""
                : _matches.Count == 0 ? "no matches"
                : $"{_current + 1} of {_matches.Count}";
        _findLayer.SetMatches(_matches, _current);
    }

    /// <summary>Move to the next (+1) or previous (-1) hit, wrapping at either end, and scroll to it.</summary>
    private void Step(int direction)
    {
        if (!_formatted || _matches.Count == 0) return;
        _current = (_current + direction + _matches.Count) % _matches.Count;
        ShowMatches();
        RevealCurrent();
    }

    /// <summary>
    /// Scroll the current hit into view with a few lines of context. Posted below layout priority: after a
    /// fold opens, the hit's position only exists once the new text has been measured.
    /// </summary>
    private void RevealCurrent()
    {
        if (_matches.Count == 0 || _lines.Count > MaxColouredLines) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_findLayer.CurrentRectangle() is { } hit)
                _findLayer.BringIntoView(hit.Inflate(new Thickness(GutterWidth * 2, LineHeight * 3)));
        }, DispatcherPriority.Background);
    }

    /// <summary>Replace the document's text. One string and one layout, so the scroll offset only ever gets
    /// clamped, never zeroed.</summary>
    private void FillText(IReadOnlyList<JsonRow> rows)
    {
        _jsonText.FontSize = _fontSize;
        _jsonText.LineHeight = LineHeight;
        _jsonText.SetDocument(JsonText.Join(rows));
    }

    /// <summary>
    /// Rebuild the chevron column. A <see cref="Canvas"/> rather than a control per line: only container
    /// lines get a visual, and each one is placed at its row's exact offset — which is why the text block
    /// pins <see cref="JsonLineHeight"/> instead of letting the font decide.
    /// </summary>
    private void FillGutter(IReadOnlyList<JsonRow> rows)
    {
        _jsonGutter.Children.Clear();
        _jsonGutter.Width = GutterWidth;
        _jsonGutter.Height = rows.Count * LineHeight;

        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].FoldPath is not { } path) continue;

            var chevron = new TextBlock
            {
                Text = rows[i].IsFolded ? "▸" : "▾",
                FontSize = ChevronSize,
                Width = GutterWidth,
                Height = LineHeight,
                LineHeight = LineHeight,              // centres the glyph in its row
                TextAlignment = TextAlignment.Center,
                Foreground = Res("Text.Faint"),
                Cursor = _handCursor,
            };
            chevron.PointerEntered += (_, _) => chevron.Foreground = Res("Text.Primary");
            chevron.PointerExited += (_, _) => chevron.Foreground = Res("Text.Faint");
            chevron.PointerPressed += (_, e) =>
            {
                if (!_folded.Add(path)) _folded.Remove(path);
                e.Handled = true;                     // don't start a text selection under the chevron
                RenderBody();
            };

            Canvas.SetTop(chevron, i * LineHeight);
            _jsonGutter.Children.Add(chevron);
        }
    }

    /// <summary>A read-only, selectable text box — the raw view, and the fallback for a huge value.</summary>
    private TextBox PlainText(string text, TextWrapping wrapping) => new()
    {
        Text = text,
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = wrapping,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Foreground = Res("Text.Code"),
        FontFamily = _isJson ? MonoFont : FontFamily.Default,
        FontSize = _fontSize,
        Margin = new Thickness(8),
    };

    private static string ColorKeyForSpan(JsonSpanKind kind) => kind switch
    {
        JsonSpanKind.Key => "Syntax.Func",
        JsonSpanKind.String => "Ok.Green",
        JsonSpanKind.Number => "Syntax.Number",
        JsonSpanKind.Keyword => "Syntax.Keyword",
        _ => "Text.Dim",
    };
}
