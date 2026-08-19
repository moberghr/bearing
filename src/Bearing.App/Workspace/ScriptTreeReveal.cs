using System.Collections.Generic;
using System.IO;
using Bearing.App.ViewModels;

namespace Bearing.App.Workspace;

/// <summary>
/// Finding a file in the Scripts tree: the walk from the tree's roots down to the node for a given path.
/// Pure and separate from <see cref="ViewModels.ScriptsViewModel"/> (§2.5) — "which nodes lead to this
/// file?" is answerable without a TreeView, which is the only way to test it here (§4.3).
/// </summary>
public static class ScriptTreeReveal
{
    /// <summary>
    /// The folders leading to <paramref name="fullPath"/>, outermost first, followed by the node for the
    /// path itself (a <see cref="ScriptItem"/>, or the folder when the path names one). Empty when the path
    /// isn't in the tree — a file outside the project, or one the tree hasn't picked up yet.
    /// </summary>
    /// <param name="nodes">The tree roots (<see cref="ScriptsViewModel.ScriptNodes"/>): a mix of
    /// <see cref="ScriptFolderViewModel"/> and <see cref="ScriptItem"/>.</param>
    public static IReadOnlyList<object> PathTo(IEnumerable<object> nodes, string fullPath)
    {
        var target = Normalize(fullPath);
        var chain = new List<object>();
        return Walk(nodes) ? chain : System.Array.Empty<object>();

        bool Walk(IEnumerable<object> level)
        {
            foreach (var node in level)
            {
                switch (node)
                {
                    case ScriptItem item when string.Equals(Normalize(item.FullPath), target, PathComparison):
                        chain.Add(item);
                        return true;
                    case ScriptFolderViewModel folder:
                        chain.Add(folder);
                        if (string.Equals(Normalize(folder.FullPath), target, PathComparison)) return true;
                        if (Walk(folder.Children)) return true;
                        chain.RemoveAt(chain.Count - 1);
                        break;
                }
            }
            return false;
        }
    }

    /// <summary>Case-insensitive, matching how the rest of the scripts tree compares names
    /// (<see cref="ScratchNaming"/>) — the same file typed with different casing is the same file here.</summary>
    private const System.StringComparison PathComparison = System.StringComparison.OrdinalIgnoreCase;

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
