using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
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
/// </summary>
internal sealed class JsonTextBlock : SelectableTextBlock
{
    private IReadOnlyList<JsonColourSpan> _colours = Array.Empty<JsonColourSpan>();
    private readonly Func<JsonSpanKind, IBrush> _brushFor;

    public JsonTextBlock(Func<JsonSpanKind, IBrush> brushFor) => _brushFor = brushFor;

    /// <summary>Keep the theme's <see cref="SelectableTextBlock"/> styling (selection brush, cursor).</summary>
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    /// <summary>Replace the whole document. One text change, one layout.</summary>
    public void SetDocument(JsonTextDocument document)
    {
        _colours = document.Colours;
        if (Text == document.Text) InvalidateTextLayout();   // same text, the colours may still differ
        else Text = document.Text;
    }

    protected override TextLayout CreateTextLayout(string? text)
    {
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var properties = new Dictionary<JsonSpanKind, TextRunProperties>();
        var overrides = new List<ValueSpan<TextRunProperties>>(_colours.Count);
        foreach (var colour in _colours)
        {
            if (!properties.TryGetValue(colour.Kind, out var run))
                properties[colour.Kind] = run = new GenericTextRunProperties(
                    typeface, FontSize, TextDecorations, _brushFor(colour.Kind), fontFeatures: FontFeatures);
            overrides.Add(new ValueSpan<TextRunProperties>(colour.Start, colour.Length, run));
        }

        var max = GetMaxSizeFromConstraint();
        return new TextLayout(
            text, typeface, FontSize, Foreground, TextAlignment, TextWrapping, TextTrimming, TextDecorations,
            FlowDirection, max.Width, max.Height, LineHeight, LetterSpacing, MaxLines, FontFeatures, overrides);
    }
}
