using System;
using AvaloniaEdit;
using Bearing.App.ViewModels;

namespace Bearing.App.Editing;

/// <summary>
/// Bridges an AvaloniaEdit <see cref="TextEditor"/> to the active <see cref="EditorTabViewModel"/>'s buffer
/// and caret. AvaloniaEdit's <c>TextEditor.Text</c> isn't cleanly two-way bindable, so this is the
/// documented view-layer exception to MVVM binding (docs/mvvm-refactor-plan.md): the tab is the model of
/// record, and this behaviour keeps editor ↔ tab in sync in both directions.
///
/// <para>Direction 1 (tab → editor): <see cref="Bind"/> loads a tab's text/caret into the editor, guarding
/// the load so it doesn't echo back as a user edit.</para>
/// <para>Direction 2 (editor → tab): user typing / caret moves write straight back onto the bound tab.</para>
///
/// Highlight, folding, and completion stay in the code-behind — they observe the same editor events; this
/// behaviour only owns the tab write-back and the load guard. The host does its own post-load work (rebuild
/// results / statement highlight) right after calling <see cref="Bind"/>.
/// </summary>
internal sealed class EditorTextBehavior
{
    private readonly TextEditor _editor;
    private EditorTabViewModel? _tab;
    private bool _loading;   // true while pushing a tab's buffer into the editor (suppresses write-back)

    public EditorTextBehavior(TextEditor editor)
    {
        _editor = editor;
        _editor.TextChanged += OnEditorTextChanged;
        _editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
    }

    /// <summary>The tab currently mirrored into the editor (null shows an empty buffer).</summary>
    public EditorTabViewModel? Tab => _tab;

    /// <summary>Load <paramref name="tab"/>'s buffer and caret into the editor (or clear it when null),
    /// without the load counting as a user edit. Call on tab switch / external load.</summary>
    public void Bind(EditorTabViewModel? tab)
    {
        _tab = tab;
        _loading = true;
        _editor.Text = tab?.Text ?? "";
        if (tab is not null)
            _editor.CaretOffset = Math.Clamp(tab.CaretOffset, 0, _editor.Text.Length);
        _loading = false;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (!_loading && _tab is { } tab) tab.Text = _editor.Text;
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (!_loading && _tab is { } tab) tab.CaretOffset = _editor.CaretOffset;
    }
}
