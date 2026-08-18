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
/// Ordering: match quality first (an exact or prefix hit is never buried under a scattered one), then
/// the engine's <see cref="Suggestion.Priority"/> (joins &gt; tables &gt; columns &gt; keywords), then
/// the fuzzy score, then the label. So <c>WHERE</c> stays reachable while typing <c>wh</c>, and at equal
/// quality a table still beats a keyword.
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
            .OrderByDescending(x => x.Match.Quality)
            .ThenByDescending(x => x.Suggestion.Priority)
            .ThenByDescending(x => x.Match.Score)
            .ThenBy(x => x.Suggestion.DisplayText, System.StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Suggestion)
            .ToList();
    }

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
