namespace Squirrel.Core.Schema;

/// <summary>
/// Immutable, indexed implementation of <see cref="ISchemaSnapshot"/>. Built once from catalog
/// reads (or hand-built in tests) and then queried cheaply on every keystroke.
/// </summary>
public sealed class SchemaSnapshot : ISchemaSnapshot
{
    private readonly Dictionary<uint, List<PgColumn>> _columnsByTable;
    private readonly Dictionary<uint, List<PgForeignKey>> _fksByTable;
    // (schema, lower-name) and (lower-name) lookups
    private readonly Dictionary<(string schema, string name), PgTable> _bySchemaName;
    private readonly Dictionary<string, List<PgTable>> _byName;

    public string Database { get; }
    public IReadOnlyList<string> Schemas { get; }
    public IReadOnlyList<PgTable> Tables { get; }

    public SchemaSnapshot(
        string database,
        IReadOnlyList<string> schemas,
        IReadOnlyList<PgTable> tables,
        IReadOnlyList<PgColumn> columns,
        IReadOnlyList<PgForeignKey> foreignKeys)
    {
        Database = database;
        Schemas = schemas;
        Tables = tables;

        _columnsByTable = columns
            .GroupBy(c => c.TableOid)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.AttNum).ToList());

        _fksByTable = new Dictionary<uint, List<PgForeignKey>>();
        foreach (var fk in foreignKeys)
        {
            Add(_fksByTable, fk.ParentOid, fk);
            if (fk.ReferencedOid != fk.ParentOid)
                Add(_fksByTable, fk.ReferencedOid, fk);
        }

        _bySchemaName = tables.ToDictionary(t => (t.Schema.ToLowerInvariant(), t.Name.ToLowerInvariant()), t => t);
        _byName = tables
            .GroupBy(t => t.Name.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        static void Add(Dictionary<uint, List<PgForeignKey>> map, uint key, PgForeignKey fk)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<PgForeignKey>();
            list.Add(fk);
        }
    }

    public IReadOnlyList<PgColumn> ColumnsOf(uint tableOid)
        => _columnsByTable.TryGetValue(tableOid, out var c) ? c : Array.Empty<PgColumn>();

    public PgTable? ResolveTable(string? schema, string name)
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

    public IReadOnlyList<PgForeignKey> ForeignKeysTouching(uint tableOid)
        => _fksByTable.TryGetValue(tableOid, out var f) ? f : Array.Empty<PgForeignKey>();
}
