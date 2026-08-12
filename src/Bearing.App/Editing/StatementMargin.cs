using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Bearing.App.Theming;

namespace Bearing.App.Editing;

/// <summary>
/// A thin gutter column (sits between the line-number margin and the text) that draws an accent
/// bar spanning the statement Run will execute — the statement under the caret, or nothing while a
/// selection is active. Its own column, so it never overlaps glyphs. Holds an absolute offset span.
/// </summary>
public sealed class StatementMargin : AbstractMargin
{
    // Not "Width": that would shadow Layoutable.Width, which this class also has (and which layout reads),
    // so the two names would look interchangeable while meaning different things.
    private const double GutterWidth = 7;
    private const double BarWidth = 3;
    private const double BarInset = 2;

    // {Syntax.Func} azure at the bar's ~0xDD alpha. Resolved from the theme token (falls back
    // to the token's literal value) so a theme swap follows; cached after the first render.
    private static IBrush? _bar;
    private static IBrush Bar => _bar ??= ThemeBrush.AtAlpha("Syntax.Func", 0xDD, Color.FromRgb(0x6F, 0xA6, 0xE2));

    private int _start = -1;
    private int _end = -1;

    /// <summary>Set the highlighted span (absolute offsets); an empty/invalid span clears it.</summary>
    public void SetSpan(int start, int end)
    {
        if (end <= start) { start = -1; end = -1; }
        if (start == _start && end == _end) return;
        _start = start;
        _end = end;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(GutterWidth, 0);

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        base.OnTextViewChanged(oldTextView, newTextView);
        InvalidateVisual();
    }

    protected override void OnTextViewVisualLinesChanged() => InvalidateVisual();

    public override void Render(DrawingContext drawingContext)
    {
        var textView = TextView;
        var document = Document;
        if (_start < 0 || _end <= _start || textView is null || document is null || !textView.VisualLinesValid)
            return;

        var docLen = document.TextLength;
        var start = _start > docLen ? docLen : _start;
        var end = _end > docLen ? docLen : _end;
        if (end <= start) return;

        var startLine = document.GetLineByOffset(start).LineNumber;
        var endLine = document.GetLineByOffset(end > start ? end - 1 : end).LineNumber;
        var offset = textView.VerticalOffset;

        double? top = null, bottom = null;
        foreach (var line in textView.VisualLines)
        {
            if (line.LastDocumentLine.LineNumber < startLine || line.FirstDocumentLine.LineNumber > endLine)
                continue;
            var y = line.VisualTop - offset;
            top = top is null ? y : System.Math.Min(top.Value, y);
            bottom = bottom is null ? y + line.Height : System.Math.Max(bottom.Value, y + line.Height);
        }

        if (top is null || bottom is null) return;
        var rect = new Rect(BarInset, top.Value, BarWidth, bottom.Value - top.Value);
        drawingContext.DrawRectangle(Bar, null, new RoundedRect(rect, 1.5));
    }
}
