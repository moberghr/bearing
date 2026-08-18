using Bearing.Core.Schema;

namespace Bearing.Sql;

/// <summary>
/// Generates a short table alias, avoiding collisions with aliases already in scope. Ported from
/// the prototype's DetermineAlias: initials of the words in the name (film_actor → fa,
/// __MigrationHistory → mh — see <see cref="IdentifierWords"/>), else the first letter
/// (film → f), with numeric disambiguation.
/// </summary>
public static class AliasResolver
{
    public static string Determine(TableInfo table, IEnumerable<string> existingAliases)
    {
        var baseAlias = BaseAlias(table.Name);

        var taken = new HashSet<string>(existingAliases, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseAlias)) return baseAlias;

        var n = 2;
        while (taken.Contains(baseAlias + n)) n++;
        return baseAlias + n;
    }

    /// <summary>
    /// The collision-free part: initials for a multi-word name, the first letter for a single word.
    /// Falls back to <c>t</c> when the name contributes no letters at all (all-punctuation, or empty),
    /// since an alias has to be a bare identifier — quoting it would defeat the point.
    /// </summary>
    private static string BaseAlias(string name)
    {
        var words = IdentifierWords.Split(name).Where(w => char.IsLetter(w[0])).ToList();
        if (words.Count == 0) return "t";
        if (words.Count > 1) return string.Concat(words.Select(w => char.ToLowerInvariant(w[0])));
        return words[0][..1].ToLowerInvariant();
    }
}
