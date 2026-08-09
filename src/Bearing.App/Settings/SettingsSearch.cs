using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Input;
using Bearing.Core.Workspace;

namespace Bearing.App.Settings;

/// <summary>One section's worth of filtered results — a category and the settings that survived the filter.</summary>
public sealed record SettingsSection(SettingsCategory Category, IReadOnlyList<SettingDescriptor> Settings);

/// <summary>
/// Filters and ranks the settings catalog for the settings window's search box. Pure and UI-free so the
/// matching rules are unit-testable without a window (§2.5).
/// <para>
/// Sections keep their declared order rather than being re-ordered by relevance — with a catalog this
/// size, a stable layout you can learn beats a list that reshuffles as you type. Ranking applies
/// <i>within</i> a section, and empty sections drop out.
/// </para>
/// </summary>
public static class SettingsSearch
{
    /// <summary>
    /// The sections to render. <paramref name="categoryId"/> null or empty means every category;
    /// <paramref name="query"/> null or blank means no filtering (declaration order).
    /// </summary>
    public static IReadOnlyList<SettingsSection> Filter(
        string? query,
        string? categoryId = null,
        IEnumerable<SettingDescriptor>? catalog = null,
        IEnumerable<SettingsCategory>? categories = null)
    {
        var all = (catalog ?? SettingsCatalog.All).ToList();
        var cats = (categories ?? SettingsCatalog.Categories).ToList();

        if (!string.IsNullOrEmpty(categoryId))
            cats = cats.Where(c => c.Id == categoryId).ToList();

        var sections = new List<SettingsSection>();
        foreach (var cat in cats)
        {
            var rows = all.Where(d => d.CategoryId == cat.Id);
            rows = string.IsNullOrWhiteSpace(query)
                ? rows
                : rows.Select(d => (Setting: d, Score: Score(d, query!)))
                      .Where(x => x.Score.HasValue)
                      .OrderByDescending(x => x.Score!.Value)
                      .Select(x => x.Setting);

            var list = rows.ToList();
            if (list.Count > 0) sections.Add(new SettingsSection(cat, list));
        }
        return sections;
    }

    /// <summary>
    /// How well a setting matches, or null for no match. Two passes, in order of how much the user
    /// probably meant it:
    /// <list type="number">
    /// <item>every whitespace-separated token appears somewhere in the setting's text — the normal case,
    /// scored by where and how well the tokens hit the <i>title</i>;</item>
    /// <item>failing that, a fuzzy subsequence match on the title alone, so an abbreviated or
    /// mistyped query ("fntsize") still lands.</item>
    /// </list>
    /// </summary>
    public static int? Score(SettingDescriptor setting, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return 0;

        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var haystack = setting.SearchText.ToLowerInvariant();
        var title = setting.Title.ToLowerInvariant();

        if (tokens.All(t => haystack.Contains(t.ToLowerInvariant(), StringComparison.Ordinal)))
        {
            var score = 10;
            foreach (var token in tokens)
            {
                var t = token.ToLowerInvariant();
                var idx = title.IndexOf(t, StringComparison.Ordinal);
                if (idx < 0) continue;                                            // matched via description/keywords only
                score += 40;                                                      // a title hit is worth far more
                if (idx == 0 || !char.IsLetterOrDigit(title[idx - 1])) score += 15; // …at a word boundary, more still
                score -= Math.Min(idx, 20);                                       // earlier in the title wins
            }
            return score;
        }

        // Fuzzy fallback: same subsequence scorer the command palette uses, title only.
        return PaletteFilter.Score(setting.Title, q);
    }
}
