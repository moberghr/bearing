namespace Bearing.Core.Logging;

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

    /// <summary>
    /// Raised once an appended entry is actually in the log and readable by <see cref="SearchAsync"/>.
    /// <para>
    /// Separate from <see cref="Append"/> returning, because it does not: appending hands the entry to a
    /// writer and returns immediately, so anything that re-reads the log on the strength of an
    /// <c>Append</c> call races the insert and usually misses the row it refreshed for (#78). This fires on
    /// the writer's own thread — a UI subscriber has to marshal.
    /// </para>
    /// </summary>
    event Action<QueryLogEntry>? Appended;

    Task<IReadOnlyList<QueryLogEntry>> SearchAsync(QueryLogQuery query, CancellationToken ct);
}
