using System.Text;
using Bearing.Core.Schema;

namespace Bearing.Sql;

/// <summary>
/// Renders a readable <c>CREATE TABLE</c> for a relation. Postgres has no built-in "give me this table's
/// DDL" function, so we compose it: columns (type + NOT NULL), the primary key and outgoing foreign keys
/// from the schema snapshot, plus — when a <see cref="TableDetails"/> read is supplied — the table's
/// check/unique/exclusion constraints and a <c>CREATE INDEX</c> per index (#46).
/// <para>
/// Identifiers are quoted by the connection's dialect, so a SQL Server table reads as
/// <c>[dbo].[Orders]</c>. Still for display and copy rather than guaranteed
/// round-trippable: column defaults, identity/generated columns, storage parameters, partitioning and
/// inheritance are not read, and a constraint or index comes out in the server's own rendering rather than
/// one this generator controls.
/// </para>
/// </summary>
public static class TableDdlGenerator
{
    /// <param name="details">
    /// Constraints, indexes and triggers as read on demand, or null when they were not read (a caller with no
    /// server, or a read that failed). Null yields the columns-and-keys DDL this used to produce, which is
    /// worth showing on its own — an empty <see cref="TableDetails"/> means "read, and there are none", and
    /// the two must not be conflated.
    /// </param>
    public static string CreateTable(TableInfo table, ISchemaSnapshot snapshot, TableDetails? details = null)
        => CreateTable(PostgresDialect.Instance, table, snapshot, details);

    /// <inheritdoc cref="CreateTable(TableInfo, ISchemaSnapshot, TableDetails?)"/>
    public static string CreateTable(
        ISqlDialect dialect, TableInfo table, ISchemaSnapshot snapshot, TableDetails? details = null)
    {
        var columns = snapshot.ColumnsOf(table.Id);
        var sb = new StringBuilder();
        sb.Append("create table ").Append(Qualify(dialect, table.Schema, table.Name)).Append(" (\n");

        var lines = new List<string>();
        foreach (var c in columns)
            lines.Add($"    {Ident(dialect, c.Name)} {c.DataType}{(c.NotNull ? " not null" : "")}");

        var pk = columns.Where(c => c.IsPrimaryKey).Select(c => Ident(dialect, c.Name)).ToList();
        if (pk.Count > 0)
            lines.Add($"    primary key ({string.Join(", ", pk)})");

        foreach (var fk in snapshot.ForeignKeysTouching(table.Id))
        {
            if (fk.ParentTableId != table.Id) continue; // outgoing only
            var referenced = snapshot.Tables.FirstOrDefault(t => t.Id == fk.ReferencedTableId);
            if (referenced is null) continue;

            var parentCols = NamesByOrdinal(dialect, columns, fk.ParentOrdinals);
            var refCols = NamesByOrdinal(dialect, snapshot.ColumnsOf(fk.ReferencedTableId), fk.ReferencedOrdinals);
            lines.Add($"    foreign key ({string.Join(", ", parentCols)}) " +
                      $"references {Qualify(dialect, referenced.Schema, referenced.Name)} ({string.Join(", ", refCols)})");
        }

        // Check / unique / exclusion constraints, in the server's own words: a CHECK body cannot be rebuilt
        // from catalog columns, so this is the only rendering that is actually the table's.
        foreach (var constraint in Inline(details))
            lines.Add($"    constraint {Ident(dialect, constraint.Name)} {constraint.Definition}");

        sb.Append(string.Join(",\n", lines)).Append("\n);\n");

        // Indexes follow the table rather than sitting inside it, because that is where they go in SQL — and
        // the ones backing a primary key or a unique constraint are left out: the constraint above already
        // creates them, and re-issuing them would fail.
        foreach (var index in details?.Indexes ?? [])
        {
            // Skipped by *ownership*, not by shape: an index a constraint created is issued by that
            // constraint, so emitting it again fails on the name. Comparing column sets instead dropped
            // genuinely separate indexes that happened to cover the same columns — a reordered unique index,
            // or one differing only in its INCLUDE payload — and kept the index backing an exclusion
            // constraint, which is neither primary nor unique and so slipped past every other test.
            if (index.BackedByConstraint || index.IsPrimary || index.Definition.Length == 0) continue;
            sb.Append(index.Definition.TrimEnd().TrimEnd(';')).Append(";\n");
        }

        return sb.ToString();
    }

    /// <summary>The constraints that belong inside the <c>CREATE TABLE</c> body: everything except the primary
    /// key and the foreign keys, which are already rendered from the snapshot above.</summary>
    private static IEnumerable<ConstraintInfo> Inline(TableDetails? details)
        => (details?.Constraints ?? [])
            .Where(c => c.Kind is ConstraintKind.Unique or ConstraintKind.Check or ConstraintKind.Exclusion)
            .Where(c => c.Definition.Length > 0);


    private static List<string> NamesByOrdinal(
        ISqlDialect dialect, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<int> ordinals)
    {
        var byOrdinal = columns.ToDictionary(c => c.Ordinal, c => c.Name);
        var names = new List<string>(ordinals.Count);
        foreach (var n in ordinals)
            names.Add(Ident(dialect, byOrdinal.TryGetValue(n, out var name) ? name : $"?{n}"));
        return names;
    }

    /// <summary>Generated DDL always quotes — nobody types over this output, so the safe form wins.</summary>
    /// <summary>Generated DDL always quotes — nobody types over this output, so the safe form
    /// wins — and the dialect decides what quoting means.</summary>
    private static string Ident(ISqlDialect dialect, string id) => dialect.Quote(id);

    private static string Qualify(ISqlDialect dialect, string? schema, string table) =>
        string.IsNullOrEmpty(schema)
            ? Ident(dialect, table)
            : $"{Ident(dialect, schema)}.{Ident(dialect, table)}";
}
