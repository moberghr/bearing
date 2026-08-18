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

/// <summary>
/// One chunk of a streamed result (<see cref="IQueryExecutor.StreamRowsAsync"/>). Batches arrive in row
/// order and are meant to be appended as they land, so a long read shows progress instead of one jump at the
/// end. <see cref="Truncated"/> is set only on the final batch, and only when the read stopped at
/// <see cref="QueryOptions.MaxRows"/> with rows still waiting on the server — that is how a caller tells
/// "this is the whole result" from "this is where the ceiling cut it", without a second query.
/// </summary>
public sealed record RowBatch(IReadOnlyList<object?[]> Rows, bool Truncated);

public sealed record QueryOptions
{
    /// <summary>Cap materialized rows (UI grid protection). Null = unlimited.</summary>
    public int? MaxRows { get; init; } = 10_000;

    /// <summary>Rows per batch when streaming (<see cref="IQueryExecutor.StreamRowsAsync"/>); ignored by the
    /// materializing paths. Sets how often a long read reports progress, nothing else.</summary>
    public int BatchRows { get; init; } = 1_000;
}
