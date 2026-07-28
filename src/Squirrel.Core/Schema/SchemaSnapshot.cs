namespace Squirrel.Core.Schema;

/// <summary>
/// Immutable, indexed implementation of <see cref="ISchemaSnapshot"/>. Built once from catalog
/// reads (or hand-built in tests) and then queried cheaply on every keystroke.
/// </summary>
public sealed class SchemaSnapshot : ISchemaSnapshot
{
    private readonly Dictionary<long, List<ColumnInfo>> _columnsByTable;
    private readonly Dictionary<long, List<ForeignKeyInfo>> _fksByTable;
    // (schema, lower-name) and (lower-name) lookups
    private readonly Dictionary<(string schema, string name), TableInfo> _bySchemaName;
    private readonly Dictionary<string, List<TableInfo>> _byName;

    public string Database { get; }
    public IReadOnlyList<string> Schemas { get; }
    public IReadOnlyList<TableInfo> Tables { get; }

    public SchemaSnapshot(
        string database,
        IReadOnlyList<string> schemas,
        IReadOnlyList<TableInfo> tables,
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<ForeignKeyInfo> foreignKeys)
    {
        Database = database;
        Schemas = schemas;
        Tables = tables;

        _columnsByTable = columns
            .GroupBy(c => c.TableId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Ordinal).ToList());

        _fksByTable = new Dictionary<long, List<ForeignKeyInfo>>();
        foreach (var fk in foreignKeys)
        {
            Add(_fksByTable, fk.ParentTableId, fk);
            if (fk.ReferencedTableId != fk.ParentTableId)
                Add(_fksByTable, fk.ReferencedTableId, fk);
        }

        _bySchemaName = tables.ToDictionary(t => (t.Schema.ToLowerInvariant(), t.Name.ToLowerInvariant()), t => t);
        _byName = tables
            .GroupBy(t => t.Name.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        static void Add(Dictionary<long, List<ForeignKeyInfo>> map, long key, ForeignKeyInfo fk)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<ForeignKeyInfo>();
            list.Add(fk);
        }
    }

    public IReadOnlyList<ColumnInfo> ColumnsOf(long tableId)
        => _columnsByTable.TryGetValue(tableId, out var c) ? c : Array.Empty<ColumnInfo>();

    public TableInfo? ResolveTable(string? schema, string name)
    {
        var n = name.ToLowerInvariant();
        if (schema is not null)
            return _bySchemaName.TryGetValue((schema.ToLowerInvariant(), n), out var t) ? t : null;

        if (!_byName.TryGetValue(n, out var candidates) || candidates.Count == 0)
            return null;
        if (candidates.Count == 1)
            return candidates[0];

        // Ambiguous bare name: prefer the earliest schema in search_path order.
        foreach (var s in Schemas)
        {
            var hit = candidates.FirstOrDefault(t => string.Equals(t.Schema, s, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return candidates[0];
    }

    public IReadOnlyList<ForeignKeyInfo> ForeignKeysTouching(long tableId)
        => _fksByTable.TryGetValue(tableId, out var f) ? f : Array.Empty<ForeignKeyInfo>();
}
