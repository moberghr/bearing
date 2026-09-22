using Bearing.Core.Explain;

namespace Bearing.Cli.Tools;

/// <summary>
/// A query plan as a tree of records rather than indented text.
/// <para>
/// The text form would have to be re-parsed out of its indentation by whoever read it, which is exactly
/// what <c>ExplainSql</c> avoids by asking Postgres for JSON in the first place. A caller here is usually a
/// program deciding <i>why</i> a query is slow, and the question it asks is "which node cost the most".
/// </para>
/// </summary>
public static class PlanTree
{
    /// <summary>How many hotspots are named. The plan is there in full, so this is a shortcut rather than a
    /// summary: enough to point at the problem, not so many that it is the plan again in another order.</summary>
    public const int Hotspots = 5;

    public static PlanResponse Of(ExplainPlan plan) =>
        new(plan.Analyzed, plan.RolledBack, Node(plan.Root))
        {
            PlanningMs = plan.PlanningMs,
            ExecutionMs = plan.ExecutionMs,
            // By self time when it was measured and by estimated cost when it was not — ExplainPlan.Hotspots
            // already makes that choice, and making it twice is how the two would disagree.
            Hotspots = plan.Hotspots().Take(Hotspots).Select(Summary).ToList(),
        };

    private static PlanNode Node(ExplainNode node) => Summary(node) with
    {
        Children = node.Children.Count == 0
            ? null
            : node.Children.Select(Node).ToList(),
    };

    /// <summary>One node without its children. Every measurement stays null when it is absent, so a plan
    /// that was not analysed carries no actual rows — which is a different thing from having none.</summary>
    private static PlanNode Summary(ExplainNode node) => new(node.NodeType)
    {
        Relation = Trimmed(node.Relation),
        Alias = Trimmed(node.Alias),
        Index = Trimmed(node.IndexName),
        Filter = Trimmed(node.Filter),
        EstimatedCost = node.EstimatedCost,
        EstimatedRows = node.EstimatedRows,
        ActualRows = node.ActualRows,
        ActualMs = node.ActualMs,
        SelfMs = node.SelfMs,
        // A loop count of one is what every node has unless it is on the inner side of a nested loop, so
        // carrying it everywhere would be noise on the nodes it says nothing about.
        Loops = node.Loops is { } loops && loops != 1 ? loops : null,
        BlocksRead = node.SharedBlocksRead is { } blocks && blocks > 0 ? blocks : null,
    };

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
