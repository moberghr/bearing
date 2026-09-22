using Bearing.Core.Schema;

namespace Bearing.Cli.Tools;

/// <summary>
/// The catalog as records — from the same <see cref="ISchemaSnapshot"/> completion reads, so a caller sees
/// the database the editor sees and nothing had to be read twice.
/// <para>
/// Split across two commands rather than answered in one, and that is the design: a snapshot of a real
/// database is thousands of columns, and returning them all so a model can find one table would spend its
/// context on the other nine hundred. <see cref="Tables"/> is names; <see cref="Table"/> is one relation in
/// full.
/// </para>
/// </summary>
public static class SchemaCatalog
{
    /// <summary>How many relations one listing will name before it stops and says so.</summary>
    public const int MaxTables = 500;

    public static TablesResponse Tables(ISchemaSnapshot snapshot, string? schema)
    {
        var matching = snapshot.Tables
            .Where(t => schema is null || string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Schema, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        return new TablesResponse(
            snapshot.Schemas,
            matching.Take(MaxTables).Select(t => new RelationSummary(t.Schema, t.Name, Kind(t.Kind))).ToList(),
            matching.Count)
        {
            // Said rather than implied: a listing silently cut at the cap reads exactly like a database
            // with that many relations, and a model would then conclude the one it wants does not exist.
            Truncated = matching.Count > MaxTables ? true : null,
        };
    }

    /// <summary>One relation: columns, and the foreign keys touching it from either side.</summary>
    public static TableResponse Table(ISchemaSnapshot snapshot, TableInfo table)
    {
        var columns = snapshot.ColumnsOf(table.Id)
            .OrderBy(c => c.Ordinal)
            .Select(c => new ColumnSummary(c.Name, c.DataType, c.NotNull, c.IsPrimaryKey))
            .ToList();

        var keys = new List<ForeignKeySummary>();
        foreach (var fk in snapshot.ForeignKeysTouching(table.Id))
        {
            if (snapshot.TableById(fk.ParentTableId) is not { } parent) continue;
            if (snapshot.TableById(fk.ReferencedTableId) is not { } referenced) continue;

            // Both ends in full, because this list carries the keys pointing *at* this table as well as the
            // ones it declares — "which tables reference payment" is the question asked before writing a
            // join, and it cannot be answered from one side.
            keys.Add(new ForeignKeySummary(
                fk.Name,
                Side(snapshot, parent, fk.ParentOrdinals),
                Side(snapshot, referenced, fk.ReferencedOrdinals)));
        }

        return new TableResponse(table.Schema, table.Name, Kind(table.Kind), columns, keys);
    }

    private static KeySide Side(ISchemaSnapshot snapshot, TableInfo table, IReadOnlyList<int> ordinals)
    {
        var byOrdinal = snapshot.ColumnsOf(table.Id).ToDictionary(c => c.Ordinal, c => c.Name);

        // An ordinal with no column is a snapshot that moved under us rather than a key with a gap; naming
        // the number is more use to a reader than dropping the entry silently.
        var names = ordinals
            .Select(o => byOrdinal.TryGetValue(o, out var name) ? name : $"column {o}")
            .ToList();

        return new KeySide($"{table.Schema}.{table.Name}", names);
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
