using System;
using System.Linq;
using System.Threading.Tasks;
using AvaloniaEdit;
using Bearing.Sql;

namespace Bearing.App.Editing;

/// <summary>
/// The editor's text operations: the statement-aware commands (open line, toggle comment, select statement,
/// jump between statements, the Ctrl+U / Ctrl+W deletes) and the "which SQL would Run execute" question.
/// <para>
/// Owns the statement-highlight margin, so the marked statement and the statement Run picks can never
/// disagree — they come from the same <see cref="StatementSplitter"/> call here. Extracted from
/// <c>MainWindow</c>: the window now only maps keystrokes onto these.
/// </para>
/// <para>
/// Every one of those questions is asked of the <b>selected tab's</b> dialect, resolved per call. A
/// T-SQL buffer's statements end at a <c>GO</c> and not at a <c>;</c> inside <c>[a;b]</c>, so read with
/// the PostgreSQL lexer a GO-separated batch is one statement and "run current statement" runs the whole
/// buffer. The tab can change engine under a live editor (switch tabs, re-point a tab's connection),
/// which is why this is a callback and not a constructor value.
/// </para>
/// </summary>
public sealed class EditorTextCommands
{
    private readonly TextEditor _editor;
    private readonly Func<ISqlDialect> _dialect;
    private readonly StatementMargin _highlight = new();

    /// <summary>Wraps <paramref name="editor"/> and installs the statement-highlight margin in its own
    /// column, right of the line numbers. <paramref name="dialect"/> supplies the engine whose lexical
    /// rules decide where the statements in the buffer are.</summary>
    public EditorTextCommands(TextEditor editor, Func<ISqlDialect> dialect)
    {
        _editor = editor;
        _dialect = dialect;
        _editor.TextArea.LeftMargins.Add(_highlight);
    }

    // ---- what Run executes -------------------------------------------------------------------

    /// <summary>The SQL a Run should execute: the selection if there is one, else the statement at the caret,
    /// else the whole buffer. Normalized so several blank-line-separated statements without semicolons run as
    /// a batch instead of one malformed command.</summary>
    public string SqlToRun()
    {
        var selected = _editor.SelectedText;
        var sql = string.IsNullOrWhiteSpace(selected)
            ? StatementSplitter.StatementAt(_dialect(), _editor.Text, _editor.CaretOffset)?.Text ?? _editor.Text
            : selected;
        return StatementSplitter.EnsureSeparated(_dialect(), sql);
    }

    /// <summary>query.runAll: the entire buffer as a batch, ignoring caret and selection.</summary>
    public string SqlToRunAll() => StatementSplitter.EnsureSeparated(_dialect(), _editor.Text);

    // ---- statement highlight -----------------------------------------------------------------

    /// <summary>Mark the statement Run would execute — the selection if any, else the statement at the caret
    /// — so the highlight always matches <see cref="SqlToRun"/>. A selection is its own indicator, so the
    /// margin clears for one.</summary>
    public void UpdateStatementHighlight()
    {
        if (!string.IsNullOrEmpty(_editor.SelectedText))
            _highlight.SetSpan(-1, -1);
        else if (StatementSplitter.StatementAt(_dialect(), _editor.Text, _editor.CaretOffset) is { } stmt)
            _highlight.SetSpan(stmt.TrimmedStart, stmt.TrimmedEnd);
        else
            _highlight.SetSpan(-1, -1);
    }

    // ---- statement navigation ----------------------------------------------------------------

    /// <summary>Alt+Up / Alt+Down: move the caret to the previous / next runnable statement.</summary>
    public void MoveToAdjacentStatement(int direction)
    {
        var text = _editor.Text;
        var spans = StatementSplitter.Split(_dialect(), text)
            .Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
        if (spans.Count == 0) return;

        var current = StatementSplitter.StatementAt(_dialect(), text, _editor.CaretOffset);
        var idx = current is null ? 0 : spans.FindIndex(s => s.Start == current.Start);
        if (idx < 0) idx = 0;

        var target = Math.Clamp(idx + direction, 0, spans.Count - 1);
        _editor.CaretOffset = spans[target].TrimmedStart;
        _editor.TextArea.Caret.BringCaretToView();
    }

    /// <summary>Ctrl+Shift+A: select the whole statement the caret sits in.</summary>
    public void SelectCurrentStatement()
    {
        if (StatementSplitter.StatementAt(_dialect(), _editor.Text, _editor.CaretOffset) is not { } stmt) return;
        _editor.SelectionStart = stmt.TrimmedStart;
        _editor.SelectionLength = stmt.TrimmedEnd - stmt.TrimmedStart;
        _editor.CaretOffset = stmt.TrimmedEnd;
    }

    // ---- text edits --------------------------------------------------------------------------

    /// <summary>Insert a blank line below (or above) the caret's line, matching its indentation.</summary>
    public void OpenLine(bool below)
    {
        var doc = _editor.Document;
        var line = doc.GetLineByOffset(_editor.CaretOffset);
        var lineText = doc.GetText(line.Offset, line.Length);
        var indent = lineText[..(lineText.Length - lineText.TrimStart().Length)];

        if (below)
        {
            doc.Insert(line.EndOffset, "\n" + indent);
            _editor.CaretOffset = line.EndOffset + 1 + indent.Length;
        }
        else
        {
            doc.Insert(line.Offset, indent + "\n");
            _editor.CaretOffset = line.Offset + indent.Length;
        }
        _editor.TextArea.Caret.BringCaretToView();
    }

    /// <summary>Ctrl+/: toggle <c>-- </c> comments over the lines the caret/selection touches.</summary>
    public void ToggleLineComment()
    {
        var (start, end) = Span();
        var result = LineCommenter.Toggle(_editor.Text, start, end);
        if (result.Text == _editor.Text) return;

        _editor.Document.Replace(0, _editor.Document.TextLength, result.Text);
        _editor.SelectionStart = result.SelectionStart;
        _editor.SelectionLength = result.SelectionLength;
        _editor.CaretOffset = result.SelectionStart + result.SelectionLength;
    }

    /// <summary>Ctrl+U / Ctrl+W: apply a <see cref="TextDeleter"/> span as one document edit, so undo
    /// stays granular and the caret lands where the removed text began.</summary>
    public void ApplyDelete(Func<string, int, int, DeleteRange> op)
    {
        var (start, end) = Span();
        var range = op(_editor.Text, start, end);
        if (range.IsEmpty) return;

        _editor.TextArea.ClearSelection();
        _editor.Document.Remove(range.Start, range.Length);
        _editor.CaretOffset = range.Start;
        _editor.TextArea.Caret.BringCaretToView();
    }

    /// <summary>The editor's (start, end) offsets: the selection when there is one, else the caret twice.</summary>
    private (int Start, int End) Span() => _editor.SelectionLength > 0
        ? (_editor.SelectionStart, _editor.SelectionStart + _editor.SelectionLength)
        : (_editor.CaretOffset, _editor.CaretOffset);

    // ---- formatting --------------------------------------------------------------------------

    /// <summary>
    /// editor.format: lay out the selection, or the whole buffer when there is none — the Format
    /// Document / Format Selection convention, not <see cref="SqlToRun"/>'s statement-at-the-caret rule.
    /// Formatting is a whole-file edit in every editor people arrive from, and quietly reformatting only the
    /// statement you happened to be sitting in would be the surprise.
    /// </summary>
    /// <param name="options">Keyword case and indent width, from the user's settings.</param>
    /// <returns>A line for the status bar when nothing was written — the formatter declined, or the buffer
    /// moved under us — and null when there is nothing to say, the edit having happened or the text already
    /// being laid out.</returns>
    public async Task<string?> FormatSqlAsync(SqlFormatOptions options)
    {
        var selected = _editor.SelectionLength > 0;
        var start = selected ? _editor.SelectionStart : 0;
        var length = selected ? _editor.SelectionLength : _editor.Document.TextLength;
        if (length == 0) return null;

        // The document's line endings, not the fragment's: a one-line selection out of a CRLF file has no
        // CRLF in it for the formatter to find, and it would hand back LF to splice into a CRLF buffer.
        var newline = _editor.Document.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var source = _editor.Document.GetText(start, length);

        // Off the UI thread: a large script takes hundreds of milliseconds, and a window that stops
        // repainting for that long reads as a hang. FormatAsync runs on its own stack-sized thread and
        // blocks nothing, so this is one thread rather than a frozen one.
        var before = _editor.Document.Version;
        var result = await SqlFormat.FormatAsync(source, options with { Newline = newline });

        // The user can type while we are away, and applying an edit computed against text that has since
        // moved would corrupt the buffer — the offsets no longer mean what they meant. Decline instead, and
        // say so rather than appearing to do nothing.
        if (before is not null && _editor.Document.Version?.CompareAge(before) != 0)
            return "Not formatted — the document changed while it was being formatted.";

        if (result.Refused)
            return selected
                ? $"Selection not formatted — {result.Refusal}."
                : $"Not formatted — {result.Refusal}.";
        if (!result.Changed) return null;

        // One Replace, so the whole reformat is a single undo step rather than a stack of them.
        var caret = _editor.CaretOffset;
        _editor.Document.Replace(start, length, result.Text);

        if (selected)
        {
            _editor.SelectionStart = start;
            _editor.SelectionLength = result.Text.Length;   // keep what was formatted selected
        }
        else
        {
            // The caret cannot be preserved meaningfully — every offset after the first change has moved —
            // so it is clamped rather than guessed at. Landing near where you were beats landing at zero.
            _editor.CaretOffset = Math.Min(caret, _editor.Document.TextLength);
        }
        return null;
    }
}
