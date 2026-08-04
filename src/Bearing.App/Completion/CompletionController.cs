using System;
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
/// </summary>
internal sealed class CompletionController
{
    private readonly TextEditor _editor;
    private readonly ICompletionEngine _engine;
    private readonly Func<ISchemaSnapshot?> _snapshot;
    private readonly DispatcherTimer _debounce;
    private CompletionWindow? _window;
    private int _generation;

    public CompletionController(TextEditor editor, ICompletionEngine engine, Func<ISchemaSnapshot?> snapshot)
    {
        _editor = editor;
        _engine = engine;
        _snapshot = snapshot;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = TriggerAsync(); };
        _editor.TextArea.TextEntered += OnTextEntered;
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

        // While a window is open, let AvaloniaEdit filter it as the user keeps typing.
        if (_window is not null) return;

        if (char.IsLetter(ch) || ch == '_')
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

        foreach (var suggestion in result.Suggestions)
            window.CompletionList.CompletionData.Add(new BearingCompletionData(suggestion));

        window.Closed += (_, _) => { if (ReferenceEquals(_window, window)) _window = null; };
        _window = window;
        window.Show();
    }
}
