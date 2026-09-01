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

/// <summary>Constraint kind, from <c>pg_constraint.contype</c>.</summary>
public enum ConstraintKind
{
    PrimaryKey,
    Unique,
    Check,
    ForeignKey,
    Exclusion,
    Other,
}

/// <summary>
/// A table constraint. <see cref="Definition"/> is the server's own rendering
/// (<c>pg_get_constraintdef</c>) rather than something reassembled from parts — a CHECK body cannot be
/// rebuilt from catalog columns, and for the kinds that could be, the server's text is the one that matches
/// what the table actually has.
/// </summary>
public sealed record ConstraintInfo(
    long Id,
    string Name,
    ConstraintKind Kind,
    IReadOnlyList<int> Ordinals,
    string Definition);

/// <summary>
/// An index on a relation. <see cref="Definition"/> is <c>pg_get_indexdef</c> — a complete
/// <c>CREATE INDEX</c>, which is what makes an expression or partial index legible at all.
/// <para>
/// <see cref="IsPrimary"/> and <see cref="IsUnique"/> are separate because a unique index is not always a
/// constraint and a primary key is always both; <see cref="IsValid"/> is false for an index left behind by a
/// failed <c>CREATE INDEX CONCURRENTLY</c>, which the planner ignores — exactly the thing you are looking for
/// when a query is unexpectedly slow.
/// </para>
/// </summary>
/// <param name="Ordinals">
/// The <b>key</b> columns, in index order — not the <c>INCLUDE</c> payload, which the planner cannot search
/// on. An index on expressions alone reports none, and its <paramref name="Definition"/> is then the only
/// thing that says what it covers.
/// </param>
/// <param name="BackedByConstraint">
/// True when a constraint owns this index — a primary key, a unique constraint, an exclusion constraint.
/// Such an index is created by its constraint and cannot be issued separately, so generated DDL must not
/// emit it: the name is already taken by then.
/// </param>
public sealed record IndexInfo(
    long Id,
    string Name,
    bool IsUnique,
    bool IsPrimary,
    bool IsValid,
    IReadOnlyList<int> Ordinals,
    string Definition,
    bool BackedByConstraint = false);

/// <summary>A trigger on a relation. <see cref="Definition"/> is <c>pg_get_triggerdef</c>.</summary>
public sealed record TriggerInfo(
    long Id,
    string Name,
    bool Enabled,
    string Definition);

/// <summary>
/// The per-table metadata that is deliberately <b>not</b> in <see cref="ISchemaSnapshot"/>: constraints,
/// indexes and triggers, read on demand when a table is expanded.
/// <para>
/// The snapshot is on the completion hot path — handed to the engine on every keystroke, treated as a pure
/// value, loaded in bulk per connection. Every index and constraint of every table would inflate a structure
/// whose whole point is being cheap, to answer questions only a table the user actually opened can ask.
/// </para>
/// </summary>
public sealed record TableDetails(
    IReadOnlyList<ConstraintInfo> Constraints,
    IReadOnlyList<IndexInfo> Indexes,
    IReadOnlyList<TriggerInfo> Triggers)
{
    public static TableDetails Empty { get; } = new([], [], []);
}

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
