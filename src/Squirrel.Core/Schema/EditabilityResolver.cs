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
        => ResolveWithReason(snapshot, columns).Target;

    /// <summary>
    /// Like <see cref="Resolve"/> but, when the result is NOT editable, also returns a short
    /// human-readable reason (for the read-only lock affordance). <c>Reason</c> is null when editable.
    /// </summary>
    public static (EditTarget? Target, string? Reason) ResolveWithReason(
        ISchemaSnapshot snapshot, IReadOnlyList<ColumnDescriptor> columns)
    {
        if (columns.Count == 0) return (null, "no columns to edit.");

        // All columns must originate from the same base table.
        uint oid = 0;
        foreach (var c in columns)
        {
            if (!c.HasBaseColumn)                               // expression/aliased column ⇒ not a clean single table
                return (null, "a column is a computed expression, not a table column.");
            if (oid == 0) oid = c.BaseTableOid;
            else if (c.BaseTableOid != oid)                     // spans multiple tables (join)
                return (null, "the result joins more than one table.");
        }

        var table = FindTable(snapshot, oid);
        if (table is null) return (null, "the source table isn't in the loaded schema.");
        if (table.Kind is not (PgRelKind.Table or PgRelKind.Partitioned)) // views etc. aren't editable
            return (null, "the result comes from a view, not a table.");

        var catalog = snapshot.ColumnsOf(oid);
        var pkAttNums = catalog.Where(c => c.IsPrimaryKey).Select(c => c.AttNum).ToHashSet();
        if (pkAttNums.Count == 0)                              // no detectable PK ⇒ can't key edits
            return (null, "no primary key found — can't generate a safe UPDATE.");

        var mapped = new List<EditableColumn>(columns.Count);
        var present = new HashSet<short>();
        for (var i = 0; i < columns.Count; i++)
        {
            var att = columns[i].BaseColumnAttNum;
            var name = NameOf(catalog, att);
            if (name is null)                                  // column not in the snapshot ⇒ bail
                return (null, "a column isn't in the loaded schema.");
            mapped.Add(new EditableColumn(i, name, pkAttNums.Contains(att)));
            present.Add(att);
        }

        if (!pkAttNums.All(present.Contains))                  // a PK column is missing from the result
            return (null, "a primary-key column is missing from the result.");

        return (new EditTarget(table.Schema, table.Name, mapped), null);
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
