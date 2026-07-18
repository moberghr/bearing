using Squirrel.Core.Data;

namespace Squirrel.Core.Schema;

/// <summary>One result column that maps to a base-table column, with its real name + PK flag.</summary>
public sealed record EditableColumn(int ResultIndex, string BaseColumn, bool IsPrimaryKey);

/// <summary>
/// The table a single-table result set edits, plus the base name + PK flag of each result column.
/// Column names are the catalog names (not result aliases), so generated DML targets the right columns.
/// </summary>
public sealed record EditTarget(string Schema, string Table, IReadOnlyList<EditableColumn> Columns)
{
    public IEnumerable<EditableColumn> KeyColumns => Columns.Where(c => c.IsPrimaryKey);
}

/// <summary>
/// Decides whether a result set is inline-editable: every column must map to the same real table
/// (not a view/expression), that table must have a primary key, and every PK column must be present
/// in the result (so UPDATE/DELETE can key on it). Pure — no I/O.
/// </summary>
public static class EditabilityResolver
{
    public static EditTarget? Resolve(ISchemaSnapshot snapshot, IReadOnlyList<ColumnDescriptor> columns)
    {
        if (columns.Count == 0) return null;

        // All columns must originate from the same base table.
        uint oid = 0;
        foreach (var c in columns)
        {
            if (!c.HasBaseColumn) return null;                 // expression/aliased column ⇒ not a clean single table
            if (oid == 0) oid = c.BaseTableOid;
            else if (c.BaseTableOid != oid) return null;       // spans multiple tables (join)
        }

        var table = FindTable(snapshot, oid);
        if (table is null) return null;
        if (table.Kind is not (PgRelKind.Table or PgRelKind.Partitioned)) return null; // views etc. aren't editable

        var catalog = snapshot.ColumnsOf(oid);
        var pkAttNums = catalog.Where(c => c.IsPrimaryKey).Select(c => c.AttNum).ToHashSet();
        if (pkAttNums.Count == 0) return null;                 // no detectable PK ⇒ can't key edits

        var mapped = new List<EditableColumn>(columns.Count);
        var present = new HashSet<short>();
        for (var i = 0; i < columns.Count; i++)
        {
            var att = columns[i].BaseColumnAttNum;
            var name = NameOf(catalog, att);
            if (name is null) return null;                     // column not in the snapshot ⇒ bail
            mapped.Add(new EditableColumn(i, name, pkAttNums.Contains(att)));
            present.Add(att);
        }

        if (!pkAttNums.All(present.Contains)) return null;     // a PK column is missing from the result

        return new EditTarget(table.Schema, table.Name, mapped);
    }

    private static string? NameOf(IReadOnlyList<PgColumn> cols, short attNum)
    {
        foreach (var c in cols)
            if (c.AttNum == attNum) return c.Name;
        return null;
    }

    private static PgTable? FindTable(ISchemaSnapshot snapshot, uint oid)
    {
        foreach (var t in snapshot.Tables)
            if (t.Oid == oid) return t;
        return null;
    }
}
