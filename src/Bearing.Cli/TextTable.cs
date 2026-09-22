using System.Globalization;
using System.Text;
using System.Text.Json;
using Bearing.Cli.Tools;

namespace Bearing.Cli;

/// <summary>
/// The <c>--table</c> rendering: the same responses, laid out for a person.
/// <para>
/// It switches on the response <i>type</i>, so a command that gains a shape is a compile error here rather
/// than a silently unhandled case. It used to read the JSON, which meant the layout depended on key names
/// and a renamed field degraded quietly to the fallback.
/// </para>
/// <para>
/// A null prints as an empty cell. That is ambiguous against an empty string, and it is why this format is
/// documented as unstable and JSON is the default: only the JSON tells those two apart.
/// </para>
/// </summary>
public static class TextTable
{
    public static string Render(ICliResponse response)
    {
        var output = new StringBuilder();

        switch (response)
        {
            case ConnectionsResponse connections:
                Records(output, connections.Connections);
                break;

            case TablesResponse tables:
                Records(output, tables.Tables);
                if (tables.Truncated == true)
                    output.AppendLine($"({tables.TableCount} in all; this is the first {tables.Tables.Count})");
                break;

            case TableResponse table:
                AppendTable(output, table);
                break;

            case QueryResponse query:
                foreach (var set in query.Results) AppendSet(output, set);
                break;

            case ExportResponse export:
                output.AppendLine(
                    $"Wrote {export.Rows:N0} row{(export.Rows == 1 ? "" : "s")} "
                    + $"in {export.Results} result set{(export.Results == 1 ? "" : "s")} "
                    + $"to {export.Written} ({export.Format}).");
                break;

            case PlanResponse plan:
                AppendPlan(output, plan);
                break;

            default:
                output.AppendLine(JsonSerializer.Serialize(response, CliRunner.Json));
                break;
        }

        return output.ToString();
    }

    private static void AppendTable(StringBuilder output, TableResponse table)
    {
        output.AppendLine($"{table.Schema}.{table.Name}  ({table.Kind})");
        output.AppendLine();
        Records(output, table.Columns);

        if (table.ForeignKeys.Count == 0) return;

        output.AppendLine();
        output.AppendLine("Foreign keys");
        foreach (var key in table.ForeignKeys)
            output.AppendLine(
                $"  {key.Name}: {key.From.Table}({string.Join(", ", key.From.Columns)})"
                + $" -> {key.To.Table}({string.Join(", ", key.To.Columns)})");
    }

    private static void AppendSet(StringBuilder output, ResultSet set)
    {
        if (set.Columns.Count > 0)
        {
            var headers = set.Columns.Select(c => c.Name).ToList();
            var rows = set.Rows.Select(r => r.Select(Cell).ToList()).ToList();
            Grid(output, headers, rows);
        }

        if (set.Message is { Length: > 0 } message) output.AppendLine(message);

        // "cut short" rather than "more rows exist": the read stopped at the ceiling, and saying so is what
        // stops someone reading a partial answer as the whole one.
        var truncated = set.Truncated ? ", cut short at the row limit" : "";
        output.AppendLine(
            $"({set.RowCount} row{(set.RowCount == 1 ? "" : "s")} in {set.DurationMs} ms{truncated})");
    }

    private static void AppendPlan(StringBuilder output, PlanResponse plan)
    {
        output.AppendLine(plan.Analyzed
            ? $"Measured plan (rolled back: {(plan.RolledBack ? "yes" : "no")})"
            : "Estimated plan — the statement was not run");
        if (plan.PlanningMs is { } planning) output.AppendLine($"Planning: {planning} ms");
        if (plan.ExecutionMs is { } execution) output.AppendLine($"Execution: {execution} ms");
        output.AppendLine();

        AppendNode(output, plan.Plan, 0);

        if (plan.Hotspots.Count == 0) return;
        output.AppendLine();
        output.AppendLine("Costliest nodes");
        foreach (var node in plan.Hotspots) output.AppendLine($"  {Describe(node)}");
    }

    private static void AppendNode(StringBuilder output, PlanNode node, int depth)
    {
        output.AppendLine(new string(' ', depth * 2) + Describe(node));
        foreach (var child in node.Children ?? []) AppendNode(output, child, depth + 1);
    }

    private static string Describe(PlanNode node)
    {
        var text = new StringBuilder(node.Node);
        if (node.Relation is { Length: > 0 }) text.Append(" on ").Append(node.Relation);
        if (node.Index is { Length: > 0 }) text.Append(" using ").Append(node.Index);
        if (node.ActualMs is { } ms) text.Append(CultureInfo.InvariantCulture, $"  {ms} ms");
        else if (node.EstimatedCost is { } cost) text.Append(CultureInfo.InvariantCulture, $"  cost {cost}");
        if (node.ActualRows is { } rows) text.Append(CultureInfo.InvariantCulture, $"  {rows} rows");
        return text.ToString();
    }

    /// <summary>A list of records as a grid, with a column per property that any of them sets.</summary>
    private static void Records<T>(StringBuilder output, IReadOnlyList<T> records)
    {
        if (records.Count == 0)
        {
            output.AppendLine("(none)");
            return;
        }

        var properties = typeof(T).GetProperties();
        var values = records
            .Select(r => properties.Select(p => Cell(p.GetValue(r))).ToList())
            .ToList();

        // A property no row sets gets no column: an optional field — an environment, an unavailable reason —
        // is present on some rows and absent on others, and a column of blanks is noise on the rest.
        var used = Enumerable.Range(0, properties.Length)
            .Where(i => values.Any(row => row[i].Length > 0))
            .ToList();

        Grid(
            output,
            used.Select(i => Header(properties[i].Name)).ToList(),
            values.Select(row => used.Select(i => row[i]).ToList()).ToList());
    }

    /// <summary>A property name as a column heading, in the same spelling the JSON uses — so the two views
    /// of one response name the same field the same way.</summary>
    private static string Header(string property)
        => JsonNamingPolicy.SnakeCaseLower.ConvertName(property);

    private static void Grid(StringBuilder output, List<string> headers, List<List<string>> rows)
    {
        var widths = headers.Select(h => h.Length).ToList();
        foreach (var row in rows)
            for (var i = 0; i < row.Count && i < widths.Count; i++)
                widths[i] = Math.Max(widths[i], row[i].Length);

        output.AppendLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        output.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
            output.AppendLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
    }

    private static string Cell(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        IEnumerable<string> many => string.Join(", ", many),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}
