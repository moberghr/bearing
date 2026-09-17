using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bearing.App.ViewModels;
using Bearing.Core.Schema;

namespace Bearing.App.Workspace;

/// <summary>Where a kind of object belongs once a database's children are grouped (#132).</summary>
public enum SchemaKindPlacement
{
    /// <summary>It has a schema, so in full mode it sits inside that schema's groups.</summary>
    PerSchema,

    /// <summary>It belongs to the database rather than to any schema, so it sits in <c>Administer</c>.</summary>
    Administer,
}

/// <summary>One object kind as the tree shows it: its row title, its icon and text glyph, and where it
/// goes. Titles, icons and glyphs are the ones the flat run already used — a regrouping should not also
/// relabel everything.</summary>
public sealed record SchemaTreeKind(string Title, string IconKey, string Glyph, SchemaKindPlacement Placement);

/// <summary>
/// How a database node arranges its children (#132) — the decisions, with no view models in sight.
/// <para>
/// The tree grew to seventeen sibling groups a kind at a time, each placement right on its own (§9.9a) and
/// the sum unreadable. What is actually being decided is small and worth stating once: which kinds belong to
/// a schema and which to the database, what order a schema's groups read in, which schemas exist at all, and
/// how a count renders when there is nothing to count. Kept pure and out of <c>SchemaNodes.cs</c> so all four
/// are testable without a browser, a connection or a window (§2.5).
/// </para>
/// </summary>
public static class SchemaTreeShape
{
    /// <summary>Simple mode's one bucket for the long tail.</summary>
    public const string OtherObjects = "Other objects";

    /// <summary>Full mode's bucket for what belongs to the database rather than to a schema.</summary>
    public const string Administer = "Administer";

    /// <summary>Full mode's level above the schemas.</summary>
    public const string Schemas = "Schemas";

    /// <summary>
    /// What a group with nothing in it shows. Deliberately not <c>0</c>: a zero reads as a measurement, and
    /// the thing being said is that the kind is absent. A group whose read has not landed yet is not created
    /// at all, so this mark always means "asked, and there are none" (§1.7 — absences are typed).
    /// </summary>
    public const string EmptyCount = "—";

    public static string CountText(int count)
        => count <= 0 ? EmptyCount : count.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The thirteen late-read kinds, in the order they read, each placed.
    /// <para>
    /// The placement is not invented here — it is already in the data. <c>SchemaObjectInfo.Schema</c> is empty
    /// for a kind that has none, which is exactly the line between "inside a schema" and "about the database".
    /// Sequences, types and policies carry their schema in their own records instead, and policies take it
    /// from the table they guard.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SchemaTreeKind> Kinds { get; } =
    [
        new("Sequences", "Icon.Sequence", "#", SchemaKindPlacement.PerSchema),
        new("Types", "Icon.Type", "T", SchemaKindPlacement.PerSchema),
        new("Policies", "Icon.Policy", "§", SchemaKindPlacement.PerSchema),
        new("Collations", "Icon.Collation", "aA", SchemaKindPlacement.PerSchema),
        new("Operators", "Icon.Operator", "±", SchemaKindPlacement.PerSchema),
        new("Operator classes", "Icon.Operator", "±", SchemaKindPlacement.PerSchema),
        new("Text search", "Icon.TextSearch", "⌕", SchemaKindPlacement.PerSchema),

        new("Extensions", "Icon.Extension", "⬡", SchemaKindPlacement.Administer),
        new("Event triggers", "Icon.Trigger", "⚡", SchemaKindPlacement.Administer),
        new("Publications", "Icon.Publication", "⇈", SchemaKindPlacement.Administer),
        // Subscriptions share the publication icon: they are the same relationship seen from the other end,
        // and the glyph is what distinguishes them.
        new("Subscriptions", "Icon.Publication", "⇊", SchemaKindPlacement.Administer),
        new("Foreign servers", "Icon.ForeignServer", "⇄", SchemaKindPlacement.Administer),
        new("Casts", "Icon.Cast", "→", SchemaKindPlacement.Administer),
    ];

    /// <summary>
    /// The groups inside one schema, in reading order: what a query starts from first, then what it calls,
    /// then the machinery. Tables lead because they are what nearly every expand is for — the one thing full
    /// mode must not bury any deeper than it already does.
    /// </summary>
    public static IReadOnlyList<string> SchemaGroupTitles { get; } =
    [
        "Tables",
        "Views",
        "Functions",
        "Procedures",
        .. Kinds.Where(k => k.Placement == SchemaKindPlacement.PerSchema).Select(k => k.Title),
    ];

    /// <summary>
    /// Every schema this database has anything in, default first and the rest by name — the order the inline
    /// list already uses (<c>SchemaObjectLabel.SchemaRank</c>).
    /// </summary>
    /// <param name="kinds">
    /// The late read, when it has landed. A schema holding only sequences or only types exists as far as the
    /// user is concerned, and would be invisible if this counted tables and routines alone — which is what it
    /// did while the schema level was merely additive (§9.9a). Null before the read lands, and the list is
    /// then tables and routines only.
    /// </param>
    public static IReadOnlyList<string> SchemasOf(
        ISchemaSnapshot snapshot, IReadOnlyList<RoutineInfo> routines, DatabaseObjectKinds? kinds,
        string defaultSchema)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in snapshot.Tables) names.Add(table.Schema);
        foreach (var routine in routines) names.Add(routine.Schema);

        if (kinds is not null)
        {
            foreach (var sequence in kinds.Sequences) names.Add(sequence.Schema);
            foreach (var type in kinds.Types) names.Add(type.Schema);
            foreach (var item in kinds.Collations.Concat(kinds.Operators)
                         .Concat(kinds.OperatorClasses).Concat(kinds.TextSearchConfigs))
                if (item.Schema.Length > 0) names.Add(item.Schema);
        }

        return names
            .OrderBy(name => SchemaObjectLabel.SchemaRank(name, defaultSchema))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// How many objects the long tail holds, for simple mode's one bucket. The count is what stops that
    /// bucket reading as an empty group while its read is still out (#132's note), so it is the total of
    /// everything inside rather than the number of groups.
    /// </summary>
    public static int LongTailCount(DatabaseObjectKinds kinds)
        => kinds.Sequences.Count + kinds.Types.Count + kinds.Policies.Count + kinds.Extensions.Count
           + kinds.Publications.Count + kinds.Subscriptions.Count + kinds.ForeignServers.Count
           + kinds.EventTriggers.Count + kinds.Collations.Count + kinds.Casts.Count
           + kinds.Operators.Count + kinds.OperatorClasses.Count + kinds.TextSearchConfigs.Count;
}
