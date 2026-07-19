using System.Collections.Generic;
using System.Linq;

namespace Squirrel.App.Input;

/// <summary>
/// Ranks commands for the command palette. Pure and dependency-free so the ranking is unit-testable.
/// Empty query → everything, grouped then alphabetical. Non-empty → fuzzy subsequence match on the
/// title, best score first (contiguous runs and word-boundary hits score higher, earlier matches win).
/// </summary>
public static class PaletteFilter
{
    public static IReadOnlyList<KeyCommand> Rank(IEnumerable<KeyCommand> commands, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return commands.OrderBy(c => c.Group).ThenBy(c => c.Title).ToList();

        return commands
            .Select(c => (Command: c, Score: Score(c.Title, query.Trim())))
            .Where(x => x.Score.HasValue)
            .OrderByDescending(x => x.Score!.Value)
            .ThenBy(x => x.Command.Title)
            .Select(x => x.Command)
            .ToList();
    }

    /// <summary>Subsequence score, or null when <paramref name="query"/> isn't a subsequence of the title.</summary>
    public static int? Score(string title, string query)
    {
        var t = title.ToLowerInvariant();
        var q = query.ToLowerInvariant();

        var from = 0;
        var score = 0;
        var firstIdx = -1;
        var prevIdx = -2;
        foreach (var c in q)
        {
            var idx = t.IndexOf(c, from);
            if (idx < 0) return null;
            if (firstIdx < 0) firstIdx = idx;
            if (idx == prevIdx + 1) score += 5;                                   // contiguous run
            if (idx == 0 || !char.IsLetterOrDigit(t[idx - 1])) score += 3;        // start of a word
            prevIdx = idx;
            from = idx + 1;
        }
        return score - firstIdx;                                                  // earlier first hit is better
    }
}
