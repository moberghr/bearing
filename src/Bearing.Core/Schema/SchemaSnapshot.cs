namespace Bearing.Core.Schema;

/// <summary>
/// Immutable, indexed implementation of <see cref="ISchemaSnapshot"/>. Built once from catalog
/// reads (or hand-built in tests) and then queried cheaply on every keystroke.
/// </summary>
public sealed class SchemaSnapshot : ISchemaSnapshot
{
    private readonly Dictionary<long, List<ColumnInfo>> _columnsByTable;
    private readonly Dictionary<long, List<ForeignKeyInfo>> _fksByTable;
    // Case-folded (schema, name) and (name) lookups. Both map to a *list*: Postgres lets one schema
    // hold two relations differing only by case (quoted identifiers reach them), so folding the key can
    // collide. A plain dictionary threw while *building* the snapshot, taking completion, the schema
    // browser and editability resolution down for the whole database.
    private readonly Dictionary<(string schema, string name), List<TableInfo>> _bySchemaName;
    private readonly Dictionary<string, List<TableInfo>> _byName;

    public string Database { get; }
    public IReadOnlyList<string> Schemas { get; }
    public IReadOnlyList<string> SearchPath { get; }
    public IReadOnlyList<TableInfo> Tables { get; }

    public SchemaSnapshot(
        string database,
        IReadOnlyList<string> schemas,
        IReadOnlyList<TableInfo> tables,
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<ForeignKeyInfo> foreignKeys,
        IReadOnlyList<string>? searchPath = null)
    {
        Database = database;
        Schemas = schemas;
        // No search_path given (hand-built snapshots): treat every listed schema as reachable, which is
        // the pre-existing assumption and keeps bare names unqualified.
        SearchPath = searchPath ?? schemas;
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

        _bySchemaName = tables
            .GroupBy(t => (t.Schema.ToLowerInvariant(), t.Name.ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => g.ToList());
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
        {
            if (!_bySchemaName.TryGetValue((schema.ToLowerInvariant(), n), out var inSchema))
                return null;
            // The caller's spelling wins when it names a relation exactly: `"Users"` must not answer
            // `users`. Only fall back to the folded hit when nothing matches case-sensitively, which is
            // the unquoted path Postgres folds (`from users` finding `Users` in a one-relation schema).
            return ExactName(inSchema, name) ?? inSchema[0];
        }

        if (!_byName.TryGetValue(n, out var candidates) || candidates.Count == 0)
            return null;
        if (candidates.Count == 1)
            return candidates[0];

        // Same rule, one step earlier: narrow to the exact-case spellings before search_path breaks ties,
        // so a `Users` in a later schema is not shadowed by a `users` in an earlier one (they are
        // different relations to Postgres, not two candidates for the same name).
        var pool = candidates.Where(t => string.Equals(t.Name, name, StringComparison.Ordinal)).ToList();
        if (pool.Count == 0) pool = candidates;
        if (pool.Count == 1) return pool[0];

        // Ambiguous bare name: prefer the earliest schema in search_path order.
        foreach (var s in Schemas)
        {
            var hit = pool.FirstOrDefault(t => string.Equals(t.Schema, s, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return pool[0];
    }

    /// <summary>The first candidate spelled exactly as the caller spelled it, or null when none is.</summary>
    private static TableInfo? ExactName(List<TableInfo> candidates, string name)
    {
        foreach (var t in candidates)
            if (string.Equals(t.Name, name, StringComparison.Ordinal)) return t;
        return null;
    }

    public IReadOnlyList<ForeignKeyInfo> ForeignKeysTouching(long tableId)
        => _fksByTable.TryGetValue(tableId, out var f) ? f : Array.Empty<ForeignKeyInfo>();
}
