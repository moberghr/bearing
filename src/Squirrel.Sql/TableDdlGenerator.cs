using System.Text;
using Squirrel.Core.Schema;

namespace Squirrel.Sql;

/// <summary>
/// Renders a readable <c>CREATE TABLE</c> for a relation from catalog data already in the schema
/// snapshot — columns (type + NOT NULL), the primary key, and outgoing foreign keys. Postgres has no
/// built-in "give me this table's DDL" function, so we compose it. Identifiers are double-quoted;
/// this is for display/copy, not guaranteed round-trippable (indexes, defaults, checks are omitted).
/// </summary>
public static class TableDdlGenerator
{
    public static string CreateTable(PgTable table, ISchemaSnapshot snapshot)
    {
        var columns = snapshot.ColumnsOf(table.Oid);
        var sb = new StringBuilder();
        sb.Append("create table ").Append(Qualify(table.Schema, table.Name)).Append(" (\n");

        var lines = new List<string>();
        foreach (var c in columns)
            lines.Add($"    {Ident(c.Name)} {c.DataType}{(c.NotNull ? " not null" : "")}");

        var pk = columns.Where(c => c.IsPrimaryKey).Select(c => Ident(c.Name)).ToList();
        if (pk.Count > 0)
            lines.Add($"    primary key ({string.Join(", ", pk)})");

        foreach (var fk in snapshot.ForeignKeysTouching(table.Oid))
        {
            if (fk.ParentOid != table.Oid) continue; // outgoing only
            var referenced = snapshot.Tables.FirstOrDefault(t => t.Oid == fk.ReferencedOid);
            if (referenced is null) continue;

            var parentCols = NamesByAttNum(columns, fk.ParentAttNums);
            var refCols = NamesByAttNum(snapshot.ColumnsOf(fk.ReferencedOid), fk.ReferencedAttNums);
            lines.Add($"    foreign key ({string.Join(", ", parentCols)}) " +
                      $"references {Qualify(referenced.Schema, referenced.Name)} ({string.Join(", ", refCols)})");
        }

        sb.Append(string.Join(",\n", lines)).Append("\n);\n");
        return sb.ToString();
    }

    private static List<string> NamesByAttNum(IReadOnlyList<PgColumn> columns, IReadOnlyList<short> attNums)
    {
        var byNum = columns.ToDictionary(c => c.AttNum, c => c.Name);
        var names = new List<string>(attNums.Count);
        foreach (var n in attNums)
            names.Add(Ident(byNum.TryGetValue(n, out var name) ? name : $"?{n}"));
        return names;
    }

    private static string Ident(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";

    private static string Qualify(string? schema, string table) =>
        string.IsNullOrEmpty(schema) ? Ident(table) : $"{Ident(schema)}.{Ident(table)}";
}
