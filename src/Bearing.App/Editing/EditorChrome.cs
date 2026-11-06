using System;
using System.Collections.Generic;
using System.Linq;
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
    /// there for the same reason. The <c>Select.Text</c> token, so it follows a light/dark switch.</summary>
    public static IBrush SelectionBrush => Res("Select.Text");

    /// <summary>Editor surface, current-line highlight, faint line numbers, and the translucent
    /// <see cref="SelectionBrush"/> — all the live token brushes, so a light/dark switch repaints them.</summary>
    public static void Apply(TextEditor editor)
    {
        editor.Background = Res("Bg.Editor");
        editor.LineNumbersForeground = Res("Text.Faint");
        editor.Options.HighlightCurrentLine = true;
        var lineActive = Res("Bg.LineActive");
        editor.TextArea.TextView.CurrentLineBackground = lineActive;
        editor.TextArea.TextView.CurrentLineBorder = new Pen(lineActive); // no contrasting box
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
    // history preview) reuses it instead of paying again.
    //
    // One lazy, not two: the wrapper and the registry it wraps have to be the same pair, and two independent
    // `??=` fields can be raced into disagreeing — one thread building the wrapper while another builds a
    // second registry, leaving SqlRegistry answering from an instance SqlOptions does not wrap. Harmless in
    // what it resolves, but it is two ~100ms registries held for the life of the process, and the shape is
    // the bug rather than the symptom.
    private static SqlGrammarInjection? _sqlOptions;

    /// <summary>What TextMate is actually handed: the packaged registry, plus
    /// <see cref="SqlGrammarInjection"/>'s one rule for the boolean literals it has none for.</summary>
    internal static SqlGrammarInjection SqlOptions =>
        _sqlOptions ??= new SqlGrammarInjection(new RegistryOptions(ThemeName.DarkPlus));

    /// <summary>The shared TextMate registry. First read is the expensive one; keep it off the startup path
    /// for surfaces the user may never open.</summary>
    internal static RegistryOptions SqlRegistry => SqlOptions.Registry;

    /// <summary>Install TextMate SQL syntax highlighting. DarkPlus / LightPlus supply token colours; exact
    /// Bearing syntax hues are deferred (needs internal TextMateSharp APIs — docs/design/editor-4a/README.md
    /// §Fidelity). Going through here is what keeps every SQL surface changing together when they land, and
    /// what lets <see cref="ApplyColorScheme"/> find them.</summary>
    public static void InstallSqlHighlighting(TextEditor editor)
    {
        var installation = editor.InstallTextMate(SqlOptions);
        var sql = SqlRegistry.GetLanguageByExtension(".sql");
        if (sql is not null)
        {
            installation.SetGrammar(SqlRegistry.GetScopeByLanguageId(sql.Id));
        }
        // The registry's default theme is DarkPlus; a surface opened while light is showing starts light.
        if (Theming.ColorSchemes.IsLight(Avalonia.Application.Current))
        {
            installation.SetTheme(SqlRegistry.LoadTheme(ThemeName.LightPlus));
        }
        lock (Installed)
        {
            Installed.RemoveAll(w => !w.TryGetTarget(out _));
            Installed.Add(new WeakReference<Highlighted>(new Highlighted(editor, installation)));
        }
    }

    private sealed record Highlighted(TextEditor Editor, TextMate.Installation Installation);

    /// <summary>Every highlighted surface still alive. Weak, because the history preview and the SQL-preview
    /// windows come and go and nothing tells this class they went.</summary>
    private static readonly List<WeakReference<Highlighted>> Installed = new();

    /// <summary>
    /// Re-theme every live SQL surface for a light/dark switch: the TextMate theme (its own palette, which
    /// the token repaint cannot reach), then a redraw, because the selection and current-line layers paint
    /// their brushes only when asked to.
    /// </summary>
    public static void ApplyColorScheme(bool light)
    {
        List<Highlighted> live;
        lock (Installed)
        {
            Installed.RemoveAll(w => !w.TryGetTarget(out _));
            live = Installed.Select(w => w.TryGetTarget(out var h) ? h : null).OfType<Highlighted>().ToList();
        }
        if (live.Count == 0)
        {
            return;
        }
        var theme = SqlRegistry.LoadTheme(light ? ThemeName.LightPlus : ThemeName.DarkPlus);
        foreach (var h in live)
        {
            h.Installation.SetTheme(theme);
            h.Editor.TextArea.TextView.Redraw();
        }
    }
}
