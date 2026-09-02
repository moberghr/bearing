using System.Text;
using Bearing.Core.Schema;

namespace Bearing.Sql;

/// <summary>
/// Renders a readable <c>CREATE TABLE</c> for a relation from catalog data already in the schema
/// snapshot — columns (type + NOT NULL), the primary key, and outgoing foreign keys. Neither engine has
/// a built-in "give me this table's DDL" function, so we compose it. Identifiers are quoted by the
/// dialect; this is for display/copy, not guaranteed round-trippable (indexes, defaults, checks are
/// omitted).
/// </summary>
public static class TableDdlGenerator
{
    /// <summary>The Postgres rendering — the default for a caller with no connection in hand.</summary>
    public static string CreateTable(TableInfo table, ISchemaSnapshot snapshot)
        => CreateTable(PostgresDialect.Instance, table, snapshot);

    /// <inheritdoc cref="CreateTable(TableInfo, ISchemaSnapshot)"/>
    public static string CreateTable(ISqlDialect dialect, TableInfo table, ISchemaSnapshot snapshot)
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

        sb.Append(string.Join(",\n", lines)).Append("\n);\n");
        return sb.ToString();
    }

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
    private static string Ident(ISqlDialect dialect, string id) => dialect.Quote(id);

    private static string Qualify(ISqlDialect dialect, string? schema, string table) =>
        string.IsNullOrEmpty(schema) ? Ident(dialect, table) : $"{Ident(dialect, schema)}.{Ident(dialect, table)}";
}
