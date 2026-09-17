using System.Collections.Generic;
using System.Linq;
using Bearing.Core.Schema;

namespace Bearing.App.Workspace;

/// <summary>
/// A snapshot's partition hierarchy, indexed once.
/// <para>
/// Exists for two reasons, both bugs it fixes. It is <b>indexed</b>, because the first version asked
/// <c>snapshot.Tables.Any(p =&gt; p.Id == parent)</c> per partition — a linear scan inside
/// <c>LoadChildrenAsync</c>, which is awaited before the tree renders, so a database with a few thousand
/// partitions paid millions of comparisons on exactly the path #76 and #119 moved their reads off. And it is
/// <b>recursive</b>, because a partition can itself be partitioned (range then list is an ordinary Postgres
/// shape) and the first version handed each nested node no map at all, so a sub-partition was reachable from
/// nowhere in the tree.
/// </para>
/// </summary>
public sealed class PartitionMap
{
    private readonly Dictionary<long, List<TableInfo>> _children;
    private readonly HashSet<long> _known;

    private PartitionMap(Dictionary<long, List<TableInfo>> children, HashSet<long> known)
    {
        _children = children;
        _known = known;
    }

    /// <summary>An empty map, for a caller with no snapshot to index.</summary>
    public static PartitionMap Empty { get; } = new([], []);

    public static PartitionMap Of(ISchemaSnapshot snapshot)
    {
        var children = new Dictionary<long, List<TableInfo>>();
        var known = new HashSet<long>();

        foreach (var table in snapshot.Tables)
        {
            known.Add(table.Id);
            if (table.PartitionOf is not { } parent) continue;
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
            list.Add(table);
        }

        foreach (var list in children.Values)
            list.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));

        return new PartitionMap(children, known);
    }

    /// <summary>The partitions of one relation, in name order. Empty for a relation with none.</summary>
    public IReadOnlyList<TableInfo> ChildrenOf(long tableId)
        => _children.TryGetValue(tableId, out var list) ? list : [];

    /// <summary>
    /// Whether this relation should be left out of a flat list because its parent is in the same list.
    /// <para>
    /// False when the parent is <em>not</em> in the snapshot — a partition whose parent was filtered out (a
    /// different schema's, say) has to appear somewhere rather than vanish, so it stays at the top level.
    /// </para>
    /// </summary>
    public bool NestsUnderAKnownParent(TableInfo table)
        => table.PartitionOf is { } parent && _known.Contains(parent);

    /// <summary>Relations to show at the top of a list: everything that is not nested under a known parent.</summary>
    public IEnumerable<TableInfo> TopLevel(IEnumerable<TableInfo> tables)
        => tables.Where(t => !NestsUnderAKnownParent(t));
}
