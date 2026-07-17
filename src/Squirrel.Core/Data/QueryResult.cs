namespace Squirrel.Core.Data;

public sealed record ColumnDescriptor(string Name, string DataTypeName, Type ClrType);

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
