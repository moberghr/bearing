using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Bearing.App.Theming;
using Bearing.Sql;

namespace Bearing.App.Editing;

/// <summary>
/// Auto-closing quotes and brackets in the SQL editor (#70): typing <c>'</c>, <c>"</c> or <c>(</c> brings its
/// closer along with the caret between them, typing the closer yourself steps over it, Backspace on an empty
/// pair takes both halves, a selection is wrapped rather than replaced, and Enter jumps past the closer.
/// <para>
/// Thin on purpose. Every decision about <i>whether</i> to act belongs to <see cref="AutoClose"/>, which is
/// pure and cheap to test exhaustively; this class is the event plumbing. Both halves are covered — the
/// plumbing by <c>Ui.AutoCloseWiringTests</c>, which types into the real editor in the real shell.
/// </para>
/// <para>
/// Enter is the one piece of state. It escapes a pair only while the caret is still sitting immediately
/// before a closer this class inserted — an explicit latch rather than a guess from the caret's
/// surroundings, because an Enter that sometimes refuses to break a line is worse than no jump at all. Move
/// the caret anywhere else and the latch drops, so the next Enter is a newline again.
/// </para>
/// </summary>
internal sealed class EditorAutoClose
{
    private readonly TextEditor _editor;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _completionOpen;
    private readonly PairMarker _marker;

    /// <summary>
    /// The closer this class inserted and the caret has not yet left, or null. The whole Enter behaviour
    /// hangs off this one field.
    /// <para>
    /// An anchor rather than an offset, because the offset moves: typing inside the pair inserts text before
    /// the closer, so a fixed number goes stale on the first character and the latch drops itself before it
    /// is ever used. Anchors are what AvaloniaEdit has for exactly this.
    /// </para>
    /// </summary>
    private TextAnchor? _closer;

    /// <param name="enabled">Read live, so toggling the setting takes effect without a restart.</param>
    /// <param name="completionOpen">Whether the completion popup has the keyboard. Enter belongs to it while
    /// it is up — accepting a suggestion is what the user means there, not escaping a bracket.</param>
    public EditorAutoClose(TextEditor editor, Func<bool> enabled, Func<bool> completionOpen)
    {
        _editor = editor;
        _enabled = enabled;
        _completionOpen = completionOpen;
        _marker = new PairMarker();
        _editor.TextArea.TextView.BackgroundRenderers.Add(_marker);

        // TextEntering, not TextEntered: skipping over a closer and wrapping a selection both have to
        // pre-empt the insert, not correct it afterwards.
        _editor.TextArea.TextEntering += OnTextEntering;
        // Tunnel, ahead of AvaloniaEdit's own stacked input handler, for the same reason CompletionController
        // does it: Backspace and Enter are already spoken for down there.
        _editor.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _editor.TextArea.Caret.PositionChanged += (_, _) => DropLatchIfCaretLeft();
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (!_enabled() || e.Text is not { Length: 1 } text) return;

        var typed = text[0];
        var document = _editor.Document;
        var decision = AutoClose.ForTyped(
            document.Text, _editor.SelectionStart, _editor.SelectionLength, typed);

        switch (decision.Action)
        {
            case AutoCloseAction.SkipOver:
                _editor.CaretOffset++;
                Latch(null);                       // left the pair by stepping out of it
                e.Handled = true;
                return;

            case AutoCloseAction.Pair:
            case AutoCloseAction.Surround:
                {
                    var start = _editor.SelectionStart;
                    var length = _editor.SelectionLength;
                    if (length > 0) _editor.TextArea.ClearSelection();
                    document.Replace(start, length, decision.Text);
                    _editor.CaretOffset = start + decision.Caret;
                    // The closer is the last character of what was inserted.
                    Latch(start + decision.Text.Length - 1);
                    e.Handled = true;
                    return;
                }
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_enabled()) return;

        if (e.Key == Key.Back && _editor.SelectionLength == 0
            && AutoClose.DeletesEmptyPair(_editor.Document.Text, _editor.CaretOffset))
        {
            _editor.Document.Remove(_editor.CaretOffset - 1, 2);
            Latch(null);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        // The popup owns Enter while it is open, and a latch is only honoured while it is still live.
        if (_completionOpen() || _closer is not { IsDeleted: false } closer) return;
        if (closer.Offset != _editor.CaretOffset) return;   // the caret is not where Enter would act

        _editor.CaretOffset = closer.Offset + 1;
        Latch(null);
        e.Handled = true;
    }

    /// <summary>Arm or drop the Enter latch, keeping the marker in step.</summary>
    private void Latch(int? closerOffset)
    {
        _closer = closerOffset is { } offset
            ? Anchor(offset)
            : null;
        _marker.At = _closer;
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    /// <summary>An anchor on the closer that text typed inside the pair pushes along, rather than one the
    /// insertion happens after.</summary>
    private TextAnchor Anchor(int offset)
    {
        var anchor = _editor.Document.CreateAnchor(offset);
        anchor.MovementType = AnchorMovementType.AfterInsertion;
        return anchor;
    }

    /// <summary>Drop the latch once the caret is no longer sitting immediately before the closer. Cheaper and
    /// more predictable than tracking edits: if the caret is not where Enter would act, Enter should not
    /// act.</summary>
    private void DropLatchIfCaretLeft()
    {
        if (_closer is { IsDeleted: false } closer && closer.Offset == _editor.CaretOffset) return;
        if (_closer is not null) Latch(null);
    }

    /// <summary>
    /// The thin vertical mark just after the closer, showing where Enter will put the caret. Drawn through a
    /// background renderer, the same mechanism <see cref="StatementMargin"/> uses, and coloured from the
    /// theme rather than a literal (§9.1).
    /// </summary>
    private sealed class PairMarker : IBackgroundRenderer
    {
        private const double Width = 1.5;

        private static (Avalonia.Application? Owner, IImmutableBrush Brush)? _brush;

        private static IBrush Brush =>
            ThemeBrush.AtAlphaCached(ref _brush, "Syntax.Func", 0xAA, Color.FromRgb(0x6F, 0xA6, 0xE2));

        /// <summary>The closer the mark sits after, or null to draw nothing.</summary>
        public TextAnchor? At { get; set; }

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (At is not { IsDeleted: false } anchor || textView.Document is null) return;
            var offset = anchor.Offset;
            if (offset < 0 || offset >= textView.Document.TextLength) return;

            textView.EnsureVisualLines();
            var line = textView.Document.GetLineByOffset(offset);
            var visual = textView.GetVisualLine(line.LineNumber);
            if (visual is null) return;   // scrolled out of view

            var column = visual.GetVisualColumn(offset - line.Offset + 1);
            var x = visual.GetTextLineVisualXPosition(visual.TextLines[0], column) - textView.HorizontalOffset;
            var y = visual.VisualTop - textView.VerticalOffset;
            drawingContext.FillRectangle(Brush, new Rect(x, y, Width, visual.Height));
        }
    }
}
