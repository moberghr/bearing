using System.Diagnostics;
using System.Runtime.CompilerServices;
using Amazon.DynamoDBv2.Model;
using Bearing.Core.Data;

namespace Bearing.Data.DynamoDb;

/// <summary>
/// Runs PartiQL with <c>ExecuteStatement</c> and reads the answer a page at a time (<i>Behaviour 5–7, 11</i>).
/// The sibling of the other executors in shape — a failure is returned as an error result, the user's own
/// cancellation is thrown — and different in one thing that matters: there is no <c>LIMIT</c>/<c>OFFSET</c>.
/// A read is continued from DynamoDB's own <c>NextToken</c>, carried opaquely in
/// <see cref="QueryResult.Continuation"/>, so a row already on screen is never read (or billed) twice.
/// <para>
/// <b>The page rules.</b> A page ends when it holds the caller's row cap, when DynamoDB reports no
/// <c>NextToken</c>, or after <see cref="RequestBudget"/> requests — whichever comes first. Each request is
/// sent with <c>Limit</c> set to the rows still wanted, so DynamoDB never returns more than the page can hold
/// (<c>Limit</c> bounds the items <em>evaluated</em>, and a match past it would otherwise have to be dropped
/// or re-read). The budget is what keeps one page of a selective filter from reading a whole table; a page
/// it cut short says so in <see cref="QueryResult.Message"/>. <see cref="QueryResult.Truncated"/> is "a
/// token remains" — whether more rows exist comes from the token, never from the row count.
/// </para>
/// <para>
/// <b>Columns are fixed by the first page</b> (§9.10: the grid never rebuilds its columns): the table's key
/// attributes first, then the rest alphabetically (<see cref="DynamoDbValues.Layout"/>). A result that
/// continues always carries the trailing <see cref="DynamoDbValues.OtherAttributes"/> column, whatever its
/// projection — a later page can bring an attribute the first page did not show, or a value that does not
/// fit the type the first page gave its column, and both go there as JSON rather than vanish or reach the
/// grid as a cell whose type contradicts its column. A result that fits in one page has no such column.
/// </para>
/// <para>
/// <b>The executor only runs the SQL it is given</b>: nothing is kept between calls except what travels in
/// the continuation the caller hands back.
/// </para>
/// </summary>
public sealed class DynamoDbQueryExecutor : IQueryExecutor
{
    /// <summary>The most requests one page may make before it stops and offers to continue. Each evaluates at
    /// most 1 MB, so a page reads at most about 10 MB however few items match.</summary>
    internal const int RequestBudget = 10;

    internal const string OffsetPagingUnavailable =
        "Paging by offset is not available on Amazon DynamoDB: its results continue from DynamoDB's own token.";

    internal const string GeneratedWritesUnavailable =
        "Saving edited cells is not available on Amazon DynamoDB connections.";

    internal const string VariablesUnavailable =
        "Script variables are not available on Amazon DynamoDB connections; the statement was not sent.";

    internal const string NothingToRun = "There is no statement to run.";

    private readonly DynamoDbConnectionFactory _factory;

    public DynamoDbQueryExecutor(DynamoDbConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        if (options.Parameters.Count > 0)
        {
            return [Failure(TimeSpan.Zero, new QueryError(VariablesUnavailable, null, null))];
        }

        var statements = PartiQlBatch.Split(sql);
        if (statements.Count == 0)
        {
            return [Failure(TimeSpan.Zero, new QueryError(NothingToRun, null, null))];
        }

        var results = new List<QueryResult>(statements.Count);

        for (var i = 0; i < statements.Count; i++)
        {
            var result = await RunStatementAsync(statements[i], i, options.MaxRows, ct).ConfigureAwait(false);
            results.Add(result with { StatementIndex = i });

            // Each statement is its own call, so the ones before a failure have run; the ones after it are
            // not sent — a batch stops at its first error, as on the other engines.
            if (!result.Success) break;
        }

        return results;
    }

    public async Task<QueryResult> ExecuteContinuationAsync(string continuation, int maxRows, CancellationToken ct)
    {
        DynamoDbContinuation state;

        try
        {
            state = DynamoDbContinuation.Decode(continuation);
        }
        catch (DynamoDbContinuationException e)
        {
            return Failure(TimeSpan.Zero, new QueryError(e.Message, null, null));
        }

        // A continuation only ever continues a read. The token is opaque and this executor wrote it, but it
        // arrives from outside — a caller holding a string — so what it would send is checked again here rather
        // than trusted: a token naming a DELETE is refused and nothing is sent.
        if (!IsSelect(state.Statement))
        {
            var refused = Failure(TimeSpan.Zero, new QueryError(ContinuationNotARead, null, null));
            return refused with { StatementIndex = state.StatementIndex };
        }

        var sw = Stopwatch.StartNew();
        using var deadline = Deadline(ct);

        try
        {
            var page = await ReadPageAsync(state.Statement, state.NextToken, Math.Max(1, maxRows), deadline.Token)
                .ConfigureAwait(false);

            return RowsResult(page, state.Layout, state.Statement, state.KeyAttributes, state.StatementIndex, sw);
        }
        catch (Exception e) when (ct.IsCancellationRequested && e is not OperationCanceledException)
        {
            throw new OperationCanceledException("The statement was canceled.", e, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            ForgetStaleTable(state.Statement, e);
            return Failure(sw.Elapsed, ErrorFor(e, deadline)) with { StatementIndex = state.StatementIndex };
        }
    }

    internal const string ContinuationNotARead =
        "This continuation does not continue a SELECT, so nothing was sent. Run the statement again instead.";

    /// <summary>
    /// One statement from the start, following <c>NextToken</c> to the end (or to
    /// <see cref="QueryOptions.MaxRows"/>), with no request budget — "fetch all" means all. Kept for the
    /// contract's sake: the App continues a DynamoDB result through <see cref="ExecuteContinuationAsync"/>.
    /// Failures are thrown, as the interface says; the connection's timeout covers the whole stream.
    /// </summary>
    public async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string sql, QueryOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        var statements = PartiQlBatch.Split(sql);
        if (statements.Count != 1)
        {
            throw new NotSupportedException(
                $"A streamed read runs exactly one statement; this text holds {statements.Count}.");
        }

        var statement = statements[0];
        var batchSize = Math.Max(1, options.BatchRows);
        var max = options.MaxRows is { } cap ? Math.Max(0, cap) : (int?)null;
        using var deadline = Deadline(ct);

        if (!IsSelect(statement))
        {
            // No result shape: nothing to stream (RowBatch's contract), but the statement still runs.
            await SendAsync(new ExecuteStatementRequest { Statement = statement }, deadline, ct).ConfigureAwait(false);
            yield break;
        }

        var pending = new List<Dictionary<string, AttributeValue>>();
        DynamoDbColumnLayout? layout = null;
        IReadOnlyList<ColumnDescriptor> columns = [];
        string? token = null;
        var yielded = 0;

        do
        {
            if (max is { } limit && yielded + pending.Count >= limit) break;

            var request = new ExecuteStatementRequest { Statement = statement, NextToken = token };
            if (max is { } ceiling)
            {
                request.Limit = Math.Max(1, ceiling - yielded - pending.Count);
            }

            var response = await SendAsync(request, deadline, ct).ConfigureAwait(false);
            if (response.Items is { } items) pending.AddRange(items);
            token = TokenOf(response);

            if (layout is null && (pending.Count >= batchSize || token is null))
            {
                var keys = await KeyAttributesAsync(statement, pending, deadline, ct).ConfigureAwait(false);
                layout = DynamoDbValues.Layout(pending, keys, hasOverflow: token is not null);
                columns = layout.Descriptors();
            }

            while (layout is not null && pending.Count >= batchSize)
            {
                var chunk = pending.Take(batchSize).Select(item => DynamoDbValues.Row(item, layout)).ToList();
                pending.RemoveRange(0, batchSize);
                yielded += chunk.Count;

                yield return new RowBatch(columns, chunk, Truncated: false);
            }
        }
        while (token is not null);

        if (layout is null)
        {
            var keys = await KeyAttributesAsync(statement, pending, deadline, ct).ConfigureAwait(false);
            layout = DynamoDbValues.Layout(pending, keys, hasOverflow: token is not null);
            columns = layout.Descriptors();
        }

        var tail = pending.Select(item => DynamoDbValues.Row(item, layout)).ToList();

        // The tail, always (§5.7). A token left over means the cap stopped the read, not the table's end.
        yield return new RowBatch(columns, tail, Truncated: token is not null);
    }

    public Task<QueryResult> ExecutePageAsync(
        string pageSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
        => Task.FromResult(Failure(TimeSpan.Zero, new QueryError(OffsetPagingUnavailable, null, null)));

    /// <summary>
    /// Null — the interface's "this shape cannot be counted", which the App already words as "Count
    /// unavailable for this query on this engine". DynamoDB has no count of a statement's matches short of
    /// reading them all, and a count that silently scanned a table is exactly what this engine must not do.
    /// </summary>
    public Task<long?> CountAsync(string countSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
        => Task.FromResult<long?>(null);

    public Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<QueryResult>>(
            [Failure(TimeSpan.Zero, new QueryError(GeneratedWritesUnavailable, null, null))]);

    // ---- one statement -----------------------------------------------------------------------------

    private async Task<QueryResult> RunStatementAsync(string statement, int index, int? maxRows, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var deadline = Deadline(ct);

        try
        {
            if (!IsSelect(statement))
            {
                return await RunOtherAsync(statement, index, deadline, ct, sw).ConfigureAwait(false);
            }

            var cap = maxRows is { } m ? Math.Max(1, m) : (int?)null;
            var page = await ReadPageAsync(statement, null, cap, deadline.Token).ConfigureAwait(false);
            var keys = await KeyAttributesAsync(statement, page.Items, deadline, ct).ConfigureAwait(false);
            var layout = DynamoDbValues.Layout(page.Items, keys, hasOverflow: page.NextToken is not null);

            return RowsResult(page, layout, statement, keys, index, sw);
        }
        catch (Exception e) when (ct.IsCancellationRequested && e is not OperationCanceledException)
        {
            throw new OperationCanceledException("The statement was canceled.", e, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            ForgetStaleTable(statement, e);
            return Failure(sw.Elapsed, ErrorFor(e, deadline));
        }
    }

    /// <summary>
    /// <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> — and any other verb, which is sent as typed. One request:
    /// a write has nothing to page. DynamoDB returns no count of what it changed, so the message names the verb
    /// and claims no number; an <c>UPDATE … RETURNING</c> that hands items back shows them.
    /// </summary>
    private async Task<QueryResult> RunOtherAsync(
        string statement, int index, CancellationTokenSource deadline, CancellationToken ct, Stopwatch sw)
    {
        var response = await _factory.Api
            .ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement }, deadline.Token)
            .ConfigureAwait(false);

        var message = WriteSentence(PartiQlBatch.LeadVerb(statement));

        if (response.Items is not { Count: > 0 } items)
        {
            return new QueryResult([], [], 0, sw.Elapsed, message, null, Truncated: false) { StatementIndex = index };
        }

        var keys = await KeyAttributesAsync(statement, items, deadline, ct).ConfigureAwait(false);
        var layout = DynamoDbValues.Layout(items, keys, hasOverflow: false);
        var rows = items.Select(item => DynamoDbValues.Row(item, layout)).ToList();

        return new QueryResult(layout.Descriptors(), rows, rows.Count, sw.Elapsed, message, null, Truncated: false)
        {
            StatementIndex = index,
        };
    }

    internal static string WriteSentence(string verb)
        => $"{(verb.Length == 0 ? "Statement" : verb)} executed. DynamoDB does not report how many items it changed.";

    /// <summary>The sentence for a page the request budget cut short. Host-neutral — it says what happened, not
    /// which button or flag reads on; each host appends its own way to continue.</summary>
    internal static string BudgetNotice(int rows)
        => $"This page stopped after {RequestBudget} requests to DynamoDB with {rows} row{(rows == 1 ? "" : "s")}: "
            + "the read has not reached the end yet.";

    internal const string NoItems = "The SELECT returned no items.";

    // ---- paging ------------------------------------------------------------------------------------

    private sealed record Page(List<Dictionary<string, AttributeValue>> Items, string? NextToken, bool StoppedByBudget);

    /// <summary>One page under the page rules (see the class remarks). <paramref name="cap"/> null means no
    /// row cap — the budget and the token still end the page.</summary>
    private async Task<Page> ReadPageAsync(string statement, string? startToken, int? cap, CancellationToken ct)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        var token = startToken;
        var requests = 0;

        do
        {
            var request = new ExecuteStatementRequest { Statement = statement, NextToken = token };
            if (cap is { } max)
            {
                request.Limit = Math.Max(1, max - items.Count);
            }

            var response = await _factory.Api.ExecuteStatementAsync(request, ct).ConfigureAwait(false);
            requests++;

            if (response.Items is { } got) items.AddRange(got);
            token = TokenOf(response);
        }
        while (token is not null && requests < RequestBudget && (cap is not { } limit || items.Count < limit));

        var stoppedByBudget = token is not null && requests >= RequestBudget && (cap is not { } full || items.Count < full);

        return new Page(items, token, stoppedByBudget);
    }

    private static QueryResult RowsResult(
        Page page, DynamoDbColumnLayout layout, string statement, IReadOnlyList<string> keys, int index, Stopwatch sw)
    {
        var rows = page.Items
            .Select(item => DynamoDbValues.Row(item, layout))
            .ToList();
        var columns = layout.Descriptors();
        var continuation = page.NextToken is null
            ? null
            : new DynamoDbContinuation(statement, page.NextToken, layout, keys, index).Encode();
        var message = page.StoppedByBudget
            ? BudgetNotice(rows.Count)
            : columns.Count == 0 ? NoItems : null;

        return new QueryResult(columns, rows, rows.Count, sw.Elapsed, message, null, Truncated: page.NextToken is not null)
        {
            StatementIndex = index,
            Continuation = continuation,
        };
    }

    /// <summary>
    /// The key attributes a result leads with: the table's partition key, then its sort key. For
    /// <c>SELECT *</c> every item has them, so they are columns even on an empty first page; for a projection
    /// only those the first page actually holds are (<i>Behaviour 5</i>: the attributes found). A table that is
    /// gone or whose description is refused, or a statement whose table cannot be read off, gives none — the
    /// columns are then alphabetical only.
    /// <para>
    /// <b>Two describe failures are answers, the rest are failures.</b> A table DynamoDB says is gone, or a
    /// policy that refuses <c>DescribeTable</c>, gives no key attributes — asking again would say the same. A
    /// throttle, a network failure or anything else fails the statement with its own classified error rather
    /// than quietly reorder the columns (SF2); the connection's clock running out is the timeout, as for a page.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> KeyAttributesAsync(
        string statement,
        IReadOnlyList<Dictionary<string, AttributeValue>> firstPage,
        CancellationTokenSource deadline,
        CancellationToken ct)
    {
        if (PartiQlBatch.Target(statement) is not { } target) return [];

        IReadOnlyList<string> keys;

        try
        {
            var table = await _factory.DescribeTableAsync(target.Table, deadline.Token).ConfigureAwait(false);
            keys = DynamoDbMetadataReader.KeyAttributes(table.KeySchema);
        }
        catch (Exception e) when (!ct.IsCancellationRequested
                                  && !deadline.IsCancellationRequested
                                  && (DynamoDbErrors.IsResourceNotFound(e) || DynamoDbErrors.IsAccessDenied(e)))
        {
            return [];
        }
        catch (Exception e) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException(DynamoDbErrors.TimeoutSentence(_factory.StatementTimeoutSeconds), e);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            var surfaced = DynamoDbErrors.ForCaller(e, _factory.Profile);
            if (ReferenceEquals(surfaced, e)) throw;

            throw surfaced;
        }

        return PartiQlBatch.SelectsAll(statement)
            ? keys
            : keys
                .Where(key => firstPage.Any(item => item.ContainsKey(key)))
                .ToList();
    }

    // ---- requests, timeout, errors -----------------------------------------------------------------

    /// <summary>The caller's token, plus the connection's statement timeout when it has one (§1.9a: a
    /// client-side cancel, as on SQL Server).</summary>
    private CancellationTokenSource Deadline(CancellationToken ct)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_factory.StatementTimeoutSeconds > SessionPolicy.NoTimeout)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(_factory.StatementTimeoutSeconds));
        }

        return deadline;
    }

    /// <summary>One request on the streaming path, where failures are thrown: the connection's timeout comes
    /// out as a <see cref="TimeoutException"/> naming it, the user's cancellation as itself.</summary>
    private async Task<ExecuteStatementResponse> SendAsync(
        ExecuteStatementRequest request, CancellationTokenSource deadline, CancellationToken ct)
    {
        try
        {
            return await _factory.Api.ExecuteStatementAsync(request, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException(DynamoDbErrors.TimeoutSentence(_factory.StatementTimeoutSeconds), e);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            ForgetStaleTable(request.Statement, e);

            var surfaced = DynamoDbErrors.ForCaller(e, _factory.Profile);
            if (ReferenceEquals(surfaced, e)) throw;

            throw surfaced;
        }
    }

    /// <summary>
    /// Drop the cached description of the table <paramref name="statement"/> reads when the failure says it may
    /// be out of date (<see cref="DynamoDbErrors.MayMeanStaleTable"/>: the table is gone, or DynamoDB rejected the
    /// statement against it). The next statement, scan check or column layout then reads it again rather than
    /// trust a description from before a drop, a re-create or a new index (SF5).
    /// </summary>
    private void ForgetStaleTable(string statement, Exception failure)
    {
        if (!DynamoDbErrors.MayMeanStaleTable(failure)) return;
        if (PartiQlBatch.Target(statement) is { } target) _factory.ForgetTable(target.Table);
    }

    /// <summary>A failure as the grid's error. The timeout is ours to know — our own clock fired and the
    /// caller's did not — not something the SDK's exception can say.</summary>
    private QueryError ErrorFor(Exception e, CancellationTokenSource deadline)
        => DynamoDbErrors.ToQueryError(
            e,
            _factory.Profile,
            _factory.StatementTimeoutSeconds,
            timedOut: deadline.IsCancellationRequested && _factory.StatementTimeoutSeconds > SessionPolicy.NoTimeout);

    private static bool IsSelect(string statement) => PartiQlBatch.LeadVerb(statement) == "SELECT";

    private static string? TokenOf(ExecuteStatementResponse response)
        => string.IsNullOrEmpty(response.NextToken) ? null : response.NextToken;

    private static QueryResult Failure(TimeSpan elapsed, QueryError error) => new(
        [], [], RowCount: 0, elapsed, Message: null, Error: error, Truncated: false);
}
