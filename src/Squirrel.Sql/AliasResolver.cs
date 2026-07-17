using Squirrel.Core.Schema;

namespace Squirrel.Sql;

/// <summary>
/// Generates a short table alias, avoiding collisions with aliases already in scope. Ported from
/// the prototype's DetermineAlias but tuned for Postgres snake_case: initials of the underscore-
/// separated parts (film_actor → fa), else the first letter (film → f), with numeric disambiguation.
/// </summary>
public static class AliasResolver
{
    public static string Determine(PgTable table, IEnumerable<string> existingAliases)
    {
        var parts = table.Name.Split(new[] { '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var baseAlias = parts.Length > 1
            ? string.Concat(parts.Select(p => char.ToLowerInvariant(p[0])))
            : (table.Name.Length > 0 ? table.Name[..1].ToLowerInvariant() : "t");

        var taken = new HashSet<string>(existingAliases, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseAlias)) return baseAlias;

        var n = 2;
        while (taken.Contains(baseAlias + n)) n++;
        return baseAlias + n;
    }
}
