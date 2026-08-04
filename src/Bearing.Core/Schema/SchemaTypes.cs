namespace Bearing.Core.Schema;

/// <summary>Relation kind (the ones we surface).</summary>
public enum RelationKind
{
    Table,
    View,
    MaterializedView,
    ForeignTable,
    Partitioned,
}

/// <summary>A relation (table/view/…) from the catalog, identified by a provider-assigned id.</summary>
public sealed record TableInfo(long Id, string Schema, string Name, RelationKind Kind);

/// <summary>A column of a relation, identified by its owning table id + ordinal within that table.</summary>
public sealed record ColumnInfo(
    long TableId,
    int Ordinal,
    string Name,
    string DataType,
    bool NotNull,
    bool IsPrimaryKey);

/// <summary>
/// A foreign-key constraint. The referencing side ("parent") points at the referenced side.
/// Column lists are parallel (parent[i] references referenced[i]).
/// </summary>
public sealed record ForeignKeyInfo(
    long Id,
    string Name,
    long ParentTableId,
    IReadOnlyList<int> ParentOrdinals,
    long ReferencedTableId,
    IReadOnlyList<int> ReferencedOrdinals);

/// <summary>Callable-routine kind (the ones we surface).</summary>
public enum RoutineKind
{
    Function,
    Procedure,
    Aggregate,
    Window,
}

/// <summary>
/// A stored routine (function/procedure/aggregate/window), identified by a provider-assigned id.
/// <see cref="Arguments"/> is the rendered argument list and <see cref="ReturnType"/> the rendered
/// result (empty for procedures).
/// </summary>
public sealed record RoutineInfo(
    long Id,
    string Schema,
    string Name,
    RoutineKind Kind,
    string Arguments,
    string ReturnType);
