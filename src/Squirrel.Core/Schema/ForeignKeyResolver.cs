using Squirrel.Core.Data;

namespace Squirrel.Core.Schema;

/// <summary>
/// Where a foreign-key cell navigates to: the referenced table plus, per referenced key column,
/// the index in the source result row that supplies the matching value. Parallel lists —
/// <c>RefColumns[i]</c> is matched against <c>row[SourceColumnIndices[i]]</c>.
/// </summary>
public sealed record ForeignKeyTarget(
    string RefSchema,
    string RefTable,
    IReadOnlyList<string> RefColumns,
    IReadOnlyList<int> SourceColumnIndices);

/// <summary>
/// Pure resolver: given a schema snapshot and a result set's columns (each carrying its catalog
/// origin as table OID + attribute number), decides whether a clicked column is a foreign key and,
/// if so, how to build the lookup against the referenced table. No I/O — the caller supplies the
/// row values and runs the query.
/// </summary>
public static class ForeignKeyResolver
{
    /// <summary>
    /// Resolve the FK target for <paramref name="clickedColumn"/>, or null when that column has no
    /// catalog origin, isn't the referencing side of any FK, or the FK's other key columns aren't
    /// all present in the result (composite keys need every part in the same row).
    /// </summary>
    public static ForeignKeyTarget? Resolve(
        ISchemaSnapshot snapshot, IReadOnlyList<ColumnDescriptor> columns, int clickedColumn)
    {
        if (clickedColumn < 0 || clickedColumn >= columns.Count) return null;
        var col = columns[clickedColumn];
        if (!col.HasBaseColumn) return null;

        foreach (var fk in snapshot.ForeignKeysTouching(col.BaseTableOid))
        {
            // Only the referencing side navigates, and only when the clicked column is part of it.
            if (fk.ParentOid != col.BaseTableOid || !fk.ParentAttNums.Contains(col.BaseColumnAttNum)) continue;

            var refTable = FindTable(snapshot, fk.ReferencedOid);
            if (refTable is null) continue;
            var refCols = snapshot.ColumnsOf(fk.ReferencedOid);

            var sourceIndices = new int[fk.ParentAttNums.Count];
            var refNames = new string[fk.ReferencedAttNums.Count];
            var complete = true;
            for (var i = 0; i < fk.ParentAttNums.Count; i++)
            {
                var refName = NameOf(refCols, fk.ReferencedAttNums[i]);
                var sourceIndex = FindResultColumn(columns, col.BaseTableOid, fk.ParentAttNums[i]);
                if (refName is null || sourceIndex < 0) { complete = false; break; }
                sourceIndices[i] = sourceIndex;
                refNames[i] = refName;
            }
            if (!complete) continue;

            return new ForeignKeyTarget(refTable.Schema, refTable.Name, refNames, sourceIndices);
        }
        return null;
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

    /// <summary>Index of the result column whose origin is (<paramref name="tableOid"/>, <paramref name="attNum"/>).</summary>
    private static int FindResultColumn(IReadOnlyList<ColumnDescriptor> columns, uint tableOid, short attNum)
    {
        for (var i = 0; i < columns.Count; i++)
            if (columns[i].BaseTableOid == tableOid && columns[i].BaseColumnAttNum == attNum) return i;
        return -1;
    }
}
