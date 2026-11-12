using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.Utilities;
using Avalonia.VisualTree;
using Bearing.App.Results;

namespace Bearing.App.Controls;

/// <summary>
/// A selectable text block that colours one plain string by character ranges instead of holding a
/// <see cref="Avalonia.Controls.Documents.Run"/> per span. The ranges become style overrides on the text
/// layout, so a 1,500-line value is one string and a list of offsets — not 6,000 inline elements, each of
/// which had to be created, attached and styled (over a second to open or fold a 20 KB value).
/// <para>
/// Selection still paints its background and copies <see cref="TextBlock.Text"/>; it doesn't recolour the
/// selected glyphs, which the theme doesn't ask for here anyway.
/// </para>
/// <para>
/// On top of <see cref="SelectableTextBlock"/>: a drag that reaches the edge of the enclosing
/// <see cref="ScrollViewer"/> scrolls it and keeps extending the selection (the base clamps to the text and
/// never scrolls, so a long line couldn't be selected past the pane's edge); http(s) addresses in string
/// values are underlined and open on Ctrl+click (<see cref="LinkActivated"/>); and a triple-click selects
/// the token under the pointer — a string's contents without its quotes — instead of the whole document.
/// </para>
/// </summary>
internal sealed class JsonTextBlock : SelectableTextBlock
{
    /// <summary>How close to the viewport's edge a drag starts scrolling. Inside rather than past it: the
    /// inspector sits at the window's right edge, often the screen's, where the pointer can't go further.</summary>
    internal const double AutoScrollZone = 24;

    private IReadOnlyList<JsonColourSpan> _colours = Array.Empty<JsonColourSpan>();
    private IReadOnlyList<JsonLink> _links = Array.Empty<JsonLink>();
    private JsonTextDocument? _document;
    private readonly Func<JsonSpanKind, IBrush> _brushFor;
    private readonly Cursor _handCursor = new(StandardCursorType.Hand);

    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private ScrollViewer? _scroller;
    /// <summary>Where the dragging pointer last was, in <see cref="_scroller"/>'s coordinates.</summary>
    private Point _dragPoint;

    public JsonTextBlock(Func<JsonSpanKind, IBrush> brushFor)
    {
        _brushFor = brushFor;
        _autoScroll.Tick += (_, _) => AutoScrollStep();
    }

    /// <summary>Ctrl+click on a link. The view decides what opening it means.</summary>
    public event Action<Uri>? LinkActivated;

    /// <summary>Keep the theme's <see cref="SelectableTextBlock"/> styling (selection brush, cursor).</summary>
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    /// <summary>The links in the current document, in text order.</summary>
    internal IReadOnlyList<JsonLink> Links => _links;

    /// <summary>Replace the whole document. One text change, one layout.</summary>
    public void SetDocument(JsonTextDocument document)
    {
        _document = document;
        _colours = document.Colours;
        _links = JsonTextTokens.Links(document);
        if (Text == document.Text) InvalidateTextLayout();   // same text, the colours may still differ
        else Text = document.Text;
    }

    protected override TextLayout CreateTextLayout(string? text)
    {
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var properties = new Dictionary<(JsonSpanKind, bool), TextRunProperties>();
        var segments = JsonTextTokens.Segments(_colours, _links);
        var overrides = new List<ValueSpan<TextRunProperties>>(segments.Count);
        foreach (var segment in segments)
        {
            var key = (segment.Kind, segment.IsLink);
            if (!properties.TryGetValue(key, out var run))
                properties[key] = run = new GenericTextRunProperties(
                    typeface, FontSize, segment.IsLink ? Avalonia.Media.TextDecorations.Underline : TextDecorations,
                    _brushFor(segment.Kind), fontFeatures: FontFeatures);
            overrides.Add(new ValueSpan<TextRunProperties>(segment.Start, segment.Length, run));
        }

        var max = GetMaxSizeFromConstraint();
        return new TextLayout(
            text, typeface, FontSize, Foreground, TextAlignment, TextWrapping, TextTrimming, TextDecorations,
            FlowDirection, max.Width, max.Height, LineHeight, LetterSpacing, MaxLines, FontFeatures, overrides);
    }

    /// <summary>The character under <paramref name="point"/> (this control's coordinates), or null when the
    /// point is past a line's end or below the text — a caret position is not a character.</summary>
    private int? CharacterAt(Point point)
    {
        if (Text is null) return null;
        var local = point - new Point(Padding.Left, Padding.Top);
        var index = TextLayout.HitTestPoint(local).CharacterHit.FirstCharacterIndex;
        if (index < 0 || index >= Text.Length || Text[index] == '\n') return null;
        // The hit is the nearest character, even far past a line's end (IsInside isn't set for a point on
        // a glyph either) — so check the point is on that character's own box.
        foreach (var box in TextLayout.HitTestTextRange(index, 1))
            if (box.Contains(local)) return index;
        return null;
    }

    private JsonLink? LinkAt(Point point)
        => CharacterAt(point) is { } index ? JsonTextTokens.LinkAt(_links, index) : null;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && LinkAt(e.GetPosition(this)) is { } link)
        {
            e.Handled = true;   // a link click is not the start of a selection
            LinkActivated?.Invoke(link.Uri);
            return;
        }

        base.OnPointerPressed(e);

        // The base selects everything on a third click; a token is what a reader of JSON is after.
        if (props.IsLeftButtonPressed && e.ClickCount == 3 && _document is { } document)
        {
            var at = e.GetPosition(this);
            var (start, length) = JsonTextTokens.TokenAt(document,
                CharacterAt(at) ?? TextLayout.HitTestPoint(at - new Point(Padding.Left, Padding.Top)).TextPosition);
            SetCurrentValue(SelectionStartProperty, start);
            SetCurrentValue(SelectionEndProperty, start + length);
            e.Pointer.Capture(null);   // a drag after it would otherwise re-anchor the selection at the token
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (e.Pointer.Captured == this && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _scroller ??= this.FindAncestorOfType<ScrollViewer>();
            if (_scroller is null) return;
            _dragPoint = e.GetPosition(_scroller);
            if (ScrollDelta() == default) _autoScroll.Stop();
            else if (!_autoScroll.IsEnabled)
            {
                AutoScrollStep();   // move now, not a tick later
                _autoScroll.Start();
            }
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && LinkAt(e.GetPosition(this)) is not null)
            Cursor = _handCursor;
        else
            ClearValue(CursorProperty);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _autoScroll.Stop();
        base.OnPointerReleased(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _autoScroll.Stop();
        base.OnPointerCaptureLost(e);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _autoScroll.Stop();
        _scroller = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>How far to scroll this tick: nothing inside the viewport's comfort area, and faster the
    /// deeper the pointer is in the edge zone or past it.</summary>
    private Vector ScrollDelta()
    {
        if (_scroller is null) return default;
        var viewport = _scroller.Viewport;
        return new Vector(Axis(_dragPoint.X, viewport.Width), Axis(_dragPoint.Y, viewport.Height));

        static double Axis(double at, double size)
        {
            if (size <= AutoScrollZone * 2) return 0;
            var beyond = at < AutoScrollZone ? at - AutoScrollZone
                : at > size - AutoScrollZone ? at - (size - AutoScrollZone)
                : 0;
            return beyond == 0 ? 0 : Math.Sign(beyond) * Math.Min(60, 4 + Math.Abs(beyond) / 2);
        }
    }

    /// <summary>Scroll one step toward the pointer and extend the selection to where it now points.</summary>
    private void AutoScrollStep()
    {
        if (_scroller is null || Text is null) { _autoScroll.Stop(); return; }

        var before = _scroller.Offset;
        var max = new Vector(
            Math.Max(0, _scroller.Extent.Width - _scroller.Viewport.Width),
            Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height));
        var delta = ScrollDelta();
        var after = new Vector(
            Math.Clamp(before.X + delta.X, 0, max.X),
            Math.Clamp(before.Y + delta.Y, 0, max.Y));
        if (after == before) return;   // at the end already; keep the timer for when the pointer moves

        // Where the pointer sits over the text, before the scroll lands in layout: the content moves by the
        // offset change, so the same viewport point now lies that much further into the text.
        if (_scroller.TranslatePoint(_dragPoint, this) is not { } inText) return;
        _scroller.Offset = after;
        var moved = inText + (after - before);
        var point = new Point(moved.X - Padding.Left, moved.Y - Padding.Top);
        point = new Point(
            Math.Clamp(point.X, 0, Math.Max(TextLayout.WidthIncludingTrailingWhitespace, 0)),
            Math.Clamp(point.Y, 0, Math.Max(TextLayout.Height, 0)));
        SetCurrentValue(SelectionEndProperty, TextLayout.HitTestPoint(point).TextPosition);
    }
}
