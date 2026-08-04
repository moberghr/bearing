using System.Text;
using Bearing.Core.Schema;

namespace Bearing.Sql;

/// <summary>
/// Renders a readable <c>CREATE TABLE</c> for a relation from catalog data already in the schema
/// snapshot — columns (type + NOT NULL), the primary key, and outgoing foreign keys. Postgres has no
/// built-in "give me this table's DDL" function, so we compose it. Identifiers are double-quoted;
/// this is for display/copy, not guaranteed round-trippable (indexes, defaults, checks are omitted).
/// </summary>
public static class TableDdlGenerator
{
    public static string CreateTable(TableInfo table, ISchemaSnapshot snapshot)
    {
        var columns = snapshot.ColumnsOf(table.Id);
        var sb = new StringBuilder();
        sb.Append("create table ").Append(Qualify(table.Schema, table.Name)).Append(" (\n");

        var lines = new List<string>();
        foreach (var c in columns)
            lines.Add($"    {Ident(c.Name)} {c.DataType}{(c.NotNull ? " not null" : "")}");

        var pk = columns.Where(c => c.IsPrimaryKey).Select(c => Ident(c.Name)).ToList();
        if (pk.Count > 0)
            lines.Add($"    primary key ({string.Join(", ", pk)})");

        foreach (var fk in snapshot.ForeignKeysTouching(table.Id))
        {
            if (fk.ParentTableId != table.Id) continue; // outgoing only
            var referenced = snapshot.Tables.FirstOrDefault(t => t.Id == fk.ReferencedTableId);
            if (referenced is null) continue;

            var parentCols = NamesByOrdinal(columns, fk.ParentOrdinals);
            var refCols = NamesByOrdinal(snapshot.ColumnsOf(fk.ReferencedTableId), fk.ReferencedOrdinals);
            lines.Add($"    foreign key ({string.Join(", ", parentCols)}) " +
                      $"references {Qualify(referenced.Schema, referenced.Name)} ({string.Join(", ", refCols)})");
        }

        sb.Append(string.Join(",\n", lines)).Append("\n);\n");
        return sb.ToString();
    }

    private static List<string> NamesByOrdinal(IReadOnlyList<ColumnInfo> columns, IReadOnlyList<int> ordinals)
    {
        var byOrdinal = columns.ToDictionary(c => c.Ordinal, c => c.Name);
        var names = new List<string>(ordinals.Count);
        foreach (var n in ordinals)
            names.Add(Ident(byOrdinal.TryGetValue(n, out var name) ? name : $"?{n}"));
        return names;
    }

    private static string Ident(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";

    private static string Qualify(string? schema, string table) =>
        string.IsNullOrEmpty(schema) ? Ident(table) : $"{Ident(schema)}.{Ident(table)}";
}
