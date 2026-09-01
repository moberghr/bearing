using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Editing;

/// <summary>
/// Visual setup for the SQL editor, split by cost so first paint isn't blocked.
/// <see cref="Apply"/> is cheap and runs synchronously (the editor is already dark on the first frame);
/// <see cref="InstallSqlHighlighting"/> builds the TextMate grammar/theme registry, which is ~100ms+, and can
/// land afterwards over plain — but correctly coloured — text.
/// <para>Read-only SQL surfaces (the history preview, the SQL-preview window) go through
/// <see cref="SqlViewer"/>, which shares the same registry and selection colour.</para>
/// </summary>
public static class EditorChrome
{
    /// <summary>Translucent selection (steel-blue ~40%), so syntax-highlighted glyphs stay readable through
    /// it — the opaque default paints solid over the coloured text. Shared with the read-only viewers: a
    /// selection in the history preview is how a past query gets to the clipboard, so it has to be visible
    /// there for the same reason.</summary>
    public static IBrush SelectionBrush { get; } = new SolidColorBrush(Color.FromArgb(0x66, 0x2B, 0x44, 0x55));

    /// <summary>Graphite surface (#1A2027), current-line highlight (#232B36), faint line numbers (#4E5865),
    /// and the translucent <see cref="SelectionBrush"/>.</summary>
    public static void Apply(TextEditor editor)
    {
        editor.Background = Res("Bg.Editor");
        editor.LineNumbersForeground = Res("Text.Faint");
        editor.Options.HighlightCurrentLine = true;
        var lineActive = (Res("Bg.LineActive") as ISolidColorBrush)?.Color ?? Colors.Transparent;
        editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(lineActive);
        editor.TextArea.TextView.CurrentLineBorder = new Pen(new SolidColorBrush(lineActive)); // no contrasting box
        editor.TextArea.SelectionBrush = SelectionBrush;
        ApplyFoldMarginColours(editor);
    }

    /// <summary>
    /// Colour the fold gutter from the app's palette. AvaloniaEdit 12's Fluent theme sets none of these, so
    /// the markers fell back to the library's own CLR defaults — a gray outline on a <b>white</b> fill with a
    /// black hover — which made the one part of the editor that ignored the dark variant a light-first chip
    /// in a graphite gutter (#74).
    /// <para>
    /// Dim rather than faint, and filled with the editor surface so the marker reads as a mark in the gutter
    /// rather than a chip on top of it: the same weight as the line numbers beside it, which is what the
    /// gutter's other content is. Applied as a style rather than to the margin instance, because the margin
    /// is installed later (by <c>SqlFoldingController</c>) and can be re-created by a re-template.
    /// </para>
    /// </summary>
    private static void ApplyFoldMarginColours(TextEditor editor)
    {
        var style = new Style(x => x.OfType<FoldingMargin>());
        style.Setters.Add(new Setter(FoldingMargin.FoldingMarkerBrushProperty, Res("Text.Dim")));
        style.Setters.Add(new Setter(FoldingMargin.FoldingMarkerBackgroundBrushProperty, Res("Bg.Editor")));
        // Hover: brighter stroke, same fill — the affordance is the contrast coming up, not a colour change.
        style.Setters.Add(new Setter(FoldingMargin.SelectedFoldingMarkerBrushProperty, Res("Text.Primary")));
        style.Setters.Add(new Setter(FoldingMargin.SelectedFoldingMarkerBackgroundBrushProperty, Res("Bg.Editor")));
        editor.Styles.Add(style);
    }

    // One registry for the process, built on first use. Constructing it is the ~100ms — parsing the grammar
    // and the theme — and it is immutable once built, so every further editor (the SQL-preview window, the
    // history preview) reuses it instead of paying again. Only ever touched from the UI thread.
    private static RegistryOptions? _sqlRegistry;

    /// <summary>The shared TextMate registry. First read is the expensive one; keep it off the startup path
    /// for surfaces the user may never open.</summary>
    internal static RegistryOptions SqlRegistry => _sqlRegistry ??= new RegistryOptions(ThemeName.DarkPlus);

    /// <summary>Install TextMate SQL syntax highlighting. DarkPlus supplies token colours; exact Bearing
    /// syntax hues are deferred (needs internal TextMateSharp APIs — docs/design/editor-4a/README.md
    /// §Fidelity). Going through here is what keeps every SQL surface changing together when they land.</summary>
    public static void InstallSqlHighlighting(TextEditor editor)
    {
        var options = SqlRegistry;
        var installation = editor.InstallTextMate(options);
        var sql = options.GetLanguageByExtension(".sql");
        if (sql is not null)
            installation.SetGrammar(options.GetScopeByLanguageId(sql.Id));
    }
}
