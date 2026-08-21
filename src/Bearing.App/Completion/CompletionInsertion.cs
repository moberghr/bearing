using AvaloniaEdit.Document;
using Bearing.Core.Completion;

namespace Bearing.App.Completion;

/// <summary>
/// What accepting a suggestion actually types: the engine's <see cref="Suggestion.ReplacementText"/>
/// plus the one character that would otherwise cost an extra keystroke (#41). The engine stays out of
/// this — <c>ReplacementText</c> is the SQL, the suffix is an editing affordance — and it is pure, so
/// the per-kind decision is assertable without driving the popup (§4.3).
/// </summary>
internal static class CompletionInsertion
{
    /// <summary>
    /// What follows the replacement text for <paramref name="kind"/>, or <c>""</c> for nothing.
    /// The trailing space cannot be blanket-appended: some kinds are the wrong shape for it.
    /// </summary>
    public static string SuffixFor(SuggestionKind kind) => kind switch
    {
        // A schema insertion ends in '.' and the caret has to stay glued to it — the relation list
        // completes from there (CompletionController.OnInserted reopens the popup). A space breaks
        // the next completion outright.
        SuggestionKind.Schema => "",
        // Neither of these is emitted yet. A call wants its argument list open, not a space; a snippet
        // would carry its own trailing text, so appending to it would be guessing.
        SuggestionKind.Function => "(",
        SuggestionKind.Snippet => "",
        // Everything the engine does emit — keyword, table/view, column, join clause, FK predicate —
        // is followed by more SQL: `settlements s `, `select `, `u.id `.
        _ => " ",
    };

    /// <summary>
    /// The text to insert for <paramref name="s"/>, given the character that already follows the
    /// replacement span (null at end of document).
    /// </summary>
    public static string TextFor(Suggestion s, char? next)
    {
        var suffix = SuffixFor(s.Kind);
        if (suffix.Length == 0) return s.ReplacementText;
        if (next is { } ch && AlreadyHandled(ch, suffix)) return s.ReplacementText;
        return s.ReplacementText + suffix;
    }

    /// <summary>
    /// True when the character already sitting after the replacement span makes the suffix unwanted: it
    /// <em>is</em> the suffix, it is horizontal whitespace that already separates the tokens
    /// (re-completing <c>select id| from t</c> must not double the space), or it is a delimiter a space
    /// would read wrong before (<c>select id|, name</c> — nothing is coming to reclaim that one).
    /// <para>
    /// A line break deliberately does not count. The caret at the end of a line is the normal place to
    /// complete, and what gets typed next lands directly against the insertion, so the space is needed
    /// exactly as much as at end of document. Counting <c>\n</c> as "already spaced" via
    /// <c>char.IsWhiteSpace</c> is what made the suffix never appear in the running app at all.
    /// </para>
    /// </summary>
    private static bool AlreadyHandled(char next, string suffix)
        => next == suffix[0]
           || (suffix == " " && (next is ' ' or '\t' || SwallowsTrailingSpace(next)));

    /// <summary>
    /// Insert <paramref name="s"/> over <paramref name="segment"/>, returning the offset of the soft
    /// space it appended or -1 if it appended none. Takes the document rather than the <c>TextArea</c>
    /// so the offset arithmetic is exercisable without a UI (§4.3).
    /// </summary>
    public static int Apply(TextDocument document, ISegment segment, Suggestion s)
    {
        var end = segment.EndOffset;
        var next = end < document.TextLength ? document.GetCharAt(end) : (char?)null;
        var text = TextFor(s, next);

        // The segment is an anchor: Replace moves it, so the start is read before the edit.
        var start = segment.Offset;
        document.Replace(segment, text);

        return text.Length > s.ReplacementText.Length && text[^1] == ' '
            ? start + text.Length - 1
            : -1;
    }

    /// <summary>
    /// Take back the soft space at <paramref name="softSpaceOffset"/> when <paramref name="typed"/> has
    /// just landed directly after it. Returns whether the space was removed.
    /// <para>
    /// The caret check is what keeps this to a single keystroke: it must sit past both the space and the
    /// character, so moving away and typing a comma somewhere else leaves the document alone.
    /// </para>
    /// </summary>
    public static bool TrySwallow(TextDocument document, int caretOffset, int softSpaceOffset, char typed)
    {
        if (softSpaceOffset < 0 || !SwallowsTrailingSpace(typed)) return false;
        if (caretOffset != softSpaceOffset + 2) return false;
        if (softSpaceOffset >= document.TextLength || document.GetCharAt(softSpaceOffset) != ' ') return false;

        document.Remove(softSpaceOffset, 1);
        return true;
    }

    /// <summary>
    /// True when <paramref name="typed"/> is a character that has to sit tight against the completion,
    /// so the space just appended is taken back again: <c>select u.id , u.name</c> and
    /// <c>count(u.id )</c> both read badly. Operators are deliberately absent — <c>u.id = 1</c> wants
    /// the space kept, which is the whole point of appending it.
    /// </summary>
    public static bool SwallowsTrailingSpace(char typed) => typed is ',' or ')' or ';';
}
