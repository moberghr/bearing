using System.Linq;

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
/// <param name="PartitionOf">
/// The relation this one is a partition of, or null when it stands alone. Carried in the snapshot — unlike
/// every other "extra" about a relation — because the tree needs it to <em>arrange</em> the relation list,
/// which happens before anything is expanded, and it is one nullable long per relation rather than a
/// per-column payload. Without it a partitioned table's children are a hundred sibling rows of noise with
/// no link to their parent.
/// </param>
public sealed record TableInfo(long Id, string Schema, string Name, RelationKind Kind, long? PartitionOf = null);

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
/// <param name="SizeBytes">
/// What the index costs on disk, or null when it was not read. Carried here rather than fetched separately
/// because it is the other half of "is this index worth keeping" (#76) and comes from the same per-table
/// read.
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
    bool BackedByConstraint = false,
    long? SizeBytes = null);

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
/// <summary>How a column gets its value when none is supplied.</summary>
public enum ColumnIdentity
{
    /// <summary>Not an identity column.</summary>
    None,

    /// <summary><c>GENERATED ALWAYS AS IDENTITY</c> — an explicit value is rejected without OVERRIDING.</summary>
    Always,

    /// <summary><c>GENERATED BY DEFAULT AS IDENTITY</c> — an explicit value wins.</summary>
    ByDefault,
}

/// <summary>
/// What a column has beyond its type: a default, an identity, a generated expression, a non-default
/// collation, a comment.
/// <para>
/// Deliberately <b>not</b> on <see cref="ColumnInfo"/>. That record lives in <see cref="ISchemaSnapshot"/>,
/// which is handed to the completion engine on every keystroke and loaded in bulk for every column of every
/// table — a default expression and a comment per column would inflate the one structure whose whole point
/// is being cheap, to answer a question only an expanded table asks. Same reasoning as
/// <see cref="TableDetails"/> itself, which is where this lives.
/// </para>
/// </summary>
/// <param name="Default">
/// The rendered <c>DEFAULT</c> expression, or null when the column has none. For a <c>serial</c> this is the
/// <c>nextval(…)</c> call, which is the route from a column <em>to</em> its sequence — the direction
/// <see cref="SequenceInfo.OwnedBy"/> does not give you.
/// </param>
/// <param name="GeneratedExpression">The <c>GENERATED ALWAYS AS (…) STORED</c> expression, or null.</param>
/// <param name="Collation">A non-default collation, or null. Null means the database default, not unknown.</param>
public sealed record ColumnDetail(
    int Ordinal,
    string? Default,
    ColumnIdentity Identity,
    string? GeneratedExpression,
    string? Collation,
    string? Comment);

/// <summary>
/// A rule on a relation (<c>pg_rewrite</c>). The <c>_RETURN</c> rule that <em>is</em> a view is excluded by
/// the reader — it is the view's definition, which the tree already shows as one.
/// </summary>
public sealed record RuleInfo(long Id, string Name, string Definition);

/// <param name="Policies">
/// The row-level security policies on this relation (#119). Here rather than only in the per-database group
/// because this is where you are standing when a row count surprises you — beside the constraints and
/// triggers of the table that returned fewer rows than you expected. Read on the same round trip as the
/// rest, so the placement costs nothing.
/// </param>
public sealed record TableDetails(
    IReadOnlyList<ConstraintInfo> Constraints,
    IReadOnlyList<IndexInfo> Indexes,
    IReadOnlyList<TriggerInfo> Triggers,
    IReadOnlyList<PolicyInfo> Policies)
{
    public static TableDetails Empty { get; } = new([], [], [], []);

    /// <summary>Per-column extras, by ordinal (see <see cref="ColumnDetail"/>). Empty when unread.</summary>
    public IReadOnlyList<ColumnDetail> Columns { get; init; } = [];

    /// <summary>Rules on this relation, the view-defining <c>_RETURN</c> excluded.</summary>
    public IReadOnlyList<RuleInfo> Rules { get; init; } = [];

    /// <summary>
    /// The relation's own comment (<c>obj_description</c>), or null. The schema's own documentation, which
    /// the explorer had no way to show at all — and the reason someone writes one is to be read here.
    /// </summary>
    public string? Comment { get; init; }

    /// <summary>The extras for one column, or null when they were not read.</summary>
    public ColumnDetail? ColumnAt(int ordinal)
    {
        foreach (var column in Columns)
            if (column.Ordinal == ordinal) return column;
        return null;
    }
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
    string ReturnType,
    string? Comment = null);

// ---- #119: the object kinds the explorer had no record for -------------------------------------------
// Sequences, types, extensions and RLS policies. One record each, in one place, because adding a kind
// touches a fixed set of layers and the shared costs — an interface method, a demo fixture, a field on
// DatabaseObjects — are paid once for all four rather than four times in the same files.

/// <summary>
/// A sequence: what you check when an id looks wrong, and the thing an <c>EXPLAIN ANALYZE</c>'s rollback
/// cannot undo (#98).
/// </summary>
/// <param name="LastValue">
/// <c>pg_sequences.last_value</c> — null for a sequence that has not been read from yet, which is a real
/// state and not the same as zero. Also null when the connecting role cannot read it.
/// </param>
/// <param name="OwnedBy">
/// <c>schema.table.column</c> when this sequence backs a <c>serial</c> or identity column, else null. The
/// route from a column to its sequence, which the tree had no way to offer.
/// </param>
public sealed record SequenceInfo(
    long Id,
    string Schema,
    string Name,
    string DataType,
    long? LastValue,
    long Increment,
    long MinValue,
    long MaxValue,
    bool Cycles,
    string? OwnedBy,
    string? Comment = null);

/// <summary>What kind of user-defined type this is (the ones worth surfacing).</summary>
public enum TypeKind
{
    /// <summary>An enum, whose allowed labels are exactly what you look up mid-query.</summary>
    Enum,

    /// <summary>A domain: a base type plus constraints.</summary>
    Domain,

    /// <summary>A composite type, with attributes of its own.</summary>
    Composite,

    /// <summary>A range type.</summary>
    Range,
}

/// <summary>
/// A user-defined type.
/// </summary>
/// <param name="Detail">
/// What the type <em>is</em>, rendered by the server where only the server can render it: an enum's labels
/// in <c>enumsortorder</c>, a domain's base type and check constraints, a composite's attributes. One field
/// rather than a shape per kind, because every kind's answer is a string the user reads and none of it is
/// computed against.
/// </param>
public sealed record TypeInfo(
    long Id,
    string Schema,
    string Name,
    TypeKind Kind,
    string Detail,
    string? Comment = null);

/// <summary>An installed extension — is <c>pg_stat_statements</c> here, and which version.</summary>
public sealed record ExtensionInfo(long Id, string Name, string Schema, string Version, string? Comment = null);

/// <summary>
/// A row-level security policy: the reason a query returns fewer rows than you expect, silently.
/// </summary>
/// <param name="Command">The command it applies to — <c>ALL</c>, <c>SELECT</c>, … as Postgres spells it.</param>
/// <param name="Permissive">
/// True for <c>PERMISSIVE</c> (the default, OR-ed with its peers), false for <c>RESTRICTIVE</c> (AND-ed).
/// The difference decides whether adding a policy can only widen access or can only narrow it.
/// </param>
/// <param name="Roles">The roles it applies to; <c>PUBLIC</c> when it applies to everyone.</param>
/// <param name="Using">The <c>USING</c> expression via <c>pg_get_expr</c>, or null when it has none.</param>
/// <param name="WithCheck">The <c>WITH CHECK</c> expression, or null.</param>
public sealed record PolicyInfo(
    long Id,
    string Name,
    long TableId,
    string Command,
    bool Permissive,
    IReadOnlyList<string> Roles,
    string? Using,
    string? WithCheck);

/// <summary>
/// A catalog object whose whole content is its name and one line about it.
/// <para>
/// One record for a dozen kinds — publications, subscriptions, foreign servers, event triggers, collations,
/// casts, operators, operator classes, text-search configurations, tablespaces — rather than a record each.
/// The same call <see cref="TypeInfo.Detail"/> already makes: every one of these answers with a string the
/// user reads and nothing computes against, so a shape per kind would be twelve near-identical records, node
/// types and tests carrying no extra information. A kind graduates to its own record when something starts
/// <em>using</em> a field (as <see cref="PolicyInfo.TableId"/> does).
/// </para>
/// </summary>
/// <param name="Schema">The owning schema, or empty for a kind that has none (a cast, a tablespace).</param>
/// <param name="Detail">The server's own rendering of what this is — see the reader for each kind.</param>
public sealed record SchemaObjectInfo(long Id, string Schema, string Name, string Detail, string? Comment = null);

/// <summary>
/// The per-database object kinds read on demand for the schema explorer (#119).
/// <para>
/// Init properties defaulting to empty rather than a positional constructor: there are now a dozen of them,
/// a caller that cares about one should not have to name eleven, and a provider with no answer for a kind
/// leaves it alone. Empty lists rather than nulls — "this database has no policies" and "this provider does
/// not report policies" both mean the tree shows no Policies group, and no caller has to tell them apart.
/// </para>
/// </summary>
public sealed record DatabaseObjectKinds
{
    public static DatabaseObjectKinds Empty { get; } = new();

    public IReadOnlyList<SequenceInfo> Sequences { get; init; } = [];
    public IReadOnlyList<TypeInfo> Types { get; init; } = [];
    public IReadOnlyList<ExtensionInfo> Extensions { get; init; } = [];
    public IReadOnlyList<PolicyInfo> Policies { get; init; } = [];

    /// <summary>Logical-replication publications defined in this database.</summary>
    public IReadOnlyList<SchemaObjectInfo> Publications { get; init; } = [];

    /// <summary>
    /// Subscriptions belonging to this database. <c>pg_subscription</c> is a cluster-wide catalog but each
    /// row names one database, so this is scoped to the connected one — which is also where a user looks.
    /// </summary>
    public IReadOnlyList<SchemaObjectInfo> Subscriptions { get; init; } = [];

    /// <summary>Foreign servers and the wrapper behind each — what a foreign table actually points at.</summary>
    public IReadOnlyList<SchemaObjectInfo> ForeignServers { get; init; } = [];

    /// <summary>Event triggers. Per database, despite firing on DDL that feels cluster-wide.</summary>
    public IReadOnlyList<SchemaObjectInfo> EventTriggers { get; init; } = [];

    public IReadOnlyList<SchemaObjectInfo> Collations { get; init; } = [];
    public IReadOnlyList<SchemaObjectInfo> Casts { get; init; } = [];
    public IReadOnlyList<SchemaObjectInfo> Operators { get; init; } = [];
    public IReadOnlyList<SchemaObjectInfo> OperatorClasses { get; init; } = [];
    public IReadOnlyList<SchemaObjectInfo> TextSearchConfigs { get; init; } = [];

    /// <summary>Whether there is anything at all to show — every group would be skipped when false.</summary>
    public bool IsEmpty
        => Sequences.Count == 0 && Types.Count == 0 && Extensions.Count == 0 && Policies.Count == 0
           && Publications.Count == 0 && Subscriptions.Count == 0 && ForeignServers.Count == 0
           && EventTriggers.Count == 0 && Collations.Count == 0 && Casts.Count == 0
           && Operators.Count == 0 && OperatorClasses.Count == 0 && TextSearchConfigs.Count == 0;

    /// <summary>The policies on one relation, for the folder under the table itself.</summary>
    public IEnumerable<PolicyInfo> PoliciesOf(long tableId) => Policies.Where(p => p.TableId == tableId);
}


// ---- #120: roles and grants ---------------------------------------------------------------------------
// Kept apart from DatabaseObjectKinds on purpose: roles are cluster-wide, not per database, so they do not
// belong under a database node and cannot be read per database without saying something false about scope.

/// <summary>
/// A role, as <c>pg_roles</c> reports it — <b>never</b> <c>pg_authid</c>, which is where the password hash
/// lives. <c>pg_roles</c> masks it; this record has nowhere to put it even if it were read.
/// </summary>
/// <param name="CanLogin">
/// <c>rolcanlogin</c>. The line between a login role and a group: a role that cannot log in is a bag of
/// privileges other roles are granted, which is a different thing to look at.
/// </param>
/// <param name="ConnectionLimit">
/// <c>rolconnlimit</c>. <b>-1 means unlimited</b>, which is the default and not an absence — rendering it as
/// a number would say the role may open minus one connection.
/// </param>
/// <param name="ValidUntil">
/// <c>rolvaliduntil</c>, or null when the role has <em>no expiry</em>. Null here is a real state and not a
/// permission problem: conflating "never expires" with "you may not see this" would assert a cause nobody
/// checked (§1.1), so the two are kept distinguishable all the way to the row.
/// </param>
/// <param name="MemberOf">Roles this one is a member of (<c>pg_auth_members</c>), by name.</param>
public sealed record RoleInfo(
    long Id,
    string Name,
    bool CanLogin,
    bool IsSuperuser,
    bool CanCreateDb,
    bool CanCreateRole,
    bool InheritsPrivileges,
    int ConnectionLimit,
    DateTimeOffset? ValidUntil,
    IReadOnlyList<string> MemberOf);

/// <summary>
/// What one role may do to one object on the connected database (#120) — an <c>aclitem</c> rendered
/// readably, so it says <c>SELECT, INSERT</c> rather than <c>arwd</c>.
/// </summary>
/// <param name="Object">
/// What the privileges are on: the database itself (<c>"database app"</c>) or a relation
/// (<c>"public.payment"</c>). A string because these come from different catalogs and the row only reads it.
/// </param>
/// <param name="Privileges">The granted privileges, spelled as the DDL does, in a stable order.</param>
public sealed record RoleGrant(string Object, IReadOnlyList<string> Privileges);

/// <summary>
/// A role's grants on one database, plus whether the reading role could see them at all.
/// </summary>
/// <param name="Grants">What was found. Empty is ambiguous on its own, which is why <paramref name="Visible"/>
/// exists.</param>
/// <param name="Visible">
/// False when the read itself was refused. Carried rather than collapsed into an empty list because "this
/// role has no grants here" and "you are not allowed to find out" are different answers, and showing the
/// second as the first is the one mistake a privilege screen must not make.
/// </param>
public sealed record RoleGrants(IReadOnlyList<RoleGrant> Grants, bool Visible)
{
    public static RoleGrants NotVisible { get; } = new([], false);

    public static RoleGrants Of(IReadOnlyList<RoleGrant> grants) => new(grants, true);
}
