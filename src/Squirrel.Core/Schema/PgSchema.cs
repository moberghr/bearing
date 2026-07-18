namespace Squirrel.Core.Schema;

/// <summary>Relation kind, mirroring pg_class.relkind (the ones we surface).</summary>
public enum PgRelKind
{
    Table,
    View,
    MaterializedView,
    ForeignTable,
    Partitioned,
}

/// <summary>A relation (table/view/…) as read from the catalog. OID-identified.</summary>
public sealed record PgTable(uint Oid, string Schema, string Name, PgRelKind Kind);

/// <summary>A column of a relation. Identified by (owning table OID, attribute number).</summary>
public sealed record PgColumn(
    uint TableOid,
    short AttNum,
    string Name,
    string DataType,
    bool NotNull,
    bool IsPrimaryKey);

/// <summary>
/// A foreign-key constraint. The referencing side ("parent") points at the referenced side.
/// Column lists are parallel (parent[i] references referenced[i]).
/// </summary>
public sealed record PgForeignKey(
    uint ConstraintOid,
    string Name,
    uint ParentOid,
    IReadOnlyList<short> ParentAttNums,
    uint ReferencedOid,
    IReadOnlyList<short> ReferencedAttNums);

/// <summary>Callable-routine kind, mirroring pg_proc.prokind (the ones we surface).</summary>
public enum PgRoutineKind
{
    Function,
    Procedure,
    Aggregate,
    Window,
}

/// <summary>
/// A stored routine (function/procedure/aggregate/window) as read from pg_proc. OID-identified.
/// <see cref="Arguments"/> is the rendered argument list (pg_get_function_arguments) and
/// <see cref="ReturnType"/> the rendered result (pg_get_function_result; empty for procedures).
/// </summary>
public sealed record PgRoutine(
    uint Oid,
    string Schema,
    string Name,
    PgRoutineKind Kind,
    string Arguments,
    string ReturnType);
