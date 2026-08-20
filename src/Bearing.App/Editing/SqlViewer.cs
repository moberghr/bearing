using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using AvaloniaEdit;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Editing;

/// <summary>
/// The app's read-only SQL surfaces, dressed as one control. There were three treatments of the same thing:
/// the real editor (<see cref="EditorChrome"/>), the SQL-preview window (the right control, none of the
/// chrome), and the history preview (a plain <c>TextBox</c> — not even the right control). This is what the
/// last two now share, so highlighting and colours can't drift apart between them (#48).
/// <para>
/// A viewer, not an editor: no line numbers, no current-line highlight, nothing with a caret worth pointing
/// at. Selection stays enabled and visible — copying a past query out of the preview is the whole point of
/// it. The background is left transparent because every host already paints a surface behind it.
/// </para>
/// </summary>
public static class SqlViewer
{
    /// <summary>
    /// Read-only viewer chrome: monospace, wrapped or scrolling, editing affordances off. Cheap — safe to
    /// call while building a window. Highlighting is deliberately <em>not</em> installed here: the first
    /// install in the process builds the TextMate registry (~100ms), which a panel the user may never open
    /// should not spend. Call <see cref="EditorChrome.InstallSqlHighlighting"/> when the surface is first
    /// shown, once per control rather than once per open.
    /// </summary>
    /// <param name="wordWrap">Wrap long lines instead of scrolling sideways. On for narrow hosts (the side
    /// pane): a one-line 400-character query is otherwise a horizontal scroll. Off where the text is
    /// pre-formatted DDL in a wide window, which wrapping only mangles.</param>
    public static void ApplyChrome(TextEditor editor, bool wordWrap = false)
    {
        editor.IsReadOnly = true;
        editor.Background = Brushes.Transparent;
        editor.FontFamily = MonoFont;
        editor.ShowLineNumbers = false;
        editor.Options.HighlightCurrentLine = false;
        editor.WordWrap = wordWrap;
        editor.HorizontalScrollBarVisibility = wordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        editor.TextArea.SelectionBrush = EditorChrome.SelectionBrush;
    }

    /// <summary>A ready-to-show read-only SQL view over <paramref name="sql"/> — chrome plus highlighting,
    /// for code-built one-shot surfaces (the preview window). Long-lived controls declared in XAML take
    /// <see cref="ApplyChrome"/> up front and install highlighting on first use instead.</summary>
    public static TextEditor Create(string sql, bool wordWrap = false, double fontSize = 13)
    {
        var editor = new TextEditor { Text = sql, FontSize = fontSize, Margin = new Thickness(8) };
        ApplyChrome(editor, wordWrap);
        EditorChrome.InstallSqlHighlighting(editor);
        return editor;
    }
}
