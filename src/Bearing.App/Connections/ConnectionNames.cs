using System;
using System.Collections.Generic;
using System.Linq;

namespace Bearing.App.Connections;

/// <summary>
/// The one rule for a connection name that must not collide with names already in a project — shared by
/// duplicate, paste, import and move-to-project, so every route that lands a connection beside others
/// suffixes it the same way.
/// </summary>
public static class ConnectionNames
{
    /// <summary>
    /// A name none of <paramref name="existing"/> uses (compared case-insensitively). A duplicate always gets
    /// a suffix (two rows reading <c>prod</c> would be worse than useless); a paste, import or move keeps its
    /// name when nothing clashes, because landing in a project that has never seen it should not rename it.
    /// </summary>
    public static string Unique(IEnumerable<string> existing, string baseName, bool alwaysSuffix = true)
    {
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!alwaysSuffix && !taken.Contains(baseName)) return baseName;

        var candidate = $"{baseName} (copy)";
        for (var n = 2; taken.Contains(candidate); n++) candidate = $"{baseName} (copy {n})";
        return candidate;
    }
}
