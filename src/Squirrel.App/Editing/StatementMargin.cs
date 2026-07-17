using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Squirrel.App.Editing;

/// <summary>
/// A thin gutter column (sits between the line-number margin and the text) that draws an accent
/// bar spanning the statement Run will execute — the statement under the caret, or nothing while a
/// selection is active. Its own column, so it never overlaps glyphs. Holds an absolute offset span.
/// </summary>
public sealed class StatementMargin : AbstractMargin
{
    private const double Width = 7;
    private const double BarWidth = 3;
    private const double BarInset = 2;

    private static readonly IBrush Bar = new SolidColorBrush(Color.FromArgb(0xDD, 0x4F, 0x9C, 0xEE));

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

    protected override Size MeasureOverride(Size availableSize) => new(Width, 0);

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
