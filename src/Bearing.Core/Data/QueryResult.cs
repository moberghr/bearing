namespace Bearing.Core.Data;

/// <summary>
/// One result column. When the column maps straight to a table column (not an expression/alias),
/// <see cref="BaseTableId"/> + <see cref="BaseColumnOrdinal"/> identify its catalog origin (resolved
/// against the schema snapshot) — the hook for FK navigation and inline edit. Both are 0 for
/// expression/aliased columns, and are populated only for a raw query, not the wrapped paging query.
/// Identity is provider-assigned (the Postgres provider maps table OID + attribute number onto it).
/// </summary>
public sealed record ColumnDescriptor(
    string Name,
    string DataTypeName,
    Type ClrType,
    long BaseTableId = 0,
    int BaseColumnOrdinal = 0)
{
    /// <summary>True when this column maps straight to a catalog table column.</summary>
    public bool HasBaseColumn => BaseTableId != 0 && BaseColumnOrdinal > 0;
}

public sealed record QueryError(string Message, string? SqlState, int? Position);

/// <summary>A fully-materialized query result (small/medium result sets). Large ones stream instead.</summary>
public sealed record QueryResult(
    IReadOnlyList<ColumnDescriptor> Columns,
    IReadOnlyList<object?[]> Rows,
    long RowCount,
    TimeSpan Duration,
    string? Message,
    QueryError? Error,
    bool Truncated)
{
    public bool Success => Error is null;
}

public sealed record QueryOptions
{
    /// <summary>Cap materialized rows (UI grid protection). Null = unlimited.</summary>
    public int? MaxRows { get; init; } = 10_000;
}
