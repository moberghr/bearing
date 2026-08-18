using System.Collections.Generic;
using System.Linq;

namespace Bearing.App.Input;

/// <summary>
/// Ranks commands for the command palette. Pure and dependency-free so the ranking is unit-testable.
/// Empty query → everything, grouped then alphabetical. Non-empty → fuzzy subsequence match on the
/// title (<see cref="FuzzyMatcher"/>), best match first: match quality decides the order, the score
/// breaks ties inside a quality.
/// </summary>
public static class PaletteFilter
{
    public static IReadOnlyList<KeyCommand> Rank(IEnumerable<KeyCommand> commands, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return commands.OrderBy(c => c.Group).ThenBy(c => c.Title).ToList();

        var trimmed = query.Trim();
        return commands
            .Select(c => (Command: c, Match: FuzzyMatcher.Match(c.Title, trimmed)))
            .Where(x => x.Match.IsMatch)
            .OrderByDescending(x => x.Match.Quality)
            .ThenByDescending(x => x.Match.Score)
            .ThenBy(x => x.Command.Title)
            .Select(x => x.Command)
            .ToList();
    }

    /// <summary>Subsequence score, or null when <paramref name="query"/> isn't a subsequence of the title.</summary>
    public static int? Score(string title, string query)
    {
        var match = FuzzyMatcher.Match(title, query);
        return match.IsMatch ? match.Score : null;
    }
}
