using Bearing.Core.Data;

namespace Bearing.Core.Schema;

/// <summary>One result column that maps to a base-table column, with its real name, PK flag and whether the
/// catalog declares it NOT NULL (which is what stops the grid offering NULL as an editable value).</summary>
public sealed record EditableColumn(int ResultIndex, string BaseColumn, bool IsPrimaryKey, bool NotNull = false);

/// <summary>
/// The table a single-table result set edits, plus the base name + PK flag of each result column.
/// Column names are the catalog names (not result aliases), so generated DML targets the right columns.
/// </summary>
public sealed record EditTarget(string Schema, string Table, IReadOnlyList<EditableColumn> Columns)
{
    public IEnumerable<EditableColumn> KeyColumns => Columns.Where(c => c.IsPrimaryKey);

    /// <summary>Whether a result column may hold NULL. Permissive when the column isn't mapped: the answer
    /// only ever narrows what the UI offers, so "don't know" must not forbid a legal value.</summary>
    public bool AllowsNull(int resultIndex)
        => Columns.FirstOrDefault(c => c.ResultIndex == resultIndex) is not { NotNull: true };
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

        // All columns must originate from the same base table — but they arrive carrying either origin
        // form (see ColumnDescriptor): Postgres hands over catalog ids, SqlClient only ever names. Ids
        // compare directly; names compare case-insensitively, because SQL Server's default collation is
        // case-insensitive and the snapshot's own lookups are case-folded. A batch that mixes the two
        // agrees only if both forms resolve to the same relation, which is checked below — never assumed.
        long tableId = 0;
        string? namedSchema = null, namedTable = null, namedCatalog = null;
        var byName = false;
        foreach (var c in columns)
        {
            if (!c.HasBaseColumn)                               // expression/aliased column ⇒ not a clean single table
                return (null, "a column is a computed expression, not a table column.");
            if (HasIdOrigin(c))
            {
                if (tableId == 0) tableId = c.BaseTableId;
                else if (c.BaseTableId != tableId)              // spans multiple tables (join)
                    return (null, "the result joins more than one table.");
            }
            else
            {
                if (!byName)
                {
                    byName = true;
                    namedSchema = c.BaseSchemaName;
                    namedTable = c.BaseTableName;
                    namedCatalog = c.BaseCatalogName;
                }
                else if (!Same(namedSchema, c.BaseSchemaName) || !Same(namedTable, c.BaseTableName)
                         || !Same(namedCatalog, c.BaseCatalogName))
                    return (null, "the result joins more than one table.");
            }
        }

        TableInfo? table = tableId == 0 ? null : FindTable(snapshot, tableId);
        if (tableId != 0 && table is null) return (null, "the source table isn't in the loaded schema.");
        if (byName)
        {
            // A name origin is only unique *within* a database. T-SQL reaches another one with a
            // three-part name (`select * from reporting.dbo.Orders` on a connection whose database is
            // `app`), and Postgres has no analogue — so before trusting the name, check it belongs to the
            // database this snapshot describes. Without it, identical schemas across databases on one
            // instance (the norm) resolved the foreign table to the local one of the same name, marked the
            // grid editable, and generated an UPDATE that ran against the connected database. The confirm
            // dialog showed the same `[dbo].[Orders]` either way, so §1.2's guard could not expose it.
            if (namedCatalog is { Length: > 0 }
                && !namedCatalog.Equals(snapshot.Database, StringComparison.OrdinalIgnoreCase))
                return (null, "the source table is in another database.");

            var named = snapshot.ResolveTable(namedSchema, namedTable!);
            if (named is null) return (null, "the source table isn't in the loaded schema.");
            // Mixed forms: they describe one result set only if they land on the same relation. Anything
            // else is two tables however it was spelled, so it takes the join reason rather than a guess.
            if (table is not null && named.Id != table.Id)
                return (null, "the result joins more than one table.");
            table ??= named;
        }

        if (table!.Kind is not (RelationKind.Table or RelationKind.Partitioned)) // views etc. aren't editable
            return (null, "the result comes from a view, not a table.");

        var catalog = snapshot.ColumnsOf(table.Id);
        var pkOrdinals = catalog.Where(c => c.IsPrimaryKey).Select(c => c.Ordinal).ToHashSet();
        if (pkOrdinals.Count == 0)                             // no detectable PK ⇒ can't key edits
            return (null, "no primary key found — can't generate a safe UPDATE.");

        var mapped = new List<EditableColumn>(columns.Count);
        var present = new HashSet<int>();
        for (var i = 0; i < columns.Count; i++)
        {
            // The catalog's own ordinal is what the rest of this method keys on, so a name-origin column
            // is resolved to its ColumnInfo first and then treated exactly like an id-origin one — the
            // EditTarget it produces is identical in shape, and the DML still uses catalog names.
            var info = HasIdOrigin(columns[i])
                ? ColumnAt(catalog, columns[i].BaseColumnOrdinal)
                : ColumnNamed(catalog, columns[i].BaseColumnName!);
            if (info is null)                                  // column not in the snapshot ⇒ bail
                return (null, "a column isn't in the loaded schema.");
            mapped.Add(new EditableColumn(i, info.Name, pkOrdinals.Contains(info.Ordinal), info.NotNull));
            present.Add(info.Ordinal);
        }

        if (!pkOrdinals.All(present.Contains))                 // a PK column is missing from the result
            return (null, "a primary-key column is missing from the result.");

        return (new EditTarget(table.Schema, table.Name, mapped), null);
    }

    /// <summary>Whether this column carries the id origin form. Checked rather than inferred from
    /// <c>BaseTableId</c> alone: an id without an ordinal is not usable, and the column may still carry a
    /// usable name origin, so the two loops below must agree on which form they are reading.</summary>
    private static bool HasIdOrigin(ColumnDescriptor c) => c.BaseTableId != 0 && c.BaseColumnOrdinal > 0;

    /// <summary>Name equality for catalog identifiers arriving from a name-origin column.</summary>
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static ColumnInfo? ColumnNamed(IReadOnlyList<ColumnInfo> cols, string name)
    {
        foreach (var c in cols)
            if (Same(c.Name, name)) return c;
        return null;
    }

    private static ColumnInfo? ColumnAt(IReadOnlyList<ColumnInfo> cols, int ordinal)
    {
        foreach (var c in cols)
            if (c.Ordinal == ordinal) return c;
        return null;
    }

    private static TableInfo? FindTable(ISchemaSnapshot snapshot, long tableId)
    {
        foreach (var t in snapshot.Tables)
            if (t.Id == tableId) return t;
        return null;
    }
}
