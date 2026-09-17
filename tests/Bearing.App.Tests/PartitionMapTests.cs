using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Workspace;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The partition hierarchy, indexed. Both of the bugs this type exists to fix are regression-tested here:
/// a sub-partition that was reachable from nowhere, and a parent lookup that scanned every relation per
/// partition on the path the tree renders from.
/// </summary>
public class PartitionMapTests
{
    private static SchemaSnapshot Snapshot(params TableInfo[] tables)
        => new("db", ["public"], tables, [], [], searchPath: ["public"]);

    private static TableInfo T(long id, string name, long? parent = null)
        => new(id, "public", name, RelationKind.Table, parent);

    [Fact]
    public void A_parent_reports_its_partitions_in_name_order()
    {
        var map = PartitionMap.Of(Snapshot(
            T(1, "event", null),
            T(3, "event_2027", 1),
            T(2, "event_2026", 1)));

        Assert.Equal(["event_2026", "event_2027"], map.ChildrenOf(1).Select(t => t.Name));
        Assert.Empty(map.ChildrenOf(2));
    }

    [Fact]
    public void A_partition_nests_and_a_plain_table_does_not()
    {
        var parent = T(1, "event", null);
        var child = T(2, "event_2026", 1);
        var plain = T(3, "store", null);
        var map = PartitionMap.Of(Snapshot(parent, child, plain));

        Assert.False(map.NestsUnderAKnownParent(parent));
        Assert.True(map.NestsUnderAKnownParent(child));
        Assert.False(map.NestsUnderAKnownParent(plain));

        Assert.Equal(["event", "store"], map.TopLevel([parent, child, plain]).Select(t => t.Name));
    }

    [Fact]
    public void A_partition_whose_parent_is_absent_stays_at_the_top_level()
    {
        // Its parent was filtered out (another schema's, say). Nesting it would make it vanish from the tree
        // entirely, which is worse than showing it beside the tables.
        var orphan = T(2, "event_2026", 999);
        var map = PartitionMap.Of(Snapshot(orphan));

        Assert.False(map.NestsUnderAKnownParent(orphan));
        Assert.Equal(["event_2026"], map.TopLevel([orphan]).Select(t => t.Name));
    }

    /// <summary>
    /// The bug this type was introduced for: with a two-level hierarchy the sub-partition was skipped at the
    /// top level (its parent was present) and never added below (each nested node was built with no map), so
    /// it was reachable from nowhere. Range-then-list is an ordinary Postgres shape.
    /// </summary>
    [Fact]
    public void A_sub_partition_is_reachable_through_its_own_parent()
    {
        var map = PartitionMap.Of(Snapshot(
            T(1, "event", null),
            T(2, "event_2026", 1),
            T(3, "event_2026_eu", 2),
            T(4, "event_2026_us", 2)));

        // Only the root is top-level…
        Assert.Equal(["event"], map.TopLevel(new[] { T(1, "event"), T(2, "event_2026", 1), T(3, "event_2026_eu", 2) })
            .Select(t => t.Name));
        // …and every level below it is reachable from the level above.
        Assert.Equal(["event_2026"], map.ChildrenOf(1).Select(t => t.Name));
        Assert.Equal(["event_2026_eu", "event_2026_us"], map.ChildrenOf(2).Select(t => t.Name));
    }

    [Fact]
    public void An_empty_map_answers_everything_with_nothing()
    {
        Assert.Empty(PartitionMap.Empty.ChildrenOf(1));
        Assert.False(PartitionMap.Empty.NestsUnderAKnownParent(T(1, "x", 2)));
    }

    /// <summary>
    /// The other half of the fix: indexed, not scanned. Asserted as a time bound rather than by counting
    /// comparisons, because the shape of the failure was quadratic — the previous
    /// <c>Tables.Any(p =&gt; p.Id == parent)</c> per partition — and this runs before the tree renders.
    /// </summary>
    [Fact]
    public void Indexing_a_large_hierarchy_is_not_quadratic()
    {
        const int count = 20_000;
        var tables = new List<TableInfo> { T(1, "event", null) };
        for (var i = 0; i < count; i++) tables.Add(T(i + 2, $"event_{i:00000}", 1));
        var snapshot = Snapshot(tables.ToArray());

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var map = PartitionMap.Of(snapshot);
        var top = map.TopLevel(snapshot.Tables).ToList();
        clock.Stop();

        Assert.Single(top);
        Assert.Equal(count, map.ChildrenOf(1).Count);
        // Generous by three orders of magnitude against the quadratic version (20k × 20k comparisons), so it
        // fails on a regression rather than on a slow machine.
        Assert.True(clock.ElapsedMilliseconds < 2000, $"took {clock.ElapsedMilliseconds} ms");
    }
}
