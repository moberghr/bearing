using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bearing.App.Results;

namespace Bearing.App.Controls;

/// <summary>
/// Find highlights drawn behind a <see cref="TextBlock"/> rather than baked into its runs. Re-splitting the
/// runs per keystroke meant rebuilding and re-laying-out the whole document — over a second per character
/// on a 20 KB value — while a typed query only ever moves highlights. Sits in the text's own grid cell,
/// under it, and measures each hit with the text's own layout, so wide glyphs and the zoom are honoured.
/// <para>
/// Expects one text line per row: the inspector's text never wraps and breaks exactly between rows.
/// </para>
/// </summary>
internal sealed class FindHighlightLayer : Control
{
    private readonly TextBlock _text;
    private IReadOnlyList<JsonMatch> _matches = Array.Empty<JsonMatch>();
    private int _current = -1;

    public FindHighlightLayer(TextBlock text)
    {
        _text = text;
        IsHitTestVisible = false;   // selection and the context menu belong to the text above
    }

    public IBrush? Brush { get; set; }

    /// <summary>The hit Enter / the arrows are on.</summary>
    public IBrush? CurrentBrush { get; set; }

    /// <summary>The hits to mark, in row order, and the index of the current one. Redraws without touching
    /// the text.</summary>
    public void SetMatches(IReadOnlyList<JsonMatch> matches, int current)
    {
        _matches = matches;
        _current = current;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        foreach (var (index, rect) in MatchRectangles())
            if ((index == _current ? CurrentBrush : Brush) is { } brush)
                context.FillRectangle(brush, rect);
    }

    /// <summary>Where the current hit sits, or null when there is none (or its row isn't laid out).</summary>
    public Rect? CurrentRectangle()
    {
        Rect? found = null;
        foreach (var (index, rect) in MatchRectangles())
            if (index == _current) found = found is { } r ? r.Union(rect) : rect;
        return found;
    }

    /// <summary>Where each hit sits, in the text's coordinates (which are this layer's: same cell), tagged
    /// with its index — a hit split across bidi runs yields more than one rectangle.</summary>
    internal IEnumerable<(int Index, Rect Rect)> MatchRectangles()
    {
        if (_matches.Count == 0) yield break;

        var lines = _text.TextLayout.TextLines;
        var row = 0;
        var top = 0.0;
        for (var index = 0; index < _matches.Count; index++)
        {
            var match = _matches[index];
            for (; row < match.Row && row < lines.Count; row++) top += lines[row].Height;
            if (row >= lines.Count) yield break;

            var line = lines[row];
            foreach (var bounds in line.GetTextBounds(line.FirstTextSourceIndex + match.Column, match.Length))
                yield return (index, bounds.Rectangle.Translate(new Vector(0, top)));
        }
    }
}
