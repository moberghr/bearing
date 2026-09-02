using System.Collections.Generic;
using System.Linq;

namespace Bearing.Core.Explain;

/// <summary>
/// One node of a Postgres query plan, as reported by <c>EXPLAIN (FORMAT JSON)</c>.
/// <para>
/// A record tree rather than the raw JSON, so everything above it — the view, the "which node cost the
/// most" arithmetic, the tests — works against names instead of dictionary lookups. Fields Postgres omits
/// are null: a plain <c>EXPLAIN</c> has no timings at all, and <c>BUFFERS</c> figures are absent unless it
/// was asked for.
/// </para>
/// </summary>
/// <param name="NodeType">Postgres' own label — "Seq Scan", "Hash Join", "Index Only Scan".</param>
/// <param name="Relation">The table this node reads, where it reads one.</param>
/// <param name="Alias">Its alias in the query, when it differs from the relation.</param>
/// <param name="IndexName">The index used, for the scan kinds that use one.</param>
/// <param name="Filter">A residual filter applied after the scan — the classic source of a bad plan.</param>
/// <param name="EstimatedCost">Postgres' own cost estimate for this node and everything under it.</param>
/// <param name="EstimatedRows">Rows the planner expected.</param>
/// <param name="ActualRows">Rows there actually were. Null without ANALYZE.</param>
/// <param name="ActualMs">Wall time for this node and its children, in milliseconds. Null without ANALYZE.</param>
/// <param name="Loops">How many times the node ran; actuals are per loop, so totals multiply by this.</param>
/// <param name="SharedBlocksRead">Blocks read from disk rather than the buffer cache. Null without BUFFERS.</param>
/// <param name="Children">Input nodes, in Postgres' order.</param>
public sealed record ExplainNode(
    string NodeType,
    string? Relation,
    string? Alias,
    string? IndexName,
    string? Filter,
    double? EstimatedCost,
    double? EstimatedRows,
    double? ActualRows,
    double? ActualMs,
    double? Loops,
    double? SharedBlocksRead,
    IReadOnlyList<ExplainNode> Children)
{
    /// <summary>This node and every node beneath it, depth first in Postgres' own child order.</summary>
    public IEnumerable<ExplainNode> Flatten()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.Flatten())
                yield return node;
    }

    /// <summary>
    /// Time spent in this node alone — its own total less its children's.
    /// <para>
    /// The figure Postgres reports is inclusive, so the root always looks like the most expensive node and a
    /// list ordered by it is useless. Self time is what actually points at the problem. Null when the plan
    /// was not analysed, and floored at zero: children run per loop and the arithmetic can land slightly
    /// negative on a parallel plan, which is noise rather than a negative duration.
    /// </para>
    /// </summary>
    public double? SelfMs
    {
        get
        {
            if (ActualMs is not { } total) return null;
            var children = Children.Sum(c => c.ActualMs ?? 0);
            var self = total - children;
            return self < 0 ? 0 : self;
        }
    }

    /// <summary>
    /// How far the planner's row estimate was out, as a factor ≥ 1 — the single most useful number in a
    /// plan, because a stale estimate is what produces the wrong join order.
    /// <para>
    /// Compared against actuals multiplied by <see cref="Loops"/>, since Postgres reports actual rows per
    /// loop and the estimate for the whole node. Null without ANALYZE, and null where either side is zero:
    /// "infinitely wrong" is not a ratio worth showing.
    /// </para>
    /// </summary>
    public double? EstimateErrorFactor
    {
        get
        {
            if (ActualRows is not { } actual || EstimatedRows is not { } estimated) return null;
            var total = actual * (Loops ?? 1);
            if (total <= 0 || estimated <= 0) return null;
            return total > estimated ? total / estimated : estimated / total;
        }
    }

    /// <summary>A one-line label: the node type, and the relation or index it works on.</summary>
    public string Title => (Relation, IndexName) switch
    {
        (not null, not null) => $"{NodeType} on {Relation} using {IndexName}",
        (not null, null) => $"{NodeType} on {Relation}",
        (null, not null) => $"{NodeType} using {IndexName}",
        _ => NodeType,
    };
}

/// <summary>
/// A whole plan: its root node and the totals Postgres reports beside it.
/// </summary>
/// <param name="Root">The outermost node.</param>
/// <param name="PlanningMs">Time spent planning. Reported by Postgres 13+; null when absent.</param>
/// <param name="ExecutionMs">Time spent executing. Present only with ANALYZE.</param>
/// <param name="Analyzed">Whether the statement was actually run (ANALYZE) or only planned.</param>
/// <param name="RolledBack">Whether it ran inside a transaction that was rolled back afterwards.</param>
public sealed record ExplainPlan(
    ExplainNode Root,
    double? PlanningMs,
    double? ExecutionMs,
    bool Analyzed,
    bool RolledBack)
{
    /// <summary>Every node, worst self-time first — the order someone reading a slow plan wants. Falls back
    /// to estimated cost when the plan was not analysed, since self time does not exist then.</summary>
    public IReadOnlyList<ExplainNode> Hotspots()
        => Root.Flatten()
            .OrderByDescending(n => n.SelfMs ?? n.EstimatedCost ?? 0)
            .ToList();
}
