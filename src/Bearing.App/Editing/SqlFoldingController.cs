using System;
using System.Collections.Generic;
using System.Linq;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using Bearing.Sql;

namespace Bearing.App.Editing;

/// <summary>
/// Installs AvaloniaEdit's fold margin on the editor and keeps its foldings in sync with the SQL
/// statements in the buffer (one per multi-line query, via <see cref="SqlFolding"/>). Also drives
/// the keyboard fold commands. Folding a query collapses everything below its first line.
/// <para>
/// One editor serves every tab, so the fold state belongs to the editor, not to a tab — the same shape as
/// the zoom and the results pane. <see cref="Reset"/> is therefore part of the contract: the host calls it
/// before swapping the buffer, so tab B never opens wearing tab A's folds.
/// </para>
/// <para>
/// Two invariants here exist to keep AvaloniaEdit's height tree consistent; violating either surfaces later
/// as <c>InvalidOperationException("Trying to build visual line from collapsed line")</c> out of an ordinary
/// layout pass, which is the crash in #82:
/// <list type="number">
/// <item>A section is only ever folded while it spans real text (<see cref="SpansText"/>). Collapsing a
/// section that an edit has shrunk to nothing registers a collapsed line section over lines that no longer
/// exist.</item>
/// <item>The caret never sits inside a collapsed region (<see cref="KeepCaretVisible"/>). That is also just
/// correct behavior — an invisible caret is not a caret — but it is the state that makes the text view try to
/// build a visual line for a line it has been told is collapsed.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class SqlFoldingController
{
    private readonly TextEditor _editor;
    private readonly FoldingManager _manager;
    private readonly Func<ISqlDialect> _dialect;

    /// <summary><paramref name="dialect"/> is the selected tab's engine, asked per rebuild: the regions are
    /// statement boundaries, and those are lexical (a T-SQL buffer folds per <c>GO</c>-separated batch).
    /// One editor serves every tab, so the engine can change under a live fold margin.</summary>
    public SqlFoldingController(TextEditor editor, Func<ISqlDialect> dialect)
    {
        _editor = editor;
        _dialect = dialect;
        _manager = FoldingManager.Install(editor.TextArea); // adds the clickable [-]/[+] margin
    }

    /// <summary>The live fold sections, in document order — what the margin draws and the commands act on.
    /// Exposed so the folded state is assertable from a test; nothing in the app reads it.</summary>
    internal IEnumerable<FoldingSection> Sections => _manager.AllFoldings;

    /// <summary>Rebuild foldings from the current text; UpdateFoldings keeps unchanged regions' folded state.
    /// Wired to the editor's TextChanged by the host, so this runs on every edit.</summary>
    public void Refresh()
    {
        // Any folded section the edit has just invalidated is unfolded first, while its offsets still mean
        // something — see invariant 1 above. UpdateFoldings would otherwise carry its collapsed state onto a
        // region that no longer matches it.
        foreach (var section in _manager.AllFoldings.ToList())
            if (section.IsFolded && !SpansText(section)) section.IsFolded = false;

        var foldings = SqlFolding.ComputeFoldRegions(_dialect(), _editor.Text)
            .Select(r => new NewFolding(r.Start, r.End));
        _manager.UpdateFoldings(foldings, -1);

        // Invariant 2, on every edit and not only on the fold commands. UpdateFoldings deliberately keeps an
        // unchanged region's folded state, so an edit that did not come from typing inside the fold — an
        // undo restoring text, EditorTextCommands replacing the whole document, a paste that moves offsets —
        // can slide a still-folded section over the caret with nothing else to correct it.
        KeepCaretVisible();
    }

    /// <summary>
    /// Drop every fold. Called by the host immediately <i>before</i> it replaces the editor's buffer (#82):
    /// unfolding while the old document is still in place is what takes the collapsed line sections off the
    /// height tree cleanly, and clearing afterwards stops <see cref="Refresh"/> resurrecting a folded state
    /// on a new region that merely happens to start at the same offset.
    /// <para>Nothing is lost — fold state is not persisted per tab.</para>
    /// </summary>
    public void Reset()
    {
        foreach (var section in _manager.AllFoldings.ToList()) section.IsFolded = false;
        _manager.Clear();
    }

    public void FoldCurrent() => SetCurrent(true);
    public void UnfoldCurrent() => SetCurrent(false);

    public void FoldAll() => SetAll(true);
    public void UnfoldAll() => SetAll(false);

    private void SetCurrent(bool folded)
    {
        if (StatementSplitter.StatementAt(_dialect(), _editor.Text, _editor.CaretOffset) is not { } stmt)
            return;
        // The fold section owned by this statement is the one whose header line sits within its span.
        var section = _manager.AllFoldings.FirstOrDefault(f => f.StartOffset >= stmt.Start && f.StartOffset < stmt.End);
        if (section is null || (folded && !SpansText(section))) return;
        section.IsFolded = folded;
        if (folded) KeepCaretVisible();
    }

    private void SetAll(bool folded)
    {
        // Unfolding is always safe; folding is refused for a section that no longer spans text (invariant 1).
        foreach (var section in _manager.AllFoldings)
            if (!folded || SpansText(section)) section.IsFolded = folded;
        if (folded) KeepCaretVisible();
    }

    /// <summary>Move the caret onto the header line of whatever section just swallowed it, so it stays
    /// visible (invariant 2). A caret exactly at <c>StartOffset</c> is already on the visible header line.</summary>
    private void KeepCaretVisible()
    {
        var offset = _editor.CaretOffset;
        foreach (var section in _manager.AllFoldings)
        {
            if (!section.IsFolded || offset <= section.StartOffset || offset >= section.EndOffset) continue;
            _editor.CaretOffset = section.StartOffset;
            return;
        }
    }

    /// <summary>Whether a section still covers real text in the live document. An edit can shrink a section
    /// to nothing, or leave its end past the buffer, between one <see cref="Refresh"/> and the next.</summary>
    private bool SpansText(FoldingSection section)
        => section.StartOffset < section.EndOffset && section.EndOffset <= _editor.Document.TextLength;
}
