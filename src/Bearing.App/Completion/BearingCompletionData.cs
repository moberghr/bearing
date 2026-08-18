using System;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Bearing.Core.Completion;

namespace Bearing.App.Completion;

/// <summary>
/// Adapts a <see cref="Suggestion"/> to AvaloniaEdit's completion list, and carries the pieces the
/// row template draws: kind glyph, label, dimmed detail, trailing preview. The engine stays UI-agnostic
/// — <see cref="SuggestionKind"/> exists precisely so this layer picks the glyph.
/// </summary>
internal sealed class BearingCompletionData : ICompletionData
{
    private readonly Suggestion _s;
    private readonly Action<Suggestion>? _inserted;

    /// <param name="inserted">Called after the text is inserted (a schema pick re-opens the popup for
    /// the relations under it).</param>
    public BearingCompletionData(Suggestion s, Action<Suggestion>? inserted = null)
    {
        _s = s;
        _inserted = inserted;
    }

    /// <summary>Unused: the row is drawn by <see cref="CompletionItemTemplate"/>, which resolves the
    /// kind glyph as a stroked <see cref="Geometry"/> — <c>Themes/Icons.axaml</c> holds geometries, not
    /// images, and stroking is what makes them recolor per kind.</summary>
    public IImage? Image => null;

    /// <summary>Text AvaloniaEdit matches the typed prefix against. Narrowing is ours
    /// (<see cref="SuggestionRanker"/>), but its own caret handler still prefix-selects on this.</summary>
    public string Text => _s.FilterText;

    /// <summary>
    /// Only a fallback: the row is drawn by <see cref="CompletionItemTemplate"/> from the fields below.
    /// It stays a plain string so that if the template ever fails to attach, the list still reads as
    /// names rather than as a type name — and so the old four-space column padding is gone for good.
    /// </summary>
    public object Content => _s.DisplayText;

    public object Description => _s.Description ?? _s.DisplayText;

    public double Priority => _s.Priority;

    // ---- Row template surface ----------------------------------------------------------------

    public string DisplayText => _s.DisplayText;

    /// <summary>Dimmed secondary text: the schema, or the owning alias.</summary>
    public string? DetailText => _s.DetailText;

    /// <summary>Right-aligned preview — the synthesized join predicate. Nothing displayed this before.</summary>
    public string? TrailingText => _s.TrailingText;

    public SuggestionKind Kind => _s.Kind;

    /// <summary>Resource key of the kind glyph in <c>Themes/Icons.axaml</c>.</summary>
    public string IconKey => Kind switch
    {
        SuggestionKind.Table => "Icon.Table",
        SuggestionKind.View => "Icon.View",
        SuggestionKind.Column => "Icon.Column",
        SuggestionKind.Keyword => "Icon.Keyword",
        SuggestionKind.Join or SuggestionKind.JoinPredicate => "Icon.Join",
        SuggestionKind.Schema => "Icon.Schema",
        SuggestionKind.Function => "Icon.Function",
        SuggestionKind.Snippet => "Icon.Snippet",
        // Alias is declared but never emitted; a column-ish mark is the closest honest answer.
        _ => "Icon.Column",
    };

    /// <summary>Token key of the glyph colour, so kinds read apart by hue as well as by shape.</summary>
    public string IconColorKey => Kind switch
    {
        SuggestionKind.Table => "Syntax.Table",
        SuggestionKind.View => "Syntax.Func",
        SuggestionKind.Column => "Text.Muted",
        SuggestionKind.Keyword => "Syntax.Keyword",
        SuggestionKind.Join or SuggestionKind.JoinPredicate => "Accent.Brand",
        SuggestionKind.Schema => "Warn.Amber",
        SuggestionKind.Function => "Syntax.Func",
        _ => "Text.Dim",
    };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, _s.ReplacementText);
        _inserted?.Invoke(_s);
    }
}
