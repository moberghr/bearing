using System;
using System.Collections.Generic;
using System.Text.Json;
using Bearing.Core.Explain;

namespace Bearing.Sql;

/// <summary>
/// Turns the JSON of <c>EXPLAIN (FORMAT JSON)</c> into an <see cref="ExplainPlan"/>.
/// <para>
/// Postgres wraps the whole thing in a one-element array whose object holds <c>"Plan"</c> plus the totals, and
/// each node holds its inputs under <c>"Plans"</c>. Every field beyond <c>Node Type</c> is optional: a plain
/// EXPLAIN has no timings, BUFFERS figures appear only when asked for, and the set has grown across versions.
/// So this reads what it recognises and ignores the rest rather than mapping a fixed schema — a new
/// Postgres release must not turn a plan into an error.
/// </para>
/// </summary>
public static class ExplainPlanParser
{
    /// <summary>
    /// Parse <paramref name="json"/>, or return null when it is not a plan document.
    /// <para>
    /// Null rather than throwing, because the caller has a string from a database and no way to know what
    /// went wrong: a failed EXPLAIN, a permission error rendered as text, an empty result. The UI can say
    /// "that did not come back as a plan" and show the raw text, which is more use than an exception.
    /// </para>
    /// </summary>
    public static ExplainPlan? Parse(string? json, bool analyzed = false, bool rolledBack = false)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // Postgres emits [ { "Plan": {...}, "Planning Time": .., "Execution Time": .. } ].
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0) return null;
                root = root[0];
            }
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("Plan", out var plan) || plan.ValueKind != JsonValueKind.Object) return null;

            return new ExplainPlan(
                Node(plan),
                Number(root, "Planning Time"),
                Number(root, "Execution Time"),
                analyzed,
                rolledBack);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ExplainNode Node(JsonElement element)
    {
        var children = new List<ExplainNode>();
        if (element.TryGetProperty("Plans", out var plans) && plans.ValueKind == JsonValueKind.Array)
            foreach (var child in plans.EnumerateArray())
                if (child.ValueKind == JsonValueKind.Object)
                    children.Add(Node(child));

        return new ExplainNode(
            Text(element, "Node Type") ?? "(unknown)",
            Text(element, "Relation Name"),
            Text(element, "Alias"),
            Text(element, "Index Name"),
            // "Filter" is the residual condition; "Index Cond" is what the index itself did. Both are worth
            // showing, and the filter is the one that explains a scan reading far more rows than it returned.
            Text(element, "Filter") ?? Text(element, "Index Cond") ?? Text(element, "Recheck Cond"),
            Number(element, "Total Cost"),
            Number(element, "Plan Rows"),
            Number(element, "Actual Rows"),
            Number(element, "Actual Total Time"),
            Number(element, "Actual Loops"),
            Number(element, "Shared Read Blocks"),
            children);
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// A numeric field, or null when absent.
    /// <para>
    /// Tolerant of a number arriving as a string: Postgres emits these as JSON numbers, but the same shape
    /// travels through tools that quote everything, and a quoted cost is still a cost.
    /// </para>
    /// </summary>
    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => double.TryParse(value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : null,
            _ => null,
        };
    }
}
