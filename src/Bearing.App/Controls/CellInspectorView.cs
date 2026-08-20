using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
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
    /// <summary>Above this many lines the value renders as uncoloured, unfoldable text: one
    /// <see cref="Run"/> per span is fine for a document, ruinous for a 50k-line one.</summary>
    private const int MaxColouredLines = 4000;

    // The fold gutter is positioned by arithmetic, so the text's line box has to be a known height.
    private const double JsonFontSize = 13;      // matches the result grids
    private const double JsonLineHeight = 19;
    private const double GutterWidth = 16;
    private const double ChevronSize = 11;

    private readonly string _raw;
    private readonly bool _isJson;
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

    private readonly SelectableTextBlock _jsonText = new()
    {
        FontFamily = MonoFont,
        FontSize = JsonFontSize,
        LineHeight = JsonLineHeight,
        TextWrapping = TextWrapping.NoWrap,   // structure over reflow; Raw wraps if that's what you want
        VerticalAlignment = VerticalAlignment.Top,
    };

    /// <summary>Top, not the default Stretch: a Canvas with an explicit Height gets *centred* in the
    /// leftover space, which parked the chevrons halfway down the pane whenever the document was short.</summary>
    private readonly Canvas _jsonGutter = new()
    {
        Width = GutterWidth,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private TextBlock? _matchCount;
    private bool _formatted = true;
    private string _find = "";

    /// <summary>Build an inspector for a result's cell.</summary>
    public static CellInspectorView For(ResultSetViewModel result, int index, object?[] row, Action onClose)
        => new(
            title: ResultMetaText.InspectorTitle(result, index, row),
            raw: GridSelectionOps.CellText(row, index),
            typeName: result.Columns[index].DataTypeName,
            onClose: onClose);

    public CellInspectorView(string title, string raw, string typeName, Action onClose)
    {
        _raw = raw;
        var parsed = JsonTree.Parse(raw);
        // A declared json/jsonb column, or any value whose text opens like a document.
        _isJson = parsed is not null && (ColumnKinds.IsJson(typeName) || ColumnKinds.LooksJson(raw));
        _lines = _isJson ? JsonText.Render(parsed!) : Array.Empty<JsonLine>();

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
        var badge = ResultChrome.Badge(
            _isJson ? (ColumnKinds.IsJson(typeName) ? typeName.ToLowerInvariant() : "json") : "text",
            _isJson ? "Syntax.Table" : "Text.Dim");

        var copy = ResultChrome.IconTextButton("⧉", "Copy value");
        // The whole document, from the same renderer the view uses — never the folded placeholders.
        copy.Click += (_, _) => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(
            _isJson ? JsonText.Plain(_lines) : _raw);
        var close = ResultChrome.IconTextButton("✕", "Close");
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

        var collapseAll = ResultChrome.IconTextButton("⊟", "Collapse all");
        collapseAll.Click += (_, _) =>
        {
            _folded.Clear();
            foreach (var path in JsonText.FoldablePaths(_lines)) _folded.Add(path);
            _formatted = true;
            SyncToggles();
            RenderBody();
        };
        var expandAll = ResultChrome.IconTextButton("⊞", "Expand all");
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
            _folded.ExceptWith(JsonText.PathsToReveal(_lines, _find));   // a match can't hide inside a fold
            if (_formatted) RenderBody();
        };

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
        _bodyHost.Content = new ScrollViewer { Content = PlainText(_raw, TextWrapping.Wrap) };
    }

    /// <summary>
    /// The value as indented JSON: one coloured <see cref="Run"/> per span in a single selectable block,
    /// with a fold chevron beside every line that opens a container. Refills the standing controls, which
    /// is what keeps the scroll position across a fold or a keystroke in find.
    /// </summary>
    private Control BuildJsonView()
    {
        var rows = JsonText.Highlight(JsonText.Flatten(_lines, _folded), _find, out var matches);
        if (_matchCount is not null)
            _matchCount.Text = _find.Trim().Length == 0 ? "" : matches == 0 ? "no matches" : $"{matches} found";

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
            Grid.SetColumn(_jsonText, 1);
            body.Children.Add(_jsonGutter);
            body.Children.Add(_jsonText);
            _jsonScroller.Content = body;
        }

        FillText(rows);
        FillGutter(rows);
        return _jsonScroller;
    }

    /// <summary>Rebuild the document's runs. The whole refill is one synchronous pass, so no layout runs
    /// against a half-empty block and the scroll offset only ever gets clamped, never zeroed.</summary>
    private void FillText(IReadOnlyList<JsonRow> rows)
    {
        // Its own collection, not a fresh one: the control wires the InlineCollection it creates to itself.
        var inlines = _jsonText.Inlines!;
        inlines.Clear();

        var highlight = Tint("Accent.Brand", 0x55);
        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0) inlines.Add(new LineBreak());
            foreach (var span in rows[i].Spans)
                inlines.Add(new Run(span.Text)
                {
                    Foreground = Res(ColorKeyForSpan(span.Kind)),
                    Background = span.IsMatch ? highlight : null,
                });
        }
    }

    /// <summary>
    /// Rebuild the chevron column. A <see cref="Canvas"/> rather than a control per line: only container
    /// lines get a visual, and each one is placed at its row's exact offset — which is why the text block
    /// pins <see cref="JsonLineHeight"/> instead of letting the font decide.
    /// </summary>
    private void FillGutter(IReadOnlyList<JsonRow> rows)
    {
        _jsonGutter.Children.Clear();
        _jsonGutter.Height = rows.Count * JsonLineHeight;

        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].FoldPath is not { } path) continue;

            var chevron = new TextBlock
            {
                Text = rows[i].IsFolded ? "▸" : "▾",
                FontSize = ChevronSize,
                Width = GutterWidth,
                Height = JsonLineHeight,
                LineHeight = JsonLineHeight,          // centres the glyph in its row
                TextAlignment = TextAlignment.Center,
                Foreground = Res("Text.Faint"),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            chevron.PointerEntered += (_, _) => chevron.Foreground = Res("Text.Primary");
            chevron.PointerExited += (_, _) => chevron.Foreground = Res("Text.Faint");
            chevron.PointerPressed += (_, e) =>
            {
                if (!_folded.Add(path)) _folded.Remove(path);
                e.Handled = true;                     // don't start a text selection under the chevron
                RenderBody();
            };

            Canvas.SetTop(chevron, i * JsonLineHeight);
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
