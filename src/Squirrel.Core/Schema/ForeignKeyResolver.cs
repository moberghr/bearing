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
/// origin as table id + column ordinal), decides whether a clicked column is a foreign key and,
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

        foreach (var fk in snapshot.ForeignKeysTouching(col.BaseTableId))
        {
            // Only the referencing side navigates, and only when the clicked column is part of it.
            if (fk.ParentTableId != col.BaseTableId || !fk.ParentOrdinals.Contains(col.BaseColumnOrdinal)) continue;

            var refTable = FindTable(snapshot, fk.ReferencedTableId);
            if (refTable is null) continue;
            var refCols = snapshot.ColumnsOf(fk.ReferencedTableId);

            var sourceIndices = new int[fk.ParentOrdinals.Count];
            var refNames = new string[fk.ReferencedOrdinals.Count];
            var complete = true;
            for (var i = 0; i < fk.ParentOrdinals.Count; i++)
            {
                var refName = NameOf(refCols, fk.ReferencedOrdinals[i]);
                var sourceIndex = FindResultColumn(columns, col.BaseTableId, fk.ParentOrdinals[i]);
                if (refName is null || sourceIndex < 0) { complete = false; break; }
                sourceIndices[i] = sourceIndex;
                refNames[i] = refName;
            }
            if (!complete) continue;

            return new ForeignKeyTarget(refTable.Schema, refTable.Name, refNames, sourceIndices);
        }
        return null;
    }

    private static string? NameOf(IReadOnlyList<ColumnInfo> cols, int ordinal)
    {
        foreach (var c in cols)
            if (c.Ordinal == ordinal) return c.Name;
        return null;
    }

    private static TableInfo? FindTable(ISchemaSnapshot snapshot, long tableId)
    {
        foreach (var t in snapshot.Tables)
            if (t.Id == tableId) return t;
        return null;
    }

    /// <summary>Index of the result column whose origin is (<paramref name="tableId"/>, <paramref name="ordinal"/>).</summary>
    private static int FindResultColumn(IReadOnlyList<ColumnDescriptor> columns, long tableId, int ordinal)
    {
        for (var i = 0; i < columns.Count; i++)
            if (columns[i].BaseTableId == tableId && columns[i].BaseColumnOrdinal == ordinal) return i;
        return -1;
    }
}
