using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.ViewModels;

namespace Bearing.App.Workspace;

/// <summary>
/// Finding a relation in the schema tree (#117) — the matching half of "go to table" and "go to definition".
/// <para>
/// Deliberately <b>not</b> a <c>PathTo</c> like <see cref="ScriptTreeReveal"/>'s, and that difference is the
/// whole design note. The scripts tree is materialized: the chain to a file is a pure walk over nodes that
/// already exist. Schema nodes load their children on first expand, so the path to a table does not exist
/// until three awaited round trips have happened — the databases of a server, the relations of a database,
/// and (for a column) the columns of a relation. A pure function cannot produce it, so what lives here is
/// the part that <em>is</em> pure: which node at a given level is the one to descend into. The awaiting is
/// <c>ConnectionsViewModel.RevealRelationAsync</c>'s, and only that part needs a live tree to test.
/// </para>
/// </summary>
public static class SchemaTreeReveal
{
    /// <summary>What to reveal: a relation on one connection's database, optionally down to one column.</summary>
    /// <param name="Column">A column of the relation, for go-to-definition on a column reference; null to
    /// stop at the relation itself.</param>
    public sealed record Target(Guid ConnectionId, string Database, string Schema, string Name, string? Column = null)
    {
        /// <summary>How the target reads in a status message: <c>public.payment</c>, or its column.</summary>
        public string Label => Column is null ? $"{Schema}.{Name}" : $"{Schema}.{Name}.{Column}";
    }

    /// <summary>
    /// The tree's server rows, with connection folders (#80) walked through. Folders are built eagerly with
    /// their children, so this needs no I/O — which is what lets a reveal start from a folded-away
    /// connection without expanding anything it does not have to.
    /// </summary>
    public static IEnumerable<SchemaNodeViewModel> Servers(IEnumerable<SchemaNodeViewModel> roots)
    {
        foreach (var node in roots)
        {
            if (node.IsServer) yield return node;
            else if (node.IsFolder)
                foreach (var nested in Servers(node.Children))
                    yield return nested;
        }
    }

    /// <summary>The server row for one connection, or null when the tree has no such connection.</summary>
    public static SchemaNodeViewModel? ServerFor(IEnumerable<SchemaNodeViewModel> roots, Guid connectionId)
        => Servers(roots).FirstOrDefault(s => s.OwningConnection?.Id == connectionId);

    /// <summary>
    /// The database row named <paramref name="database"/> under a loaded server row.
    /// <para>
    /// Matched case-insensitively, but only as a fallback after an exact match: Postgres identifiers are
    /// case-sensitive when quoted, so two databases on one server <em>can</em> differ only in case, and
    /// picking the wrong one would silently reveal a table in the wrong place.
    /// </para>
    /// </summary>
    public static SchemaNodeViewModel? DatabaseUnder(SchemaNodeViewModel server, string database)
    {
        var databases = server.Children.Where(c => c.IsDatabase).ToList();
        return databases.FirstOrDefault(d => Name(d) == database)
            ?? databases.FirstOrDefault(d => string.Equals(Name(d), database, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every relation row under a loaded database row — the ones inline, and the ones inside the collapsed
    /// Views / Functions buckets, which is where a view lives (<c>SchemaGroupNodeViewModel</c>). Buckets
    /// arrive pre-populated, so reaching into one costs nothing and needs no expand.
    /// </summary>
    public static IEnumerable<RelationNodeViewModel> RelationsUnder(SchemaNodeViewModel database)
        => database.Children.SelectMany(Descend).OfType<RelationNodeViewModel>();

    /// <summary>
    /// A node and every relation row beneath it that already exists — through group buckets and through a
    /// schema folder that has been opened, at any depth.
    /// <para>
    /// Recursive because full mode puts relations three levels down (#132): database, Schemas, the schema,
    /// Tables, the row. It deliberately does not descend into a relation (whose children are its columns),
    /// and it forces nothing to load — an unopened schema folder holds only its placeholder, which is why
    /// <c>RevealRelationAsync</c> expands the path itself before asking.
    /// </para>
    /// </summary>
    private static IEnumerable<SchemaNodeViewModel> Descend(SchemaNodeViewModel node)
    {
        yield return node;
        if (node is RelationNodeViewModel) yield break;
        foreach (var child in node.Children)
            foreach (var descendant in Descend(child))
                yield return descendant;
    }

    /// <summary>
    /// The schema folders under a loaded database row — the ones directly under it (full mode with one
    /// schema) and the ones inside the Schemas group.
    /// <para>
    /// <b>Not empty in simple mode</b>, which also builds a Schemas group: simple mode's relations are inline
    /// as well, so a reveal that opened a schema folder there would materialise a second set of rows for
    /// relations already on screen, and leave a folder hanging open that the user never asked for. That is why
    /// <c>RevealRelationAsync</c> looks for the row first and only descends when it is not already there.
    /// </para>
    /// </summary>
    public static IEnumerable<SchemaNodeViewModel> SchemaFoldersUnder(SchemaNodeViewModel database)
        => database.Children
            .SelectMany(c => c is SchemaGroupNodeViewModel group ? group.Children : [c])
            .Where(c => c is SchemaFolderNodeViewModel);

    /// <summary>
    /// The relation row for <paramref name="schema"/>.<paramref name="name"/>, or null.
    /// <para>
    /// Matched on the node's own schema and name rather than on its title, because a title is a label: a
    /// relation in the <c>search_path</c>'s default schema shows as <c>payment</c> and one anywhere else as
    /// <c>reporting.payment</c> (<c>SchemaObjectLabel</c>). Matching titles would find the first and miss
    /// the second.
    /// </para>
    /// </summary>
    public static RelationNodeViewModel? RelationUnder(SchemaNodeViewModel database, string schema, string name)
    {
        var relations = RelationsUnder(database).ToList();
        return relations.FirstOrDefault(r => r.SchemaName == schema && r.RelationName == name)
            ?? relations.FirstOrDefault(r =>
                string.Equals(r.SchemaName, schema, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.RelationName, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The column row named <paramref name="column"/> under a loaded relation row, or null.</summary>
    public static SchemaNodeViewModel? ColumnUnder(SchemaNodeViewModel relation, string column)
    {
        var columns = relation.Children.OfType<ColumnNodeViewModel>().ToList();
        return columns.FirstOrDefault(c => c.Title == column)
            ?? columns.FirstOrDefault(c => string.Equals(c.Title, column, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A database row's own name, which its title is (unlike a relation's).</summary>
    private static string Name(SchemaNodeViewModel database)
        => database is DatabaseNodeViewModel d ? d.Database : database.Title;
}
