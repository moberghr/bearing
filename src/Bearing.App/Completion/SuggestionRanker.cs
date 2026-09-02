using System.Collections.Generic;
using System.Linq;
using Bearing.App.Input;
using Bearing.Core.Completion;

namespace Bearing.App.Completion;

/// <summary>
/// Narrows and orders the engine's suggestions against the partially-typed word under the caret.
/// <para>
/// This exists because AvaloniaEdit's own list filtering can't do it: its scorer only knows full match /
/// match-start / substring / camel-case, so <c>al</c> — neither a prefix nor a substring of
/// <c>accounting_lines</c> — dropped the item out of the popup entirely. The controller therefore sets
/// <c>IsFiltering = false</c> and narrows through here instead.
/// </para>
/// <para>
/// Ordering: an <i>incidentally</i> matched keyword drops below everything else first, then match quality
/// (an exact or prefix hit is never buried under a scattered one), then the engine's
/// <see cref="Suggestion.Priority"/> (joins &gt; tables &gt; columns &gt; keywords), then the fuzzy score,
/// then the label. So <c>WHERE</c> stays reachable while typing <c>wh</c>, and at equal quality a table
/// still beats a keyword.
/// </para>
/// <para>
/// <b>Incidental</b> means the query is only a substring of the keyword, or scattered through it — the
/// letters happen to be in there, in order, and none of its beginning was typed. Those are the hits that put
/// <c>delete</c> above every table while someone types <c>ete</c>. A keyword whose start or whose
/// word-initials were typed is <i>not</i> demoted: that is a keyword being asked for.
/// </para>
/// Pure — no popup, no editor (§2.5, §4.3).
/// </summary>
public static class SuggestionRanker
{
    /// <summary>
    /// Suggestions that match <paramref name="typed"/>, best first. An empty query keeps the engine's
    /// own order (already Priority-then-name), so opening the popup with Ctrl+Space looks unchanged.
    /// </summary>
    public static IReadOnlyList<Suggestion> Rank(IReadOnlyList<Suggestion> suggestions, string typed)
    {
        // A span that stopped being a name under construction ends completion: typing a space after
        // Ctrl+Space used to trim to an empty query and hold the entire schema on screen.
        if (!IsNameFragment(typed)) return Array.Empty<Suggestion>();

        var query = Query(typed);
        if (query.Length == 0) return suggestions;

        return suggestions
            .Select(s => (Suggestion: s, Match: FuzzyMatcher.Match(s.FilterText, query)))
            .Where(x => x.Match.IsMatch)
            .OrderBy(x => Incidental(x.Suggestion, x.Match.Quality))
            .ThenByDescending(x => x.Match.Quality)
            .ThenByDescending(x => x.Suggestion.Priority)
            .ThenByDescending(x => x.Match.Score)
            .ThenBy(x => x.Suggestion.DisplayText, System.StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Suggestion)
            .ToList();
    }

    /// <summary>
    /// A keyword the query only happens to be buried inside. Sorted last, so schema objects come first.
    /// <para>
    /// The cut sits above <see cref="MatchQuality.Initials"/> on purpose: a keyword whose word-initials were
    /// typed was asked for, and a two-word keyword is exactly where initials are the natural shorthand. Only
    /// Substring and Subsequence count as incidental.
    /// </para>
    /// </summary>
    private static int Incidental(Suggestion suggestion, MatchQuality quality)
        => suggestion.Kind == SuggestionKind.Keyword && quality <= MatchQuality.Substring ? 1 : 0;

    /// <summary>
    /// True while the replacement span still reads as an identifier being typed. Anything else — a space,
    /// a comma, a paren, an operator — means the caret has moved on and the popup should close rather
    /// than keep matching.
    /// </summary>
    public static bool IsNameFragment(string typed)
        => typed.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '$' or '"');

    /// <summary>
    /// The text the user has typed into the replacement span, as a matchable query: a partially-typed
    /// quoted identifier (<c>"__Mig</c>) matches on its content, since the list shows bare names.
    /// </summary>
    private static string Query(string typed)
    {
        var q = typed.Trim();
        if (q.StartsWith('"')) q = q[1..].TrimEnd('"');
        return q;
    }
}
