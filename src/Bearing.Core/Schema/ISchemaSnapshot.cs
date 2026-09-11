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
    /// One relation by its provider-assigned id, or null when the snapshot does not hold it.
    /// <para>
    /// Indexed, like <see cref="ColumnsOf"/> and <see cref="ResolveTable"/>, because the resolvers ask it
    /// per result column per foreign key: scanning <see cref="Tables"/> for each made the affordance pass
    /// over a wide <c>select *</c> quadratic in columns and linear in the whole catalog, on the thread that
    /// renders the grid.
    /// </para>
    /// </summary>
    TableInfo? TableById(long id);

    /// <summary>
    /// Resolve a table by optional schema + name. When schema is null, search_path order and
    /// identifier-casing/quoting rules decide the match. Returns null when nothing matches.
    /// </summary>
    TableInfo? ResolveTable(string? schema, string name);

    /// <summary>Every FK where the given table is either the referencing or the referenced side.</summary>
    IReadOnlyList<ForeignKeyInfo> ForeignKeysTouching(long tableId);
}
