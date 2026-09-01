using System;
using System.Collections.Generic;
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
    /// An index's detail line: what it enforces, its columns, and — loudly — whether the planner will use it
    /// at all. An invalid index is what a failed <c>CREATE INDEX CONCURRENTLY</c> leaves behind, and it is
    /// exactly what you are hunting when a query is slow despite "having an index".
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
