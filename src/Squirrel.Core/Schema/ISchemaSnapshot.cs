namespace Squirrel.Core.Schema;

/// <summary>
/// An immutable, cheaply-queryable view of one database's structure, handed to the completion
/// engine on every keystroke. Loaded once per connection (bulk catalog read) and cached; the
/// engine treats it as a pure value and never does I/O.
/// </summary>
public interface ISchemaSnapshot
{
    string Database { get; }

    /// <summary>Schemas, in search_path order where known (falls back to alphabetical).</summary>
    IReadOnlyList<string> Schemas { get; }

    IReadOnlyList<TableInfo> Tables { get; }

    IReadOnlyList<ColumnInfo> ColumnsOf(long tableId);

    /// <summary>
    /// Resolve a table by optional schema + name. When schema is null, search_path order and
    /// identifier-casing/quoting rules decide the match. Returns null when nothing matches.
    /// </summary>
    TableInfo? ResolveTable(string? schema, string name);

    /// <summary>Every FK where the given table is either the referencing or the referenced side.</summary>
    IReadOnlyList<ForeignKeyInfo> ForeignKeysTouching(long tableId);
}
