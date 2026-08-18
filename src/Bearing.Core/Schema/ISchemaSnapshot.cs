namespace Bearing.Core.Schema;

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

    /// <summary>
    /// Only the schemas reachable without qualification (the server's search_path), in order — a subset
    /// of <see cref="Schemas"/>, which also lists every other schema that holds relations. A relation
    /// outside this set has to be written schema-qualified to resolve, which is what tells completion
    /// when a bare name would be wrong.
    /// </summary>
    IReadOnlyList<string> SearchPath { get; }

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
