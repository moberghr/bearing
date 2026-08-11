using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Editing;

/// <summary>
/// Visual setup for the SQL editor, split by cost so first paint isn't blocked.
/// <see cref="Apply"/> is cheap and runs synchronously (the editor is already dark on the first frame);
/// <see cref="InstallSqlHighlighting"/> builds the TextMate grammar/theme registry, which is ~100ms+, and can
/// land afterwards over plain — but correctly coloured — text.
/// </summary>
public static class EditorChrome
{
    /// <summary>Graphite surface (#1A2027), current-line highlight (#232B36), faint line numbers (#4E5865),
    /// and a translucent selection so syntax-highlighted glyphs stay readable through it — the opaque default
    /// paints solid over the coloured text.</summary>
    public static void Apply(TextEditor editor)
    {
        editor.Background = Res("Bg.Editor");
        editor.LineNumbersForeground = Res("Text.Faint");
        editor.Options.HighlightCurrentLine = true;
        var lineActive = (Res("Bg.LineActive") as ISolidColorBrush)?.Color ?? Colors.Transparent;
        editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(lineActive);
        editor.TextArea.TextView.CurrentLineBorder = new Pen(new SolidColorBrush(lineActive)); // no contrasting box
        editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x2B, 0x44, 0x55)); // steel-blue ~40%
    }

    /// <summary>Install TextMate SQL syntax highlighting. DarkPlus supplies token colours; exact Bearing
    /// syntax hues are deferred (needs internal TextMateSharp APIs — docs/design/editor-4a/README.md
    /// §Fidelity).</summary>
    public static void InstallSqlHighlighting(TextEditor editor)
    {
        var options = new RegistryOptions(ThemeName.DarkPlus);
        var installation = editor.InstallTextMate(options);
        var sql = options.GetLanguageByExtension(".sql");
        if (sql is not null)
            installation.SetGrammar(options.GetScopeByLanguageId(sql.Id));
    }
}
