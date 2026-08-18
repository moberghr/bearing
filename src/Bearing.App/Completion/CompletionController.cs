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
/// typing, runs the engine off the UI thread, discards stale results, and shows a completion window.
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
    private readonly DispatcherTimer _debounce;
    private CompletionWindow? _window;

    /// <summary>The engine's full answer for the open window — narrowing re-ranks from this, never from
    /// the already-narrowed list, so deleting a character brings the dropped rows back.</summary>
    private IReadOnlyList<Suggestion> _suggestions = Array.Empty<Suggestion>();
    private int _generation;
    private bool _narrowQueued;   // coalesces the posted re-rank (see QueueNarrow)

    public CompletionController(TextEditor editor, ICompletionEngine engine, Func<ISchemaSnapshot?> snapshot)
    {
        _editor = editor;
        _engine = engine;
        _snapshot = snapshot;
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

    private async Task TriggerAsync()
    {
        var snapshot = _snapshot();
        if (snapshot is null) return;

        var caret = _editor.CaretOffset;
        // Scope completion to the statement at the caret so earlier statements in a multi-statement
        // buffer can't leak sources/aliases into the current one. Offsets are shifted back on Show.
        var stmt = Bearing.Sql.StatementSplitter.StatementAt(_editor.Text, caret);
        var text = stmt?.Text ?? _editor.Text;
        var localCaret = stmt is null ? caret : caret - stmt.Start;
        var baseOffset = stmt?.Start ?? 0;
        var generation = ++_generation;

        CompletionResult result;
        try
        {
            result = await Task.Run(() => _engine.Complete(text, localCaret, snapshot));
        }
        catch (Exception ex)
        {
            // Completion must never disrupt editing — but a silent swallow hid real engine faults
            // (e.g. the antlr4-c3 gotcha). Record it so it's at least visible in the crash log.
            Bearing.Persistence.CrashLog.Write("completion", ex);
            return;
        }

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
    /// the popup there instead of making the user press Ctrl+Space again.</summary>
    private void OnInserted(Suggestion suggestion)
    {
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
