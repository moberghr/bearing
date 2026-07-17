namespace Squirrel.Core.Logging;

/// <summary>One executed statement, recorded regardless of whether its script was ever saved.</summary>
public sealed record QueryLogEntry
{
    public long Id { get; init; }
    public required DateTimeOffset ExecutedAt { get; init; }
    public required string ProviderId { get; init; }
    public required string ConnectionName { get; init; }
    public required string Database { get; init; }
    public required string SqlText { get; init; }
    public TimeSpan Duration { get; init; }
    public long RowCount { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Relative script path if the SQL came from a saved script, else null (scratch).</summary>
    public string? ScriptPath { get; init; }
}

/// <summary>Free-text + structured search over the history.</summary>
public sealed record QueryLogQuery
{
    /// <summary>FTS match over the SQL text (null = no text filter).</summary>
    public string? Text { get; init; }
    public string? ConnectionName { get; init; }
    public bool? SuccessOnly { get; init; }
    public int Limit { get; init; } = 200;
}

/// <summary>Append-only, searchable log of every executed query.</summary>
public interface IQueryLog
{
    /// <summary>Record an execution. Never throws to the caller and never blocks the results grid.</summary>
    void Append(QueryLogEntry entry);

    Task<IReadOnlyList<QueryLogEntry>> SearchAsync(QueryLogQuery query, CancellationToken ct);
}
