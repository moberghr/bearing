using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bearing.Cli.Tools;

namespace Bearing.Cli;

/// <summary>
/// The <c>--table</c> and <c>--tsv</c> renderings: the same responses, laid out for a reader rather than a
/// parser. One layout, two ways of separating cells — padding for a person's eye, tabs for a model's
/// context — so the two cannot drift into saying different things about one response.
/// <para>
/// It switches on the response <i>type</i>, so a command that gains a shape is a compile error here rather
/// than a silently unhandled case. It used to read the JSON, which meant the layout depended on key names
/// and a renamed field degraded quietly to the fallback.
/// </para>
/// <para>
/// Under <c>--table</c> a null prints as an empty cell, which is ambiguous against an empty string. Under
/// <c>--tsv</c> it is <c>\N</c> and every cell is escaped (<see cref="Escape"/>), so there it is not.
/// </para>
/// </summary>
public static class TextTable
{
    /// <param name="tabs">Tab-separated and escaped (<c>--tsv</c>) rather than padded (<c>--table</c>).</param>
    public static string Render(ICliResponse response, bool tabs = false)
    {
        var output = new StringBuilder();

        switch (response)
        {
            case ConnectionsResponse connections:
                if (connections.Project is { } project)
                    output.AppendLine($"project: {project.Name} ({project.Directory})");
                Records(output, connections.Connections, tabs);
                // Said even when the list above is "(none)": that is exactly when a caller most needs to know
                // whether the connection it wanted is absent or merely unexposed.
                if (connections.NotExposed.Count > 0)
                    output.AppendLine($"not exposed: {string.Join(", ", connections.NotExposed)}");
                break;

            case TablesResponse tables:
                Records(output, tables.Tables, tabs);
                if (tables.Truncated == true)
                    output.AppendLine($"({tables.TableCount} in all; this is the first {tables.Tables.Count})");
                break;

            case TableResponse table:
                AppendTable(output, table, tabs);
                break;

            case QueryResponse query:
                // A blank line between sets, so where one statement's rows end and the next one's header
                // begins is not left to the reader to infer from a row that happens to look like a header.
                for (var i = 0; i < query.Results.Count; i++)
                {
                    if (i > 0) output.AppendLine();
                    AppendSet(output, query.Results[i], tabs);
                }
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

    private static void AppendTable(StringBuilder output, TableResponse table, bool tabs)
    {
        output.AppendLine($"{table.Schema}.{table.Name}  ({table.Kind})");
        output.AppendLine();
        Records(output, table.Columns, tabs);

        if (table.ForeignKeys.Count == 0) return;

        output.AppendLine();
        output.AppendLine("Foreign keys");
        foreach (var key in table.ForeignKeys)
            output.AppendLine(
                $"  {key.Name}: {key.From.Table}({string.Join(", ", key.From.Columns)})"
                + $" -> {key.To.Table}({string.Join(", ", key.To.Columns)})");
    }

    private static void AppendSet(StringBuilder output, ResultSet set, bool tabs)
    {
        if (set.Columns.Count > 0)
        {
            var headers = set.Columns.Select(c => c.Name).ToList();
            var rows = set.Rows.Select(r => r.Select(Cell).ToList()).ToList();
            Grid(output, headers, rows, tabs);
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
    private static void Records<T>(StringBuilder output, IReadOnlyList<T> records, bool tabs)
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
            .Where(i => values.Any(row => row[i] is { Length: > 0 }))
            .ToList();

        Grid(
            output,
            used.Select(i => Header(properties[i].Name)).ToList(),
            values.Select(row => used.Select(i => row[i]).ToList()).ToList(),
            tabs);
    }

    /// <summary>A property name as a column heading, in the same spelling the JSON uses — so the two views
    /// of one response name the same field the same way.</summary>
    private static string Header(string property)
        => JsonNamingPolicy.SnakeCaseLower.ConvertName(property);

    /// <summary>Rows under a header. A null cell is <c>null</c> here rather than <c>""</c>, so the tab
    /// layout can still tell the two apart.</summary>
    private static void Grid(StringBuilder output, List<string> headers, List<List<string?>> rows, bool tabs)
    {
        if (tabs)
        {
            // No rule under the header and no trimming: a trailing empty cell is a trailing tab, and
            // trimming it would change how many columns the row has.
            output.AppendLine(string.Join('\t', headers.Select(Escape)));
            foreach (var row in rows)
                output.AppendLine(string.Join('\t', row.Select(c => c is null ? Null : Escape(c))));
            return;
        }

        var widths = headers.Select(h => h.Length).ToList();
        foreach (var row in rows)
            for (var i = 0; i < row.Count && i < widths.Count; i++)
                widths[i] = Math.Max(widths[i], (row[i] ?? "").Length);

        output.AppendLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        output.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
            output.AppendLine(string.Join("  ", row.Select((c, i) => (c ?? "").PadRight(widths[i]))).TrimEnd());
    }

    /// <summary>A null under <c>--tsv</c>: Postgres' COPY text spelling, which no escaped value can produce —
    /// a literal backslash-N in the data comes out as <c>\\N</c>.</summary>
    public const string Null = @"\N";

    /// <summary>
    /// A cell that cannot break the line or the row it is on. The backslash is escaped first, so every
    /// escape here is unambiguous and a reader undoes them in one left-to-right pass.
    /// </summary>
    public static string Escape(string value) => value
        .Replace(@"\", @"\\")
        .Replace("\t", @"\t")
        .Replace("\n", @"\n")
        .Replace("\r", @"\r");

    private static string? Cell(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        // A record's own list — a key's columns, the schemas listed — which is always plain names.
        IEnumerable<string> many => string.Join(", ", many),
        // A cell's array or hstore, as CellValue shaped it for the JSON: a List<object?> and a dictionary.
        // Neither is IConvertible nor IFormattable, so the fallback below printed the .NET type name in
        // every row — §1.11e's "System.String[]" again, here in the rendering an agent is told to read.
        // Compact JSON because it nests, keeps a null element null, and has no separator to confuse.
        IDictionary or IEnumerable => JsonSerializer.Serialize(value, value.GetType(), CompactJson),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static readonly JsonSerializerOptions CompactJson = new(CliRunner.Json) { WriteIndented = false };
}
