using Bearing.Cli.Tools;
using Bearing.Core.Explain;
using Xunit;

namespace Bearing.Cli.Tests;

/// <summary>
/// The plan a caller receives. <c>explain</c> exists because the question an agent asks about a slow query
/// is "which node cost the most", so the two things worth pinning are that the tree arrives whole and that
/// the hotspots point at the right nodes.
/// </summary>
public class PlanTreeTests
{
    private static ExplainNode Node(
        string type,
        string? relation = null,
        string? index = null,
        double? cost = null,
        double? actualMs = null,
        double? loops = null,
        double? blocks = null,
        params ExplainNode[] children)
        => new(type, relation, Alias: null, index, Filter: null,
            EstimatedCost: cost, EstimatedRows: null, ActualRows: null,
            ActualMs: actualMs, Loops: loops, SharedBlocksRead: blocks, Children: children);

    [Fact]
    public void The_whole_tree_arrives_with_its_children_in_order()
    {
        var plan = new ExplainPlan(
            Node("Hash Join", cost: 300,
                children: [Node("Seq Scan", relation: "payment", cost: 200), Node("Hash", cost: 60)]),
            PlanningMs: 1.5, ExecutionMs: null, Analyzed: false, RolledBack: false);

        var response = PlanTree.Of(plan);

        Assert.Equal("Hash Join", response.Plan.Node);
        Assert.Equal(["Seq Scan", "Hash"], response.Plan.Children!.Select(c => c.Node));
        Assert.Equal("payment", response.Plan.Children![0].Relation);
        Assert.Equal(1.5, response.PlanningMs);
        Assert.False(response.Analyzed);
    }

    /// <summary>A leaf carries no <c>children</c> key at all rather than an empty list — with
    /// <c>WhenWritingNull</c>, null is what keeps the key out of the JSON, and a plan is mostly leaves.</summary>
    [Fact]
    public void A_leaf_has_no_children_rather_than_an_empty_list()
    {
        var response = PlanTree.Of(new ExplainPlan(
            Node("Seq Scan", relation: "film"), null, null, Analyzed: false, RolledBack: false));

        Assert.Null(response.Plan.Children);
    }

    /// <summary>
    /// Measured, the worst node is the one that spent the most time <b>in itself</b>. Postgres reports time
    /// inclusive of children, so ordering by that would always name the root — which is the one node that
    /// tells you nothing.
    /// </summary>
    [Fact]
    public void Hotspots_are_by_self_time_when_the_plan_was_measured()
    {
        var slowChild = Node("Seq Scan", relation: "payment", actualMs: 90);
        var plan = new ExplainPlan(
            Node("Hash Join", actualMs: 100, children: [slowChild, Node("Hash", actualMs: 2)]),
            PlanningMs: null, ExecutionMs: 100, Analyzed: true, RolledBack: true);

        var response = PlanTree.Of(plan);

        // Root self time is 100 - 92 = 8, so the scan leads.
        Assert.Equal("Seq Scan", response.Hotspots[0].Node);
        Assert.Equal("payment", response.Hotspots[0].Relation);
        Assert.True(response.Analyzed);
        Assert.True(response.RolledBack);
    }

    /// <summary>Unmeasured there is no self time, so the ordering falls back to the planner's own cost —
    /// otherwise every node would tie at zero and the list would be in tree order wearing a different name.</summary>
    [Fact]
    public void Hotspots_fall_back_to_estimated_cost_when_it_was_only_planned()
    {
        var plan = new ExplainPlan(
            Node("Nested Loop", cost: 10, children: [Node("Seq Scan", relation: "film", cost: 800)]),
            null, null, Analyzed: false, RolledBack: false);

        var response = PlanTree.Of(plan);

        Assert.Equal("Seq Scan", response.Hotspots[0].Node);
    }

    /// <summary>Enough to point at the problem, not so many that it is the plan again in another order.</summary>
    [Fact]
    public void The_hotspot_list_is_capped()
    {
        var leaves = Enumerable.Range(1, 12).Select(i => Node($"Scan {i}", cost: i)).ToArray();
        var plan = new ExplainPlan(
            Node("Append", cost: 0, children: leaves), null, null, Analyzed: false, RolledBack: false);

        Assert.Equal(PlanTree.Hotspots, PlanTree.Of(plan).Hotspots.Count);
    }

    /// <summary>
    /// Two measurements are suppressed where they say nothing: one loop is what every node has unless it is
    /// on the inner side of a nested loop, and zero blocks read is the buffer cache doing its job. Carrying
    /// either everywhere is noise on the nodes it does not describe.
    /// </summary>
    [Fact]
    public void A_measurement_that_says_nothing_is_left_out()
    {
        var response = PlanTree.Of(new ExplainPlan(
            Node("Index Scan", index: "payment_pkey", loops: 1, blocks: 0),
            null, null, Analyzed: true, RolledBack: false));

        Assert.Null(response.Plan.Loops);
        Assert.Null(response.Plan.BlocksRead);
        Assert.Equal("payment_pkey", response.Plan.Index);
    }

    [Fact]
    public void A_measurement_that_says_something_is_kept()
    {
        var response = PlanTree.Of(new ExplainPlan(
            Node("Index Scan", loops: 1_000, blocks: 42),
            null, null, Analyzed: true, RolledBack: false));

        Assert.Equal(1_000, response.Plan.Loops);
        Assert.Equal(42, response.Plan.BlocksRead);
    }

    /// <summary>A plan that was not analysed has no actual rows, which is a different thing from having
    /// none — so the measurement is absent rather than zero.</summary>
    [Fact]
    public void An_unmeasured_plan_reports_no_actuals_rather_than_zeroes()
    {
        var response = PlanTree.Of(new ExplainPlan(
            Node("Seq Scan", relation: "film", cost: 62), null, null, Analyzed: false, RolledBack: false));

        Assert.Null(response.Plan.ActualRows);
        Assert.Null(response.Plan.ActualMs);
        Assert.Null(response.Plan.SelfMs);
        Assert.Null(response.ExecutionMs);
        Assert.Equal(62, response.Plan.EstimatedCost);
    }

    /// <summary>A blank string from the plan JSON is an absent value, not a value that is blank — and an
    /// empty key in the output would read as "this node has no relation name", which is a claim.</summary>
    [Fact]
    public void A_blank_field_is_absent_rather_than_empty()
    {
        var response = PlanTree.Of(new ExplainPlan(
            new ExplainNode("Result", Relation: "", Alias: "  ", IndexName: "", Filter: null,
                null, null, null, null, null, null, []),
            null, null, Analyzed: false, RolledBack: false));

        Assert.Null(response.Plan.Relation);
        Assert.Null(response.Plan.Alias);
        Assert.Null(response.Plan.Index);
    }
}
