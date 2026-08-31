using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.Core.Data;

namespace Bearing.App.Connections;

/// <summary>
/// The pure shape of the connections panel: connections plus declared folders, in, a nested spec out.
/// No view models, no tree control, no live connection — so the ordering, the folder inference and the
/// filter's behaviour around folders are all unit-testable, which is the only way to cover them at all
/// (§2.5, §4.3). Follows <c>ScriptsViewModel.BuildScriptNodes</c>, which does the same job for scripts.
/// </summary>
public static class ConnectionTree
{
    /// <summary>One folder and everything filed directly under it. The root is a folder with a null
    /// <see cref="Path"/>.</summary>
    public sealed record Folder(
        string? Path,
        IReadOnlyList<Folder> Folders,
        IReadOnlyList<ConnectionInfo> Connections)
    {
        /// <summary>Label for the row — the last path segment. Null only for the root.</summary>
        public string? Name => FolderPath.Name(Path);

        /// <summary>Connections anywhere beneath this folder. Shown right-aligned on the row, so a
        /// collapsed folder still says how much it is hiding.</summary>
        public int Count => Connections.Count + Folders.Sum(f => f.Count);
    }

    /// <summary>
    /// Build the panel's tree.
    ///
    /// <para>Folders come from two places and the union is deliberate: <paramref name="declaredFolders"/>
    /// keeps an <b>empty</b> folder alive, and any path a connection claims is materialised whether it was
    /// declared or not — so a hand-edited <c>project.json</c>, or an import that wrote membership without
    /// declaring the folders, can never leave a connection filed somewhere the panel doesn't draw.</para>
    ///
    /// <para>Ordering is folders first, then connections, each alphabetically and case-insensitively.
    /// Manifest order is append order, which is not an order anybody chose.</para>
    /// </summary>
    /// <param name="filter">
    /// Narrows the tree. A connection survives on its own merits (<see cref="ConnectionSearch.Matches"/>);
    /// a folder survives when something under it did, <i>or</i> when the folder's own name matches — in which
    /// case it keeps all of its contents, because having asked for "Aur Production" by name you want what is
    /// in it. Folders left empty by the filter are dropped, declared-but-empty ones included: while you are
    /// searching, a folder that cannot contain a hit is noise.
    /// </param>
    public static Folder Build(
        IEnumerable<ConnectionInfo> connections,
        IEnumerable<string>? declaredFolders = null,
        string filter = "")
    {
        var all = connections.ToList();

        // Every folder that must exist, canonical and de-duplicated, with each ancestor implied by a
        // nested path materialised too ("A/B" declared alone still draws "A").
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declared in declaredFolders ?? Enumerable.Empty<string>())
            foreach (var step in FolderPath.Ancestry(declared)) paths.Add(step);
        foreach (var c in all)
            foreach (var step in FolderPath.Ancestry(c.Folder)) paths.Add(step);

        return BuildFolder(null, paths, all, filter.Trim(), keepAll: false);
    }

    private static Folder BuildFolder(
        string? path,
        HashSet<string> paths,
        IReadOnlyList<ConnectionInfo> all,
        string filter,
        bool keepAll)
    {
        // A folder whose own name matched hands "keep everything" down to its whole subtree.
        var matchedHere = keepAll || (filter.Length > 0 && path is not null
            && FolderPath.Name(path)!.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var children = paths
            .Where(p => string.Equals(FolderPath.Parent(p), path, StringComparison.OrdinalIgnoreCase))
            .Select(p => BuildFolder(p, paths, all, filter, matchedHere))
            .Where(f => filter.Length == 0 || matchedHere || f.Count > 0)
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var here = all
            .Where(c => string.Equals(FolderPath.Normalize(c.Folder), path, StringComparison.OrdinalIgnoreCase))
            .Where(c => matchedHere || ConnectionSearch.Matches(c, filter))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Folder(path, children, here);
    }
}
