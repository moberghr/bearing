using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bearing.Core.Schema;

namespace Bearing.App.ViewModels;

/// <summary>
/// How a table's constraints, indexes, triggers and foreign keys read in the schema tree (#46). Pure, so the
/// wording and the column-name resolution are unit-testable without a tree or a server (§2.5).
/// <para>
/// Every one of these rows exists to answer a question the user has just before writing a join or diagnosing
/// a slow query, so the row itself has to carry the answer: which columns, in which order, pointing where.
/// A name alone ("payment_store_id_fkey") makes the tree a list of things to click.
/// </para>
/// </summary>
internal static class RelationDetailText
{
    /// <summary>The columns an ordinal list names, comma-joined. Ordinals the snapshot cannot resolve are
    /// rendered as their number rather than dropped, so a row never silently understates a key.</summary>
    public static string Columns(ISchemaSnapshot snapshot, long tableId, IReadOnlyList<int> ordinals)
    {
        if (ordinals.Count == 0) return "";
        var columns = snapshot.ColumnsOf(tableId);
        return string.Join(", ", ordinals.Select(o =>
            columns.FirstOrDefault(c => c.Ordinal == o)?.Name ?? $"#{o}"));
    }

    /// <summary>A relation's qualified name, or its id when the snapshot has never heard of it — a foreign
    /// key can point at a table in a schema the snapshot filtered out.</summary>
    public static string TableName(ISchemaSnapshot snapshot, long tableId)
        => snapshot.Tables.FirstOrDefault(t => t.Id == tableId) is { } table
            ? $"{table.Schema}.{table.Name}"
            : $"(table {tableId})";

    // ---- constraints ----------------------------------------------------------------------------

    /// <summary>
    /// A constraint's detail line: its kind, then the columns it covers — except for a CHECK, where the
    /// expression <i>is</i> the answer and the column list is noise beside it.
    /// </summary>
    public static string Constraint(ISchemaSnapshot snapshot, long tableId, ConstraintInfo constraint)
    {
        var kind = KindLabel(constraint.Kind);
        if (constraint.Kind == ConstraintKind.Check)
            return constraint.Definition.Length > 0 ? $"{kind} · {Body(constraint.Definition, "CHECK")}" : kind;

        var columns = Columns(snapshot, tableId, constraint.Ordinals);
        return columns.Length > 0 ? $"{kind} · {columns}" : kind;
    }

    public static string KindLabel(ConstraintKind kind) => kind switch
    {
        ConstraintKind.PrimaryKey => "primary key",
        ConstraintKind.Unique => "unique",
        ConstraintKind.Check => "check",
        ConstraintKind.ForeignKey => "foreign key",
        ConstraintKind.Exclusion => "exclusion",
        _ => "constraint",
    };

    /// <summary>The glyph column: a key for the kinds that identify a row, a guard for the kinds that
    /// restrict one.</summary>
    public static string ConstraintGlyph(ConstraintKind kind) => kind switch
    {
        ConstraintKind.PrimaryKey => "🔑",
        ConstraintKind.Unique => "◇",
        ConstraintKind.ForeignKey => "→",
        _ => "⊘",
    };

    // ---- indexes --------------------------------------------------------------------------------

    /// <summary>
    /// An index's detail line: what it enforces, its <b>key</b> columns, and — loudly — whether the planner
    /// will use it at all. An invalid index is what a failed <c>CREATE INDEX CONCURRENTLY</c> leaves behind,
    /// and it is exactly what you are hunting when a query is slow despite "having an index".
    /// <para>
    /// Key columns only: the row exists to answer "will this serve my predicate", so an <c>INCLUDE</c> payload
    /// listed beside them would be the one detail it must not misstate — those columns are carried, not
    /// searchable.
    /// </para>
    /// </summary>
    public static string Index(ISchemaSnapshot snapshot, long tableId, IndexInfo index)
    {
        var parts = new List<string>();
        if (index.IsPrimary) parts.Add("primary key");
        else if (index.IsUnique) parts.Add("unique");
        else parts.Add("index");

        // No resolvable columns means every key is an expression — the definition says what it is on.
        var columns = Columns(snapshot, tableId, index.Ordinals);
        if (columns.Length > 0) parts.Add(columns);
        else if (index.Definition.Length > 0) parts.Add(Body(index.Definition, "USING"));

        if (!index.IsValid) parts.Add("INVALID — not used by the planner");
        return string.Join(" · ", parts);
    }

    public static string IndexGlyph(IndexInfo index)
        => !index.IsValid ? "⚠" : index.IsPrimary ? "🔑" : index.IsUnique ? "◇" : "≡";

    // ---- triggers -------------------------------------------------------------------------------

    /// <summary>
    /// A trigger's detail line: when it fires and on what, pulled out of the server's own
    /// <c>CREATE TRIGGER</c> text, plus whether it is disabled — a disabled trigger looks identical to an
    /// enabled one everywhere else.
    /// </summary>
    public static string Trigger(TriggerInfo trigger)
    {
        var when = Timing(trigger.Definition);
        var state = trigger.Enabled ? null : "disabled";
        return string.Join(" · ", new[] { when, state }.Where(p => !string.IsNullOrEmpty(p)));
    }

    public static string TriggerGlyph(TriggerInfo trigger) => trigger.Enabled ? "⚡" : "◌";

    /// <summary>
    /// The <c>BEFORE INSERT OR UPDATE ON …</c> part of a trigger definition, without the function call.
    /// Read out of the text rather than carried as fields: <c>pg_get_triggerdef</c> is the only rendering
    /// that gets the WHEN clause and the column list right, and re-deriving it from <c>tgtype</c>'s bit flags
    /// would be a second implementation to keep correct.
    /// </summary>
    private static string Timing(string definition)
    {
        var from = definition.IndexOf(" BEFORE ", StringComparison.OrdinalIgnoreCase);
        if (from < 0) from = definition.IndexOf(" AFTER ", StringComparison.OrdinalIgnoreCase);
        if (from < 0) from = definition.IndexOf(" INSTEAD OF ", StringComparison.OrdinalIgnoreCase);
        if (from < 0) return "";

        var to = definition.IndexOf(" ON ", from, StringComparison.OrdinalIgnoreCase);
        return (to < 0 ? definition[(from + 1)..] : definition[(from + 1)..to]).Trim().ToLowerInvariant();
    }

    // ---- foreign keys, in both directions -------------------------------------------------------

    /// <summary>
    /// An outgoing key: what a row of <i>this</i> table points at. Reads as the join it would become —
    /// <c>store_id → shop.store(id)</c>.
    /// </summary>
    public static string Outgoing(ISchemaSnapshot snapshot, ForeignKeyInfo fk)
        => $"{Columns(snapshot, fk.ParentTableId, fk.ParentOrdinals)} → "
           + $"{TableName(snapshot, fk.ReferencedTableId)}({Columns(snapshot, fk.ReferencedTableId, fk.ReferencedOrdinals)})";

    /// <summary>
    /// An incoming key: who points at <i>this</i> table, which is the question "what breaks if I delete this
    /// row?". The arrow points the same way as the reference itself, so the row reads left to right as the
    /// other table pointing here.
    /// </summary>
    public static string Incoming(ISchemaSnapshot snapshot, ForeignKeyInfo fk)
        => $"{TableName(snapshot, fk.ParentTableId)}({Columns(snapshot, fk.ParentTableId, fk.ParentOrdinals)}) → "
           + $"{Columns(snapshot, fk.ReferencedTableId, fk.ReferencedOrdinals)}";

    /// <summary>
    /// Split a table's foreign keys by direction. <c>ForeignKeysTouching</c> returns both sides, and the two
    /// answer different questions — which is why they get separate folders rather than one list.
    /// </summary>
    public static (List<ForeignKeyInfo> Outgoing, List<ForeignKeyInfo> Incoming) SplitByDirection(
        ISchemaSnapshot snapshot, long tableId)
    {
        var outgoing = new List<ForeignKeyInfo>();
        var incoming = new List<ForeignKeyInfo>();
        foreach (var fk in snapshot.ForeignKeysTouching(tableId))
        {
            // A self-referencing key is genuinely both, and belongs in both folders: it is what a row points
            // at *and* what would break, and leaving it out of one of them is how a parent_id gets missed.
            if (fk.ParentTableId == tableId) outgoing.Add(fk);
            if (fk.ReferencedTableId == tableId) incoming.Add(fk);
        }
        return (outgoing, incoming);
    }

    // ---- #119: sequences, types, extensions and policies ---------------------------------------------

    /// <summary>
    /// A sequence's detail line: its type, where it has got to, and how it steps.
    /// <para>
    /// A sequence that has never been read from says so rather than showing a number: <c>last_value</c> is
    /// null then, and rendering it as 0 would claim the first id has already been handed out. The same null
    /// arrives when the role may not read the sequence, which is why the phrasing does not assert a cause
    /// (§1.1).
    /// </para>
    /// </summary>
    public static string Sequence(SequenceInfo sequence)
    {
        var parts = new List<string> { sequence.DataType };
        parts.Add(sequence.LastValue is { } last
            ? $"at {last.ToString("N0", CultureInfo.InvariantCulture)}"
            : "not yet used");
        if (sequence.Increment != 1)
            parts.Add($"by {sequence.Increment.ToString("N0", CultureInfo.InvariantCulture)}");
        if (sequence.Cycles) parts.Add("cycles");
        // The route from a sequence back to the column it feeds, which is the question that sends you here.
        if (sequence.OwnedBy is { } owner) parts.Add($"owned by {owner}");
        return WithComment(string.Join(" · ", parts), sequence.Comment);
    }

    /// <summary>
    /// A type's detail line: what kind it is, then what it holds — an enum's labels, a domain's base type
    /// and constraints, a composite's attributes. Truncated, because an enum can have fifty labels and a
    /// detail line is one line; the full text is on the node's definition.
    /// </summary>
    public static string UserType(TypeInfo type)
    {
        var kind = TypeKindLabel(type.Kind);
        var detail = type.Detail.Length == 0 ? kind : $"{kind} · {Truncate(type.Detail, 90)}";
        return WithComment(detail, type.Comment);
    }

    public static string TypeKindLabel(TypeKind kind) => kind switch
    {
        TypeKind.Enum => "enum",
        TypeKind.Domain => "domain",
        TypeKind.Range => "range",
        _ => "composite",
    };

    /// <summary>
    /// The type as DDL, for the node's definition view. Assembled from the server's own rendering of the
    /// body — this adds the wrapper, and never re-derives what <c>pg_get_constraintdef</c> or
    /// <c>format_type</c> already said.
    /// </summary>
    public static string TypeDdl(TypeInfo type)
    {
        var qualified = $"{type.Schema}.{type.Name}";
        return type.Kind switch
        {
            TypeKind.Enum => $"CREATE TYPE {qualified} AS ENUM ({type.Detail});",
            TypeKind.Domain => $"CREATE DOMAIN {qualified} AS {type.Detail};",
            TypeKind.Composite => $"CREATE TYPE {qualified} AS ({type.Detail});",
            _ => $"CREATE TYPE {qualified} AS RANGE (subtype = {type.Detail});",
        };
    }

    /// <summary>An extension's detail line: its version, and the schema it installed into.</summary>
    public static string Extension(ExtensionInfo extension)
        => WithComment(
            extension.Schema.Length == 0 ? extension.Version : $"{extension.Version} · in {extension.Schema}",
            extension.Comment);

    /// <summary>
    /// A policy's row label: its own name, and the table it is on when the caller has a snapshot to resolve
    /// that with. The table matters in the per-database group, where two policies with the same name on
    /// different tables would otherwise be indistinguishable; under the table itself it is already implied,
    /// and the snapshot resolving to nothing simply leaves it out.
    /// </summary>
    public static string PolicyTitle(PolicyInfo policy, ISchemaSnapshot? snapshot)
    {
        if (snapshot is null) return policy.Name;
        var table = TableName(snapshot, policy.TableId);
        return table.Length == 0 ? policy.Name : $"{policy.Name} on {table}";
    }

    /// <summary>
    /// A policy's detail line: the command it covers, whether it permits or restricts, and the roles.
    /// <para>
    /// Permissive is named rather than left implicit even though it is the default, because the two combine
    /// in opposite directions — permissive policies are OR-ed and restrictive ones AND-ed — so "which kind
    /// is this" decides whether the policy can only widen access or only narrow it.
    /// </para>
    /// </summary>
    public static string Policy(PolicyInfo policy)
    {
        var parts = new List<string>
        {
            policy.Command,
            policy.Permissive ? "permissive" : "restrictive",
        };
        if (policy.Roles.Count > 0) parts.Add("to " + string.Join(", ", policy.Roles));
        if (policy.Using is not null) parts.Add("using " + Truncate(policy.Using, 60));
        return string.Join(" · ", parts);
    }

    /// <summary>The policy's expressions in full, for its definition view. Both are shown when both exist:
    /// <c>USING</c> decides which rows are visible and <c>WITH CHECK</c> which ones may be written, and they
    /// are frequently not the same predicate.</summary>
    public static string PolicyDefinition(PolicyInfo policy)
    {
        var lines = new List<string>
        {
            $"-- {policy.Name}: {policy.Command}, "
            + (policy.Permissive ? "permissive" : "restrictive")
            + (policy.Roles.Count > 0 ? ", to " + string.Join(", ", policy.Roles) : ""),
        };
        if (policy.Using is { } predicate) lines.Add($"USING ({predicate})");
        if (policy.WithCheck is { } check) lines.Add($"WITH CHECK ({check})");
        return string.Join("\n", lines);
    }

    // ---- column extras, rules, comments, and the name-plus-a-line kinds -------------------------------

    /// <summary>
    /// A column's detail line: its type, then everything that changes what a value <em>becomes</em> —
    /// generated, identity, default — then the constraints on it.
    /// <para>
    /// Ordered by what surprises someone reading a row. A generated column cannot be written at all; an
    /// identity column can only be written with <c>OVERRIDING</c>; a default only applies when nothing is
    /// supplied. Putting the type first and the rest in that order means the row reads as "what will happen
    /// if I insert without this column".
    /// </para>
    /// </summary>
    public static string Column(ColumnInfo column, ColumnDetail? detail)
    {
        var parts = new List<string> { column.DataType };

        if (detail?.GeneratedExpression is { } generated)
            parts.Add($"generated always as {Truncate(generated, 44)}");
        else if (detail?.Identity == ColumnIdentity.Always) parts.Add("identity (always)");
        else if (detail?.Identity == ColumnIdentity.ByDefault) parts.Add("identity (by default)");
        else if (detail?.Default is { } value) parts.Add($"default {Truncate(value, 44)}");

        if (column.NotNull) parts.Add("not null");
        if (column.IsPrimaryKey) parts.Add("PK");
        // Only ever a *non-default* collation (the reader drops the rest), so its presence is the finding.
        if (detail?.Collation is { } collation) parts.Add($"collate {collation}");
        if (detail?.Comment is { } comment) parts.Add(Truncate(comment, 60));

        return string.Join(" · ", parts);
    }

    /// <summary>A rule's detail line: the event it rewrites, out of the server's own <c>CREATE RULE</c>.</summary>
    public static string Rule(RuleInfo rule)
    {
        // "AS ON INSERT TO x DO INSTEAD …" — the interesting half is between ON and DO.
        var text = rule.Definition.ReplaceLineEndings(" ");
        var on = text.IndexOf(" ON ", StringComparison.OrdinalIgnoreCase);
        var doAt = text.IndexOf(" DO ", StringComparison.OrdinalIgnoreCase);
        if (on < 0) return Truncate(text, 70);
        var head = doAt > on ? text[(on + 4)..doAt] : text[(on + 4)..];
        var instead = text.Contains(" DO INSTEAD", StringComparison.OrdinalIgnoreCase) ? " · instead" : null;
        return Truncate(head.Trim().ToLowerInvariant(), 60) + instead;
    }

    /// <summary>
    /// The detail line for a <see cref="SchemaObjectInfo"/> — its server-rendered detail, then its comment.
    /// One function for a dozen kinds, for the same reason there is one record.
    /// </summary>
    public static string SchemaObject(SchemaObjectInfo item)
    {
        if (item.Comment is not { } comment) return item.Detail;
        return item.Detail.Length == 0 ? Truncate(comment, 70) : $"{item.Detail} · {Truncate(comment, 50)}";
    }

    /// <summary>
    /// A relation's detail line, with its comment appended where it has one — the schema's own
    /// documentation, and the reason someone wrote one is to be read here.
    /// </summary>
    public static string WithComment(string detail, string? comment)
        => comment is null ? detail : (detail.Length == 0 ? Truncate(comment, 70) : $"{detail} · {Truncate(comment, 50)}");

    /// <summary>
    /// How a partitioned parent's row says how much is under it. Counted rather than listed on the row: the
    /// partitions are its children now, and a hundred names would not fit anyway.
    /// </summary>
    public static string Partitions(int count)
        => count == 1 ? "1 partition" : $"{count} partitions";

    // ---- #120: roles ----------------------------------------------------------------------------------

    /// <summary>
    /// A role's detail line: whether it can log in, the attributes that matter, and any limit or expiry.
    /// <para>
    /// "no expiry" and "unlimited" are said out loud rather than left blank. A blank on a privileges row
    /// reads as "unknown", and the two states this actually reports — no expiry set, and no connection limit
    /// — are facts, not absences of one (§1.1).
    /// </para>
    /// </summary>
    public static string Role(RoleInfo role)
    {
        var parts = new List<string> { role.CanLogin ? "login" : "group" };
        if (role.IsSuperuser) parts.Add("superuser");
        if (role.CanCreateDb) parts.Add("createdb");
        if (role.CanCreateRole) parts.Add("createrole");
        if (!role.InheritsPrivileges) parts.Add("noinherit");
        // -1 is Postgres' "unlimited"; printing the number would say the role may open minus one connection.
        if (role.ConnectionLimit >= 0) parts.Add($"max {role.ConnectionLimit} connections");
        if (role.ValidUntil is { } until)
            parts.Add($"until {until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        if (role.MemberOf.Count > 0) parts.Add($"member of {string.Join(", ", role.MemberOf)}");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// A role in full, for its definition view. Deliberately a description rather than a
    /// <c>CREATE ROLE</c>: what is shown here is read from the catalog and could not be replayed safely
    /// anyway — there is no password to reproduce, and this feature grants nothing (§1.2).
    /// </summary>
    public static string RoleSummary(RoleInfo role, string database)
    {
        var lines = new List<string>
        {
            $"-- role {role.Name}",
            role.CanLogin ? "can log in" : "cannot log in (a group role)",
            $"superuser: {YesNo(role.IsSuperuser)}",
            $"create databases: {YesNo(role.CanCreateDb)}",
            $"create roles: {YesNo(role.CanCreateRole)}",
            $"inherits privileges of roles it belongs to: {YesNo(role.InheritsPrivileges)}",
            role.ConnectionLimit < 0
                ? "connection limit: unlimited"
                : $"connection limit: {role.ConnectionLimit}",
            role.ValidUntil is { } until
                ? $"valid until: {until.ToString("u", CultureInfo.InvariantCulture)}"
                : "valid until: no expiry",
            role.MemberOf.Count > 0
                ? "member of: " + string.Join(", ", role.MemberOf)
                : "member of: nothing",
        };
        // Named, because the grants under this role are about one database while the role itself is the
        // server's — a distinction the tree can only imply.
        lines.Add($"grants shown are for the database {database} only");
        // Said explicitly: someone reading a privileges screen may reasonably wonder whether the password is
        // in here somewhere, and the answer is that it is never read (§1.1).
        lines.Add("no password material is read or shown");
        return string.Join(NewLine, lines);
    }

    /// <summary>Line separator for the multi-line summaries above, as a constant so a patch cannot turn the
    /// escape into a real newline in the source.</summary>
    private const string NewLine = "\n";

    private static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>Cut a long rendering to fit a one-line detail, with an ellipsis so it is visibly cut rather
    /// than looking like the whole of a shorter value.</summary>
    private static string Truncate(string text, int max)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// The part of a rendered definition after <paramref name="keyword"/> — the CHECK expression, the index's
    /// <c>USING …</c>. Falls back to the whole text when the keyword is absent, which is better than an empty
    /// detail line.
    /// </summary>
    private static string Body(string definition, string keyword)
    {
        var at = definition.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        return (at < 0 ? definition : definition[at..]).Trim();
    }
}
