using System.Text.Json.Nodes;
using Bearing.Core.Schema;

namespace Bearing.Cli.Tools;

/// <summary>
/// The catalog as JSON — the same <see cref="ISchemaSnapshot"/> completion reads, so a caller sees the
/// database the editor sees and nothing had to be read twice.
/// <para>
/// Split across two tools rather than answered in one, and that is the design: a snapshot of a real
/// database is thousands of columns, and returning them all so a model can find one table would spend its
/// context on the other nine hundred. <see cref="Tables"/> is names; <see cref="Table"/> is one relation in
/// full.
/// </para>
/// </summary>
public static class SchemaJson
{
    /// <summary>How many relations one listing will name before it stops and says so.</summary>
    public const int MaxTables = 500;

    public static JsonObject Tables(ISchemaSnapshot snapshot, string? schema)
    {
        var matching = snapshot.Tables
            .Where(t => schema is null || string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Schema, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var tables = new JsonArray();
        foreach (var table in matching.Take(MaxTables))
        {
            tables.Add(new JsonObject
            {
                ["schema"] = table.Schema,
                ["name"] = table.Name,
                ["kind"] = Kind(table.Kind),
            });
        }

        var result = new JsonObject
        {
            ["schemas"] = new JsonArray(snapshot.Schemas.Select(s => (JsonNode)JsonValue.Create(s)).ToArray()),
            ["tables"] = tables,
            ["table_count"] = matching.Count,
        };

        // Said rather than implied: a listing silently cut at 500 reads exactly like a database with 500
        // relations, and a model would then conclude the one it is looking for does not exist.
        if (matching.Count > MaxTables)
            result["truncated"] = true;

        return result;
    }

    /// <summary>One relation: columns, and the foreign keys touching it from either side.</summary>
    public static JsonObject Table(ISchemaSnapshot snapshot, TableInfo table)
    {
        var columns = new JsonArray();
        foreach (var column in snapshot.ColumnsOf(table.Id).OrderBy(c => c.Ordinal))
        {
            columns.Add(new JsonObject
            {
                ["name"] = column.Name,
                ["type"] = column.DataType,
                ["not_null"] = column.NotNull,
                ["primary_key"] = column.IsPrimaryKey,
            });
        }

        var foreignKeys = new JsonArray();
        foreach (var fk in snapshot.ForeignKeysTouching(table.Id))
        {
            if (snapshot.TableById(fk.ParentTableId) is not { } parent) continue;
            if (snapshot.TableById(fk.ReferencedTableId) is not { } referenced) continue;

            foreignKeys.Add(new JsonObject
            {
                ["name"] = fk.Name,
                // Both ends are named in full, because this list carries the keys pointing *at* this table
                // as well as the ones it declares — "which tables reference payment" is the question a model
                // asks before it writes a join, and it cannot be answered from one side.
                ["from"] = Side(snapshot, parent, fk.ParentOrdinals),
                ["to"] = Side(snapshot, referenced, fk.ReferencedOrdinals),
            });
        }

        return new JsonObject
        {
            ["schema"] = table.Schema,
            ["name"] = table.Name,
            ["kind"] = Kind(table.Kind),
            ["columns"] = columns,
            ["foreign_keys"] = foreignKeys,
        };
    }

    private static JsonObject Side(ISchemaSnapshot snapshot, TableInfo table, IReadOnlyList<int> ordinals)
    {
        var byOrdinal = snapshot.ColumnsOf(table.Id).ToDictionary(c => c.Ordinal, c => c.Name);
        var names = new JsonArray();
        foreach (var ordinal in ordinals)
        {
            // An ordinal with no column is a snapshot that moved under us rather than a key with a gap;
            // naming the number is more use to a reader than dropping the entry silently.
            names.Add(byOrdinal.TryGetValue(ordinal, out var name)
                ? name
                : $"column {ordinal}");
        }

        return new JsonObject
        {
            ["table"] = $"{table.Schema}.{table.Name}",
            ["columns"] = names,
        };
    }

    private static string Kind(RelationKind kind) => kind switch
    {
        RelationKind.Table => "table",
        RelationKind.View => "view",
        RelationKind.MaterializedView => "materialized view",
        RelationKind.ForeignTable => "foreign table",
        RelationKind.Partitioned => "partitioned table",
        _ => "relation",
    };
}
