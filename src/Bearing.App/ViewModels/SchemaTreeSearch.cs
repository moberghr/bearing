using System.Collections.Generic;
using System.Linq;

namespace Bearing.App.ViewModels;

/// <summary>
/// The pure half of the schema tree's type-ahead: which nodes a query reaches, and what has to be expanded
/// to put one on screen. Extracted from the sidebar's code-behind so it can be tested without a tree (§2.5) —
/// the highlight/reset asymmetry it now guards against was invisible to every automated check.
/// </summary>
internal static class SchemaTreeSearch
{
    /// <summary>
    /// Depth-first list of every loaded, non-placeholder node, <b>collapsed ones included</b>. Expansion state
    /// is deliberately not a filter: a node expanded once keeps its children in memory, and walking only the
    /// visible ones left those hidden children out of both the highlight pass and the reset pass — so a
    /// collapsed table's columns came back showing a stale highlight, or none at all despite matching. One set
    /// for matching, highlighting and clearing keeps "matched" and "highlighted" the same thing.
    /// A never-expanded node holds only its "Loading…" placeholder, so this touches no I/O.
    /// </summary>
    public static List<SchemaNodeViewModel> Flatten(IEnumerable<SchemaNodeViewModel> roots)
    {
        var list = new List<SchemaNodeViewModel>();
        void Walk(IEnumerable<SchemaNodeViewModel> ns)
        {
            foreach (var n in ns)
            {
                if (n is MessageNodeViewModel) continue;
                list.Add(n);
                Walk(n.Children);
            }
        }
        Walk(roots);
        return list;
    }

    /// <summary>Every loaded node whose title fuzzy-matches, in tree order.</summary>
    public static List<SchemaNodeViewModel> Matches(IEnumerable<SchemaNodeViewModel> roots, string query)
        => Flatten(roots).Where(n => FuzzyMatch(n.Title, query)).ToList();

    /// <summary>Case-insensitive subsequence (fuzzy) match: query chars appear in order in the text.</summary>
    public static bool FuzzyMatch(string text, string query)
    {
        if (string.IsNullOrEmpty(query)) return false;
        text = text.ToLowerInvariant(); query = query.ToLowerInvariant();
        var ti = 0;
        foreach (var c in query)
        {
            ti = text.IndexOf(c, ti);
            if (ti < 0) return false;
            ti++;
        }
        return true;
    }

    /// <summary>
    /// The chain of ancestors between the roots and <paramref name="target"/>, outermost first, or empty when
    /// the target isn't in the tree. Expanding all of them is what puts a match under a collapsed parent — a
    /// Views / Functions bucket, or a table whose columns are still loaded from an earlier expand — on screen.
    /// Every ancestor here already has its children loaded (that is how the target was found), so expanding
    /// them fires no lazy load.
    /// </summary>
    public static List<SchemaNodeViewModel> AncestorsOf(IEnumerable<SchemaNodeViewModel> roots, SchemaNodeViewModel target)
    {
        var path = new List<SchemaNodeViewModel>();
        return Walk(roots) ? path : new List<SchemaNodeViewModel>();

        bool Walk(IEnumerable<SchemaNodeViewModel> ns)
        {
            foreach (var n in ns)
            {
                if (ReferenceEquals(n, target)) return true;
                path.Add(n);
                if (Walk(n.Children)) return true;
                path.RemoveAt(path.Count - 1);
            }
            return false;
        }
    }
}
