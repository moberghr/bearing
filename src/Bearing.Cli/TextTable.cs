using System.Text;
using System.Text.Json.Nodes;

namespace Bearing.Cli;

/// <summary>
/// The <c>--table</c> rendering: the same JSON, laid out for a person.
/// <para>
/// It reads the JSON rather than the result objects on purpose. There is one shape of the answer and it is
/// the JSON one; a second renderer walking the records would be a second definition of what a command
/// returns, and the two would drift — the grid's own display path is the cautionary tale (§9.10c). This is
/// a view of the output, never a second output.
/// </para>
/// <para>
/// A null prints as an empty cell. That is ambiguous against an empty string, and it is why this format is
/// documented as unstable and JSON is the default: only the JSON tells those two apart.
/// </para>
/// </summary>
public static class TextTable
{
    public static string Render(JsonNode node)
    {
        var output = new StringBuilder();

        switch (node)
        {
            case JsonObject o when o["connections"] is JsonArray connections:
                AppendRecords(output, connections);
                break;

            case JsonObject o when o["tables"] is JsonArray tables:
                AppendRecords(output, tables);
                if (o["truncated"]?.GetValue<bool>() == true)
                    output.AppendLine($"({o["table_count"]} in all; this is the first {((JsonArray)o["tables"]!).Count})");
                break;

            case JsonObject o when o["columns"] is JsonArray columns && o["foreign_keys"] is JsonArray keys:
                output.AppendLine($"{o["schema"]}.{o["name"]}  ({o["kind"]})");
                output.AppendLine();
                AppendRecords(output, columns);
                if (keys.Count > 0)
                {
                    output.AppendLine();
                    output.AppendLine("Foreign keys");
                    foreach (var key in keys)
                        output.AppendLine(
                            $"  {key!["name"]}: {key["from"]!["table"]}({Join(key["from"]!["columns"]!)})"
                            + $" -> {key["to"]!["table"]}({Join(key["to"]!["columns"]!)})");
                }

                break;

            case JsonObject o when o["results"] is JsonArray results:
                foreach (var set in results) AppendResultSet(output, (JsonObject)set!);
                break;

            default:
                output.AppendLine(node.ToJsonString());
                break;
        }

        return output.ToString();
    }

    private static void AppendResultSet(StringBuilder output, JsonObject set)
    {
        if (set["columns"] is JsonArray columns && columns.Count > 0)
        {
            var headers = columns.Select(c => c!["name"]!.GetValue<string>()).ToList();
            var rows = ((JsonArray)set["rows"]!)
                .Select(r => ((JsonArray)r!).Select(Cell).ToList())
                .ToList();
            AppendGrid(output, headers, rows);
        }

        if (set["message"]?.GetValue<string>() is { Length: > 0 } message) output.AppendLine(message);

        var count = set["row_count"]!.GetValue<int>();
        // "cut short" rather than "more rows exist": the read stopped at the ceiling, and saying so is what
        // stops someone reading a partial answer as the whole one.
        var truncated = set["truncated"]?.GetValue<bool>() == true ? ", cut short at the row limit" : "";
        output.AppendLine($"({count} row{(count == 1 ? "" : "s")} in {set["duration_ms"]} ms{truncated})");
    }

    private static void AppendRecords(StringBuilder output, JsonArray records)
    {
        if (records.Count == 0)
        {
            output.AppendLine("(none)");
            return;
        }

        // The union of keys, in first-seen order: an optional field (environment, unavailable_reason) is
        // present on some rows and not others, and a header taken from row one alone would drop it.
        var headers = new List<string>();
        foreach (var record in records)
            foreach (var property in record!.AsObject())
                if (!headers.Contains(property.Key))
                    headers.Add(property.Key);

        var rows = records
            .Select(r => headers.Select(h => Cell(r!.AsObject().TryGetPropertyValue(h, out var v) ? v : null)).ToList())
            .ToList();

        AppendGrid(output, headers, rows);
    }

    private static void AppendGrid(StringBuilder output, List<string> headers, List<List<string>> rows)
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

    private static string Cell(JsonNode? value) => value switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value.ToJsonString(),
    };

    private static string Join(JsonNode columns)
        => string.Join(", ", ((JsonArray)columns).Select(c => c!.GetValue<string>()));
}
