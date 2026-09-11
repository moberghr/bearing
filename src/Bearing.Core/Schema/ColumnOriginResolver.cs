using Bearing.Core.Data;

namespace Bearing.Core.Schema;

/// <summary>Where one result column comes from, once both origin forms have been reduced to the same
/// catalog pair. <see cref="Column"/>'s <c>Ordinal</c> is the catalog's, which is what every downstream
/// lookup (FK ordinals, PK sets, generated DML) keys on.</summary>
public sealed record ColumnOrigin(TableInfo Table, ColumnInfo Column);

/// <summary>
/// Resolves a result column's catalog origin from <b>either</b> form <see cref="ColumnDescriptor"/> carries
/// — Postgres' table id + attribute number, or SqlClient's schema/table/column names.
/// <para>
/// This exists because the id form was treated as the only one. <c>SqlServerQueryExecutor</c> leaves
/// <c>BaseTableId</c>/<c>BaseColumnOrdinal</c> at 0 and populates only names — there is no id to be had from
/// <c>SqlDataReader</c> — and <see cref="ColumnDescriptor.HasBaseColumn"/> accepts that. But the callers that
/// then read <c>BaseTableId</c> directly were asking the snapshot about table 0 and matching ordinal 0, so
/// on SQL Server every FK badge, every ↗ navigation and every PK badge was silently absent on results the
/// grid was perfectly happy to edit. Absent, not wrong: nothing failed, the affordances just were not there.
/// </para>
/// <para>
/// Pure and snapshot-scoped, and it refuses a name origin from another database for
/// <see cref="EditabilityResolver"/>'s reason: a name is unique only <em>within</em> a database, so a
/// three-part <c>reporting.dbo.Orders</c> on a connection whose database is <c>app</c> must not resolve to
/// the local table of the same name. <see cref="EditabilityResolver"/> keeps its own pass rather than
/// calling this one — it has to diagnose <em>why</em> a set is not editable and to reconcile the two forms
/// across a whole result set, which is a different question from "what is this one column".
/// </para>
/// </summary>
public static class ColumnOriginResolver
{
    /// <summary>
    /// Every column's origin, in result order, nulls for the ones that have none.
    /// <para>
    /// The form to reach for when a caller needs more than one, which is every caller that decides an
    /// affordance for a whole result set. Resolving inside the per-column loop instead made the FK pass
    /// re-resolve all of them once per column per foreign key — and a name origin costs a case-folded
    /// catalog lookup, not a field compare.
    /// </para>
    /// </summary>
    public static ColumnOrigin?[] ResolveAll(ISchemaSnapshot snapshot, IReadOnlyList<ColumnDescriptor> columns)
    {
        var origins = new ColumnOrigin?[columns.Count];
        for (var i = 0; i < columns.Count; i++) origins[i] = Resolve(snapshot, columns[i]);
        return origins;
    }

    /// <summary>The catalog table + column behind <paramref name="column"/>, or null when it is an
    /// expression, is not in the snapshot, or names a table in another database.</summary>
    public static ColumnOrigin? Resolve(ISchemaSnapshot snapshot, ColumnDescriptor column)
    {
        if (!column.HasBaseColumn) return null;

        if (column.BaseTableId != 0 && column.BaseColumnOrdinal > 0)
        {
            var byId = snapshot.TableById(column.BaseTableId);
            if (byId is null) return null;
            var at = ColumnAt(snapshot.ColumnsOf(byId.Id), column.BaseColumnOrdinal);
            return at is null ? null : new ColumnOrigin(byId, at);
        }

        if (column.BaseCatalogName is { Length: > 0 } catalog
            && !catalog.Equals(snapshot.Database, StringComparison.OrdinalIgnoreCase))
            return null;

        var byName = snapshot.ResolveTable(column.BaseSchemaName, column.BaseTableName!);
        if (byName is null) return null;
        var named = ColumnNamed(snapshot.ColumnsOf(byName.Id), column.BaseColumnName!);
        return named is null ? null : new ColumnOrigin(byName, named);
    }

    private static ColumnInfo? ColumnAt(IReadOnlyList<ColumnInfo> cols, int ordinal)
    {
        foreach (var c in cols)
            if (c.Ordinal == ordinal) return c;
        return null;
    }

    /// <summary>Case-insensitively, as the snapshot's own name lookups are: SQL Server's default collation
    /// is case-insensitive, and the name arrives spelled however the query spelled it.</summary>
    private static ColumnInfo? ColumnNamed(IReadOnlyList<ColumnInfo> cols, string name)
    {
        foreach (var c in cols)
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }
}
