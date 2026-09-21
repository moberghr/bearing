using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using Bearing.Core.Completion;
using Bearing.Core.Schema;

namespace Bearing.App.Completion;

/// <summary>
/// Bridges the pure <see cref="ICompletionEngine"/> to AvaloniaEdit's completion popup: debounces
/// typing, calls the engine, discards stale results, and shows a completion window.
///
/// <para>
/// <b>It calls the engine <em>on</em> the UI thread, and must.</b> This summary used to say it ran the
/// engine off it, which is the belief that killed completion for the whole of 1.0 — <c>CompleteAsync</c>
/// resolves the selected tab's dialect from the window before handing the text to a parse thread of its
/// own, so entering it from anywhere else throws. The engine is not blocked by this: it returns before the
/// parse starts. See the note on <c>ICompletionEngine</c>, and the one in <see cref="TriggerAsync"/>.
/// </para>
///
/// <para>
/// It also owns the as-you-type narrowing. AvaloniaEdit's own filtering (<c>IsFiltering = true</c>)
/// scores full match / match-start / substring / camel-case only, so <c>al</c> — neither a prefix nor a
/// substring of <c>accounting_lines</c> — dropped the row out of the list altogether. Filtering is
/// therefore switched off and every keystroke re-ranks through <see cref="SuggestionRanker"/>; that
/// means this class also owns "nothing matches → close" and keeping a valid selection as the list shrinks.
/// </para>
/// </summary>
internal sealed class CompletionController
{
    private readonly TextEditor _editor;
    private readonly ICompletionEngine _engine;
    private readonly Func<ISchemaSnapshot?> _snapshot;
    private readonly Func<Bearing.Sql.ISqlDialect> _dialect;
    private readonly DispatcherTimer _debounce;
    private CompletionWindow? _window;

    /// <summary>Whether the popup currently has the keyboard. Read by <c>EditorAutoClose</c>, which must lose
    /// Enter to it: while a suggestion is selected, Enter means "accept that", not "escape the bracket".</summary>
    public bool IsOpen => _window is not null;

    /// <summary>The engine's full answer for the open window — narrowing re-ranks from this, never from
    /// the already-narrowed list, so deleting a character brings the dropped rows back.</summary>
    private IReadOnlyList<Suggestion> _suggestions = Array.Empty<Suggestion>();
    private int _generation;

    /// <summary>The fault last written to the crash log — type and message — or null. Suppresses the
    /// identical entry on every keystroke after it, and only the identical one: a *different* fault
    /// beginning while a persistent one is in force is the case the log most needs to catch, and a plain
    /// "already logged" flag would have swallowed it for the rest of the run. See the catch in
    /// <see cref="TriggerAsync"/>.</summary>
    private string? _loggedFault;
    private bool _narrowQueued;   // coalesces the posted re-rank (see QueueNarrow)

    /// <summary>Offset of the space the last accepted completion appended, or -1. Good for exactly one
    /// keystroke — see <see cref="TrySwallowSoftSpace"/>.</summary>
    private int _softSpace = -1;

    /// <summary><paramref name="dialect"/> is the selected tab's engine, asked per trigger — it decides
    /// where the statement under the caret ends, which is the window this completes inside.</summary>
    public CompletionController(TextEditor editor, ICompletionEngine engine, Func<ISchemaSnapshot?> snapshot,
        Func<Bearing.Sql.ISqlDialect> dialect)
    {
        _editor = editor;
        _engine = engine;
        _snapshot = snapshot;
        _dialect = dialect;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = TriggerAsync(); };
        _editor.TextArea.TextEntered += OnTextEntered;
        // Tunnel, i.e. ahead of AvaloniaEdit's own stacked input handler — see OnEditorKeyDownPreview.
        _editor.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnEditorKeyDownPreview,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Typing and deleting both move the caret, so this is the one signal that covers narrowing.
        _editor.TextArea.Caret.PositionChanged += (_, _) => QueueNarrow();
    }

    /// <summary>
    /// A modified gesture while the popup is open belongs to the app, not to the list. AvaloniaEdit's
    /// completion input handler accepts Enter and Tab without looking at modifiers and marks the key
    /// handled, so Ctrl+Enter never reached Run — it was swallowed by the popup. Dismiss the popup and
    /// leave the key unhandled, so it takes the normal command path (Run, Save, the palette, …) against
    /// the statement as written.
    /// <para>Shift is excluded: Shift+Enter and friends are still editing gestures.</para>
    /// </summary>
    private void OnEditorKeyDownPreview(object? sender, KeyEventArgs e)
    {
        if (_window is null) return;
        if ((e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None) return;
        _window.Close();   // closing pops AvaloniaEdit's input handler, so the key routes normally
    }

    /// <summary>Ctrl+Space: complete now, bypassing the debounce.</summary>
    public void TriggerExplicit()
    {
        _debounce.Stop();
        _ = TriggerAsync();
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        var ch = e.Text[0];

        TrySwallowSoftSpace(ch);

        if (ch == '.')
        {
            // Context just changed (alias-dot) — recompute immediately.
            _ = TriggerAsync();
            return;
        }

        // While a window is open, Narrow() re-ranks it from the engine's existing answer.
        if (_window is not null) return;

        if (char.IsLetter(ch) || ch == '_' || ch == '"')
        {
            _debounce.Stop();
            _debounce.Start();
        }
    }

    /// <summary>
    /// Take back the space a completion appended when the very next character typed is one that reads
    /// wrong after it — <c>select u.id , u.name</c>. Only the character sitting immediately after that
    /// space counts, so moving the caret away and typing a comma elsewhere forfeits it; the offset is
    /// consumed either way, making this a strictly one-keystroke window.
    /// </summary>
    private void TrySwallowSoftSpace(char typed)
    {
        // TextEntered fires after the character is in the document, so the caret already sits past both
        // it and the space. The offset is consumed either way — one keystroke, then it is gone.
        var offset = _softSpace;
        _softSpace = -1;
        CompletionInsertion.TrySwallow(_editor.Document, _editor.CaretOffset, offset, typed);
    }

    private async Task TriggerAsync()
    {
        // No catalog: a tab that has never connected, or one whose schema read has not landed. This used to
        // return, which made a fresh tab complete *nothing at all* — not even `sel` → SELECT. Keywords come
        // from the grammar and never touched the snapshot, so the guard was wider than the thing it
        // guarded. An empty snapshot keeps every catalog-derived suggestion out (there is nothing in it to
        // resolve against, so no table, column or alias can be named) while leaving the grammar's own
        // answers intact.
        var snapshot = _snapshot() ?? Bearing.Core.Schema.SchemaSnapshot.Empty;

        var caret = _editor.CaretOffset;
        // Scope completion to the statement at the caret so earlier statements in a multi-statement
        // buffer can't leak sources/aliases into the current one. Offsets are shifted back on Show.
        var stmt = Bearing.Sql.StatementSplitter.StatementAt(_dialect(), _editor.Text, caret);
        var text = stmt?.Text ?? _editor.Text;
        var localCaret = stmt is null ? caret : caret - stmt.Start;
        var baseOffset = stmt?.Start ?? 0;
        var generation = ++_generation;

        CompletionResult result;
        try
        {
            // Not Task.Run, and not merely as an optimisation: CompleteAsync resolves the dialect on *this*
            // thread before handing the parse to its own (deep) one, and that answer comes from the window's
            // DataContext. Wrapping this would ask it on a pool thread, which throws — and would also park
            // that thread on top of the parse for the length of every keystroke.
            result = await _engine.CompleteAsync(text, localCaret, snapshot);
        }
        catch (Exception ex)
        {
            // Completion must never disrupt editing — but a silent swallow hid real engine faults
            // (e.g. the antlr4-c3 gotcha). Record it so it's at least visible in the crash log.
            //
            // Once per run of *this* failure, not once per keystroke. A fault in the request rather than in
            // the text fails identically every time, so this fired on the debounce of every character
            // typed: 1.0's dialect-on-the-parse-thread put the same stack trace in the log 38 times in four
            // minutes, and put a file append in the typing path to do it.
            //
            // Keyed on the fault rather than a bare "already logged", because the second fault arriving
            // while a persistent first one is in force is the one the log most needs to catch — a flag
            // would have hidden it for the rest of the run. Cleared on the next success below, so an
            // intermittent fault is recorded again each time it comes back.
            var fault = ex.GetType().FullName + ": " + ex.Message;
            if (_loggedFault != fault)
            {
                _loggedFault = fault;
                Bearing.Persistence.CrashLog.Write("completion", ex);
            }
            return;
        }

        _loggedFault = null;

        if (generation != _generation) return; // a newer keystroke superseded this
        Show(result, baseOffset);
    }

    private void Show(CompletionResult result, int baseOffset)
    {
        if (result.Suggestions.Count == 0)
        {
            _window?.Close();
            return;
        }

        _window?.Close();

        var window = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = baseOffset + result.ReplacementStart,
            EndOffset = baseOffset + result.ReplacementStart + result.ReplacementLength,
        };
        window.CompletionList.IsFiltering = false;   // we narrow; see SuggestionRanker

        _suggestions = result.Suggestions;
        Populate(window, result.Suggestions);

        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_window, window)) return;
            _window = null;
            _suggestions = Array.Empty<Suggestion>();
        };
        _window = window;
        window.Show();
        ApplyRowTemplate(window);   // the list box only exists once the popup is up
        GuardSelection(window);

        // The span may already hold a partially-typed word (the debounce fires mid-word), so rank once
        // against it rather than opening on the engine's unfiltered order.
        Narrow();
    }

    /// <summary>
    /// Narrow on the next dispatcher pass rather than inline.
    /// <para>
    /// Two reasons, both bugs found the hard way. The caret event fires from inside the document's
    /// update-finished callback, and re-sourcing the list box there re-enters Avalonia's container
    /// recycling mid-update. And AvaloniaEdit subscribes to the same caret event when the popup opens —
    /// i.e. after this class did — so its own prefix-selection ran *after* our narrowing and cleared the
    /// selection whenever the typed text wasn't a prefix of anything (a fuzzy hit like <c>al</c>), which
    /// left Enter with nothing to insert. Posting puts us last.
    /// </para>
    /// </summary>
    private void QueueNarrow()
    {
        if (_window is null || _narrowQueued) return;
        _narrowQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _narrowQueued = false;
            Narrow();
        });
    }

    /// <summary>Draw rows with Bearing's glyph + detail + trailing-predicate template.</summary>
    private static void ApplyRowTemplate(CompletionWindow window)
    {
        if (window.CompletionList.ListBox is { } listBox)
            listBox.ItemTemplate = CompletionItemTemplate.Instance;
    }

    /// <summary>
    /// A popup with rows but nothing selected is a dead end: Enter and Tab insert whatever is selected, so
    /// a cleared selection silently makes the list unusable. AvaloniaEdit's prefix-selection clears it on
    /// any fuzzy-only match, so re-assert the first row. One subscription per popup.
    /// </summary>
    private static void GuardSelection(CompletionWindow window)
    {
        if (window.CompletionList.ListBox is not { } listBox) return;
        listBox.SelectionChanged += (_, _) =>
        {
            if (listBox.SelectedIndex < 0 && listBox.ItemCount > 0) listBox.SelectedIndex = 0;
        };
    }

    /// <summary>Re-rank the open popup against the word typed into its replacement span so far, closing
    /// it when nothing matches any more.</summary>
    private void Narrow()
    {
        if (_window is not { } window) return;

        var document = _editor.Document;
        var start = window.StartOffset;
        var caret = Math.Min(_editor.CaretOffset, document.TextLength);
        if (start < 0 || start > caret) return;   // caret left the segment — the window closes itself

        var typed = document.GetText(start, caret - start);
        var ranked = SuggestionRanker.Rank(_suggestions, typed);
        if (ranked.Count == 0)
        {
            window.Close();
            return;
        }

        Populate(window, ranked);
    }

    /// <summary>Accepting a schema (<c>audit.</c>) leaves the caret where its relations belong, so reopen
    /// the popup there instead of making the user press Ctrl+Space again. Every other kind may have left
    /// a soft space behind for the next keystroke to reclaim.</summary>
    private void OnInserted(Suggestion suggestion, int softSpaceOffset)
    {
        _softSpace = softSpaceOffset;
        if (suggestion.Kind != SuggestionKind.Schema) return;
        Dispatcher.UIThread.Post(TriggerExplicit);
    }

    /// <summary>
    /// Put <paramref name="suggestions"/> in the popup and select the top row.
    /// <para>
    /// <c>CompletionData</c> is a plain <see cref="List{T}"/> with no change notification, so the list
    /// box only re-renders when handed a fresh source — and both have to be kept in step, because
    /// AvaloniaEdit's own caret handler still prefix-selects out of <c>CompletionData</c> and would
    /// otherwise select a row that isn't displayed (leaving Enter with nothing to insert).
    /// </para>
    /// </summary>
    private void Populate(CompletionWindow window, IReadOnlyList<Suggestion> suggestions)
    {
        var items = suggestions.Select(s => (ICompletionData)new BearingCompletionData(s, OnInserted)).ToList();

        var list = window.CompletionList;
        list.CompletionData.Clear();
        foreach (var item in items) list.CompletionData.Add(item);

        ApplyRowTemplate(window);
        if (list.ListBox is { } listBox) listBox.ItemsSource = items;
        list.SelectedItem = items[0];
    }
}
