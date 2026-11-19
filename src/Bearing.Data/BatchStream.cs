using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Bearing.Core.Data;

namespace Bearing.Data;

/// <summary>
/// The plumbing behind every executor's <see cref="IQueryExecutor.ExecuteStreamingAsync"/>: the executor's own
/// read loop — the very one <c>ExecuteAsync</c> runs — reports to this as it goes, and the caller reads the
/// reports as <see cref="BatchEvent"/>s.
/// <para>
/// <b>Why a channel and a producer task</b> rather than an iterator over the reader: C# cannot <c>yield</c>
/// inside a <c>try</c> with a <c>catch</c>, and each executor's error handling is exactly a set of catches
/// (SQL Server's KeyInfo retry, MySQL's cancel mapping, Postgres' message wording). Keeping the loop as it is
/// and observing it means streaming and <c>ExecuteAsync</c> cannot drift apart. The channel is bounded, so a
/// reader that falls behind (the UI appending rows) slows the producer rather than buffering a whole set twice.
/// </para>
/// </summary>
internal sealed class BatchStream
{
    /// <summary>Few batches in flight: enough that the reader never waits on the UI for one append, small enough
    /// that a stalled UI holds the server back instead of filling memory.</summary>
    private const int Capacity = 4;

    private readonly ChannelWriter<BatchEvent> _writer;
    private readonly int _batchRows;
    private readonly List<QueryResult> _done = new();

    private BatchStream(ChannelWriter<BatchEvent> writer, int batchRows)
    {
        _writer = writer;
        _batchRows = Math.Max(1, batchRows);
    }

    /// <summary>The index the set being read now will have: one past every set finished so far.</summary>
    private int Current => _done.Count;

    /// <summary>A row-returning set has begun.</summary>
    public ValueTask StartedAsync(IReadOnlyList<ColumnDescriptor> columns, CancellationToken ct)
        => _writer.WriteAsync(new ResultStarted(Current, columns), ct);

    /// <summary>
    /// Hands over the rows read since <paramref name="flushed"/> once there are a batch of them — or all of them
    /// when <paramref name="final"/> — and returns the new mark. The rows are the list's own instances; only the
    /// slice is new.
    /// </summary>
    public async ValueTask<int> FlushAsync(List<object?[]> rows, int flushed, bool final, CancellationToken ct)
    {
        var pending = rows.Count - flushed;
        if (pending <= 0 || (!final && pending < _batchRows)) return flushed;
        await _writer.WriteAsync(new ResultRows(Current, rows.GetRange(flushed, pending)), ct).ConfigureAwait(false);
        return rows.Count;
    }

    /// <summary>The current set is finished — in the shape <c>ExecuteAsync</c> returns it. A row-returning one
    /// is reported as ended (with whether the cap cut it); one with no shape was never started.</summary>
    public async ValueTask DoneAsync(QueryResult result, CancellationToken ct)
    {
        var index = Current;
        _done.Add(result);
        if (result.Success && result.Columns.Count > 0)
            await _writer.WriteAsync(new ResultEnded(index, result.Truncated), ct).ConfigureAwait(false);
    }

    /// <summary>What a failure part-way reports: the sets finished before it, then the failure — where
    /// <c>ExecuteAsync</c> reports the failure alone.</summary>
    public IReadOnlyList<QueryResult> WithFailure(QueryResult failure) => [.. _done, failure];

    /// <summary>
    /// Runs <paramref name="read"/> — an executor's loop, given this stream to report to — and yields what it
    /// reports, ending with <see cref="BatchCompleted"/> over what it returned. An exception from the loop (a
    /// cancellation) is rethrown here, after the events before it. Abandoning the enumeration stops the loop.
    /// </summary>
    public static async IAsyncEnumerable<BatchEvent> Run(
        int batchRows,
        Func<BatchStream, CancellationToken, Task<IReadOnlyList<QueryResult>>> read,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<BatchEvent>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stream = new BatchStream(channel.Writer, batchRows);

        var producer = Task.Run(async () =>
        {
            try
            {
                var results = await read(stream, stop.Token).ConfigureAwait(false);
                await channel.Writer.WriteAsync(new BatchCompleted(results), stop.Token).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            // WaitToReadAsync rethrows the exception the producer completed with, once the events written
            // before it have been read — so a cancellation reaches the caller after every row that preceded it.
            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                while (channel.Reader.TryRead(out var item))
                    yield return item;
        }
        finally
        {
            // Normal end: the producer is done and this costs nothing. Abandoned early: the producer may be
            // parked on a full channel, and stopping it is what releases the connection it holds.
            stop.Cancel();
            try { await producer.ConfigureAwait(false); }
            catch { /* its failure, if any, was already delivered through the channel */ }
        }
    }
}
