namespace Bearing.Core.Logging;

/// <summary>One executed statement, recorded regardless of whether its script was ever saved.</summary>
public sealed record QueryLogEntry
{
    public long Id { get; init; }
    public required DateTimeOffset ExecutedAt { get; init; }
    public required string ProviderId { get; init; }
    public required string ConnectionName { get; init; }

    /// <summary>
    /// Which connection this ran against, by id (#113). Null for a row written before the log carried one —
    /// the log stored the display <em>name</em> only until then, so a historical row can be matched to a
    /// connection by name or not at all, and an audit report must say which of the two it did.
    /// </summary>
    public Guid? ConnectionId { get; init; }

    /// <summary>
    /// The connection's environment label as it was at execution time (#113), or null when unknown — either
    /// a row written before the column existed, or a connection with no environment set. Stored rather than
    /// only resolved later because it is the answer to "what ran against production this week", and a
    /// connection that has since been re-pointed at staging must not rewrite last week's history.
    /// </summary>
    public string? Environment { get; init; }
    public required string Database { get; init; }
    public required string SqlText { get; init; }
    public TimeSpan Duration { get; init; }
    public long RowCount { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Relative script path if the SQL came from a saved script, else null (scratch).</summary>
    public string? ScriptPath { get; init; }

    /// <summary>
    /// What ran this — <c>null</c> for Bearing itself, or the name of the host that did
    /// (<see cref="QueryOrigin.Cli"/>). §1.11's external path writes its own rows here, so "what ran
    /// against production last week" includes what an agent ran and says which.
    /// <para>
    /// <b>A null is not ambiguous here, unlike <see cref="ConnectionId"/>'s.</b> Every row written before
    /// this column existed came from the editor, because nothing else could write to the log until the
    /// column and the second host arrived together. So null means the app, full stop, and a report does not
    /// have to hedge about historical rows the way #113's does.
    /// </para>
    /// </summary>
    public string? Origin { get; init; }

    /// <summary>
    /// The manual-commit transaction this ran inside, or null when it committed itself (#131). An opaque
    /// per-transaction id, not anything the server knows: what it is for is tying a statement to the
    /// <c>commit</c> or <c>rollback</c> that later ended it, both of which are logged as entries of their
    /// own carrying the same value.
    /// <para>
    /// Null on every row written before v3, and on every auto-commit row since — which is not the same
    /// thing, and is why a report says how many rows it could not place rather than calling them
    /// uncommitted.
    /// </para>
    /// </summary>
    public string? TransactionId { get; init; }
}

/// <summary>Free-text + structured search over the history.</summary>
public sealed record QueryLogQuery
{
    /// <summary>FTS match over the SQL text (null = no text filter).</summary>
    public string? Text { get; init; }
    public string? ConnectionName { get; init; }
    public bool? SuccessOnly { get; init; }

    /// <summary>Earliest execution time to include, inclusive (null = no lower bound). Added for the audit
    /// export (#113), whose whole question is "what ran between these two dates".</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Latest execution time to include, inclusive (null = no upper bound).</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>
    /// Most rows to return. The history panel wants a screenful; an audit report wants the period, however
    /// many rows that is, so <c>null</c> means unbounded. Defaulted rather than nullable-by-default so no
    /// existing caller silently becomes an unbounded read.
    /// </summary>
    public int? Limit { get; init; } = 200;
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

/// <summary>The hosts that write to the query log, for <see cref="QueryLogEntry.Origin"/>. Bearing itself
/// writes null rather than a name of its own: it is the overwhelming majority of rows, and a column that is
/// null for "us" and set for "not us" reads correctly at a glance in a history list.</summary>
public static class QueryOrigin
{
    /// <summary>The `bearing` command (§1.11).</summary>
    public const string Cli = "cli";
}
