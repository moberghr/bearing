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

    /// <summary>Whether anything hangs on statement ends right now (inline zones). Format only keeps statement
    /// ends as hunk boundaries — a lex of both texts — when it does. Null reads as no.</summary>
    public Func<bool>? KeepsStatementEnds { get; set; }

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
        // Not ScopeToRun().Sql: Explain needs only the text, not where each statement ends in the buffer.
        var selected = _editor.SelectedText;
        var sql = string.IsNullOrWhiteSpace(selected)
            ? StatementSplitter.StatementAt(_dialect(), _editor.Text, _editor.CaretOffset)?.Text ?? _editor.Text
            : selected;
        return StatementSplitter.EnsureSeparated(_dialect(), sql);
    }

    /// <summary>query.runAll: the entire buffer as a batch, ignoring caret and selection.</summary>
    public string SqlToRunAll() => StatementSplitter.EnsureSeparated(_dialect(), _editor.Text);

    /// <summary><see cref="SqlToRun"/> together with where that text sits in the buffer, so its results can
    /// be placed under it (inline results).</summary>
    public Results.RunScope ScopeToRun()
    {
        var text = _editor.Text;
        if (!string.IsNullOrWhiteSpace(_editor.SelectedText))
        {
            return Results.InlineResultPlacement.Scope(_dialect(), _editor.SelectedText, _editor.SelectionStart,
                isWholeBuffer: false);
        }
        if (StatementSplitter.StatementAt(_dialect(), text, _editor.CaretOffset) is { } stmt)
        {
            return Results.InlineResultPlacement.Scope(_dialect(), stmt.Text, stmt.Start, isWholeBuffer: false);
        }
        return Results.InlineResultPlacement.Scope(_dialect(), text, 0, isWholeBuffer: false);
    }

    /// <summary>
    /// Run with focus in an inline zone (spec §8): the zone's own statements, wherever the caret is. One whose
    /// statement was removed falls back to the caret, as there is nothing left to re-run.
    /// <para>
    /// A zone holding a whole batch's sets (an unattributed Run All — every multi-statement Run All on SQL
    /// Server) does <b>not</b> re-run the batch: Run never escalates to running everything, the buffer as it is
    /// now, possibly with writes typed since. It runs the caret's statement, and <paramref name="note"/> says so.
    /// </para>
    /// </summary>
    /// <param name="note">A status line when the scope is not what the zone held, else null.</param>
    public Results.RunScope ScopeForZone(ViewModels.InlineResultZone zone, out string? note)
    {
        note = null;
        if (zone.StatementRemoved)
        {
            return ScopeToRun();
        }
        if (zone.FromWholeBuffer)
        {
            note = "Ran the statement at the caret — Run All re-runs the whole batch.";
            return ScopeToRun();
        }
        var text = _editor.Text;
        // As many statements as the run that made the zone: a multi-statement selection's zone re-runs them
        // all, not just the last one, or the rest of its sets would vanish.
        return Results.InlineResultPlacement.ZoneSpan(_dialect(), text, zone.Offset, zone.StatementCount) is { } span
            ? Results.InlineResultPlacement.Scope(_dialect(), text[span.Start..span.End], span.Start, isWholeBuffer: false)
            : ScopeToRun();
    }

    /// <summary>The bounds of the statement at <paramref name="caret"/> (raw span), or the caret itself when it
    /// is on none — where focus.results looks for that statement's zone.</summary>
    public (int Start, int End) StatementBounds(int caret)
        => StatementSplitter.StatementAt(_dialect(), _editor.Text, caret) is { } stmt
            ? (stmt.Start, stmt.End)
            : (caret, caret);

    /// <summary><see cref="SqlToRunAll"/> together with where each statement ends in the buffer.</summary>
    public Results.RunScope ScopeToRunAll()
        => Results.InlineResultPlacement.Scope(_dialect(), _editor.Text, 0, isWholeBuffer: true);

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
        var old = _editor.Text;
        var result = LineCommenter.Toggle(old, start, end);
        if (result.Text == old)
        {
            return;
        }

        // As the smallest edits, in one undo step: text that didn't change — and an inline zone hanging in it —
        // is left alone, through Ctrl+Z and redo too.
        // Line i stays line i, so the hunks come straight from the line pairs — no diff on the UI thread.
        TextHunkApply.Apply(_editor.Document, 0, Results.TextHunks.LineByLine(old, result.Text));
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
        // say so rather than appearing to do nothing. Asked again after the diff, which awaits too.
        const string moved = "Not formatted — the document changed while it was being formatted.";
        bool Moved() => before is not null && _editor.Document.Version?.CompareAge(before) != 0;
        if (Moved())
        {
            return moved;
        }

        if (result.Refused)
        {
            return selected
                ? $"Selection not formatted — {result.Refusal}."
                : $"Not formatted — {result.Refusal}.";
        }
        if (!result.Changed)
        {
            return null;
        }

        // The smallest edits that lay it out, worked out off the UI thread (a line diff, then a character diff per
        // changed run), so text it didn't change keeps its anchors through the edit, its undo and its redo — zones
        // placed after a Format survive its Ctrl+Z too. With zones, statement ends are also kept as hunk
        // boundaries (a lex of both texts, skipped without zones). Past MaxHunks edits it is one trimmed replace
        // instead, as before inline results: the diff and the Replace calls stay bounded.
        var dialect = _dialect();
        Task<IReadOnlyList<Results.TextHunks.Hunk>> Diff(bool keepEnds)
            => Task.Run(() =>
            {
                var sync = keepEnds ? Results.TextHunks.StatementSync(dialect, source, result.Text) : null;
                var diffed = Results.TextHunks.Diff(source, result.Text, sync);
                // Too many edits: one trimmed replace per stretch between statement ends (with zones, so their
                // anchors stay on the boundaries), else one for the whole text.
                return diffed.Count > Results.TextHunks.MaxHunks ? Results.TextHunks.PerStretch(source, result.Text, sync) : diffed;
            });
        var keepEnds = KeepsStatementEnds?.Invoke() == true;
        var hunks = await Diff(keepEnds);
        if (!keepEnds && KeepsStatementEnds?.Invoke() == true && !Moved())
        {
            // Zones appeared while it diffed (a run landed): their statement ends must be hunk boundaries too.
            keepEnds = true;
            hunks = await Diff(keepEnds);
        }
        if (Moved())
        {
            return moved;
        }

        // The smallest edits, in one update, so the whole reformat is a single undo step rather than a stack
        // of them — and text it didn't change (an inline zone's anchor in it) is not replaced at all.
        var caret = _editor.CaretOffset;
        TextHunkApply.Apply(_editor.Document, start, hunks);

        if (selected)
        {
            _editor.SelectionStart = start;
            _editor.SelectionLength = result.Text.Length;   // keep what was formatted selected
        }
        else if (hunks is [{ Length: > 0 } only] && caret > start + only.Offset && caret < start + only.Offset + only.Length)
        {
            // One replace over the caret: it can't follow the text, so it is clamped rather than guessed at (as
            // before inline results). Otherwise the minimal edits carry it — same statement.
            _editor.CaretOffset = Math.Min(caret, _editor.Document.TextLength);
        }
        return null;
    }
}
