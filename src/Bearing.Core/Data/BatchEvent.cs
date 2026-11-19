namespace Bearing.Core.Data;

/// <summary>
/// What <see cref="IQueryExecutor.ExecuteStreamingAsync"/> reports while a batch is read: a result set
/// beginning, rows for it, and finally the whole run. A batch is read once, to the end (§5.8) — streaming
/// changes when its rows become visible, not what is read.
/// <para>
/// <see cref="ResultStarted.Index"/> and <see cref="ResultRows.Index"/> are positions in
/// <see cref="BatchCompleted.Results"/>, the same list <see cref="IQueryExecutor.ExecuteAsync"/> returns for
/// the same text, and the rows are the same <c>object?[]</c> instances that list holds — so a consumer that
/// built grids from the events can take everything else (messages, statement attribution, the final
/// <see cref="QueryResult.Truncated"/>) from the completion without copying a row.
/// </para>
/// </summary>
public abstract record BatchEvent;

/// <summary>Result set <see cref="Index"/> has begun and returns rows of this shape. Results with no shape (an
/// UPDATE's count, a DDL) are never started; they appear only in <see cref="BatchCompleted"/>.</summary>
public sealed record ResultStarted(int Index, IReadOnlyList<ColumnDescriptor> Columns) : BatchEvent;

/// <summary>The next rows of result set <see cref="Index"/>, in order.</summary>
public sealed record ResultRows(int Index, IReadOnlyList<object?[]> Rows) : BatchEvent;

/// <summary>Result set <see cref="Index"/> has been read as far as it will be: to its end, or — when
/// <see cref="Truncated"/> — to <see cref="QueryOptions.MaxRows"/> with rows still on the server. A set that
/// was started and never ended was cut off by an error or a cancellation.</summary>
public sealed record ResultEnded(int Index, bool Truncated) : BatchEvent;

/// <summary>
/// The run is over: one result per set, exactly what <see cref="IQueryExecutor.ExecuteAsync"/> would have
/// returned — except that a failure part-way <b>keeps the sets read before it</b>, followed by the failed
/// result, where <c>ExecuteAsync</c> reports the failure alone. Always the last event of a stream that did not
/// throw (a cancellation throws).
/// </summary>
public sealed record BatchCompleted(IReadOnlyList<QueryResult> Results) : BatchEvent;
