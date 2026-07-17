namespace Squirrel.Core.Data;

/// <summary>
/// One result column. When the column maps straight to a table column (not an expression/alias),
/// <see cref="BaseTableOid"/> + <see cref="BaseColumnAttNum"/> identify its catalog origin (resolved
/// against the schema snapshot) — the hook for FK navigation and inline edit. Both are 0 for
/// expression/aliased columns, and are populated only for a raw query, not the wrapped paging query.
/// </summary>
public sealed record ColumnDescriptor(
    string Name,
    string DataTypeName,
    Type ClrType,
    uint BaseTableOid = 0,
    short BaseColumnAttNum = 0)
{
    /// <summary>True when this column maps straight to a catalog table column.</summary>
    public bool HasBaseColumn => BaseTableOid != 0 && BaseColumnAttNum > 0;
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

/// <summary>A chunk of rows from a streaming execution.</summary>
public sealed record ResultBatch(IReadOnlyList<ColumnDescriptor> Columns, IReadOnlyList<object?[]> Rows);

public sealed record QueryOptions
{
    /// <summary>Cap materialized rows (UI grid protection). Null = unlimited.</summary>
    public int? MaxRows { get; init; } = 10_000;
}
