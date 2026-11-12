using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Npgsql;
using Npgsql.Schema;
using Bearing.Core.Data;

namespace Bearing.Data.Postgres;

/// <summary>
/// Executes SQL against PostgreSQL and materializes (or streams) the result.
/// <para>
/// <b>Every await that can suspend carries <c>ConfigureAwait(false)</c>.</b> This layer is called straight
/// from view-models running on Avalonia's UI thread, whose <c>SynchronizationContext</c> would otherwise be
/// captured — and since the row loop awaits once per row, result materialization would resume on the UI
/// thread and compete with rendering. (A synchronously-completed await posts nothing, so the plain
/// <c>await using</c> disposals below are safe: by the time they run, an earlier configured await has
/// already moved us off the UI context.)
/// </para>
/// </summary>
public sealed class PostgresQueryExecutor : IQueryExecutor
{
    private readonly IPgConnectionSource _source;

    public PostgresQueryExecutor(NpgsqlConnectionFactory factory) : this(new PooledPgConnections(factory)) { }

    /// <summary>The same executor over a connection someone else is holding — a manual-commit transaction
    /// (#131). Nothing below knows which source it has; that is the point of the seam.</summary>
    internal PostgresQueryExecutor(IPgConnectionSource source) => _source = source;

    public async Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<QueryResult>();
        try
        {
            await using var rent = await _source.RentAsync(ct).ConfigureAwait(false);
            var (cmd, reader) = await OpenReaderAsync(rent, sql, options.Parameters, ct).ConfigureAwait(false);
            await using var command = cmd;
            await using var rows = reader;

            // One QueryResult per statement's result set — NextResult walks a multi-statement batch.
            // withBaseTables: capture column origin (schema/table/column) for FK-nav + inline edit.
            do
            {
                results.Add(await ReadResultSetAsync(reader, options, sw, ct, withBaseTables: true).ConfigureAwait(false));
            }
            while (await reader.NextResultAsync(ct).ConfigureAwait(false));

            return Attributed(results, reader);
        }
        catch (PostgresException pg)
        {
            sw.Stop();
            var pos = pg.Position > 0 ? pg.Position : (int?)null;
            return new[] { Failure(sw.Elapsed, new QueryError(MessageFor(pg, options.Parameters), pg.SqlState, pos)) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new[] { Failure(sw.Elapsed, new QueryError(PostgresErrorText.Explain(ex), null, null)) };
        }
    }

    public async Task<QueryResult> ExecutePageAsync(
        string pageSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
    {
        // The caller (PageSql) already shaped the paging; we just run it as one uncapped result set.
        var sw = Stopwatch.StartNew();
        try
        {
            await using var rent = await _source.RentAsync(ct).ConfigureAwait(false);
            var (cmd, reader) = await OpenReaderAsync(rent, pageSql, parameters, ct).ConfigureAwait(false);
            await using var command = cmd;
            await using var rows = reader;
            return await ReadResultSetAsync(reader, new QueryOptions { MaxRows = null }, sw, ct).ConfigureAwait(false);
        }
        catch (PostgresException pg)
        {
            sw.Stop();
            var pos = pg.Position > 0 ? pg.Position : (int?)null;
            return Failure(sw.Elapsed, new QueryError(MessageFor(pg, parameters), pg.SqlState, pos));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return Failure(sw.Elapsed, new QueryError(PostgresErrorText.Explain(ex), null, null));
        }
    }

    /// <summary>
    /// Streams one row-returning query in batches. No try/catch: this path <em>throws</em> (see the interface
    /// contract) — a stream whose failure came back as an empty tail would let "fetch all" report a complete
    /// result it never read.
    /// </summary>
    public async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string sql, QueryOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        var batchSize = Math.Max(1, options.BatchRows);
        await using var rent = await _source.RentAsync(ct).ConfigureAwait(false);
        var (cmd, reader) = await OpenReaderAsync(rent, sql, options.Parameters, ct).ConfigureAwait(false);
        await using var command = cmd;
        await using var rows = reader;

        if (reader.FieldCount == 0) yield break; // not row-returning — nothing to stream

        // Read once and carried on every batch: the shape cannot change mid-stream, and a consumer
        // with no result already on screen has nowhere else to get the header row from.
        var columns = ReadColumns(reader);

        var batch = new List<object?[]>(batchSize);
        var read = 0;
        var truncated = false;

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // Cap check *after* a successful read and before materializing: getting here means the server
            // still had a row, which is precisely the "there was more" signal the caller can't get otherwise.
            if (options.MaxRows is { } max && read >= max) { truncated = true; break; }

            var row = new object?[reader.FieldCount];
            // Sync IsDBNull for the same reason as ReadResultSetAsync: the row is already buffered.
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            batch.Add(row);
            read++;

            if (batch.Count >= batchSize)
            {
                yield return new RowBatch(columns, batch, Truncated: false);
                batch = new List<object?[]>(batchSize);
            }
        }

        // The tail, always — even empty, and even with nothing truncated. Two reasons it is unconditional:
        // Truncated has to reach the caller when the ceiling lands exactly on a batch boundary, and a
        // row-returning statement has to name its columns even when it matched no rows. A consumer building
        // a file from the stream alone (the CLI's streamed CSV) would otherwise have no header to write, and
        // would produce a headerless file for an empty result where every other export writes one.
        yield return new RowBatch(columns, batch, truncated);
    }

    public async Task<long?> CountAsync(
        string countSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
    {
        // The caller (PageSql.CountWrap, through the connection's dialect) already shaped the wrapper; we
        // just run it, exactly as ExecutePageAsync runs a page the caller shaped.
        await using var rent = await _source.RentAsync(ct).ConfigureAwait(false);

        // Inside a held transaction (#131) this probe runs under a savepoint, and it is the only call in this
        // class that does. A shape the wrapper cannot count comes back as a syntax error, and on Postgres
        // *any* error aborts the whole transaction — so a speculative count of ours would destroy the user's
        // uncommitted work and leave every later statement answering 25P02. The savepoint protects their
        // transaction from us. It is deliberately not a general undo for statements they asked for: those
        // abort the transaction, which is what the server did and what the chip then says.
        // `guarded` is set only once the savepoint is actually in place, and the SAVEPOINT is inside the
        // try: in an already-aborted transaction it is itself refused with 25P02, and issued outside it
        // would escape as a raw driver error rather than the documented "no total available".
        var guarded = false;
        try
        {
            if (rent.Transaction is not null)
            {
                await ExecNonQueryAsync(rent, "savepoint " + CountSavepoint, ct).ConfigureAwait(false);
                guarded = true;
            }
            object? scalar;
            var (cmd, reader) = await OpenReaderAsync(rent, countSql, parameters, ct).ConfigureAwait(false);
            await using (cmd)
            await using (reader)
                scalar = await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(0) ? reader.GetValue(0) : null;
            if (guarded) await ExecNonQueryAsync(rent, "release savepoint " + CountSavepoint, ct).ConfigureAwait(false);
            return scalar is null or DBNull ? null : Convert.ToInt64(scalar);
        }
        catch (Exception ex)
        {
            // **Every** failure unwinds, not just a PostgresException. The count runs under a deadline
            // (§1.5: CountTimeout 2 s, CountBudget 4 s for the batch), and a statement cancelled by its
            // token surfaces as OperationCanceledException — while the *server* has already raised 57014
            // and aborted the transaction block. Catching only the driver's exception left that savepoint
            // un-rolled-back, the scope still reporting Active, and Commit still offered for a transaction
            // Postgres would have silently turned into a rollback.
            if (guarded) await UnwindCountAsync(rent).ConfigureAwait(false);
            // The shape verdict is still the driver's alone; everything else propagates.
            if (ex is PostgresException pg && IsUncountableShape(pg)) return null;
            throw;
        }
    }

    /// <summary>
    /// Roll back to the count probe's savepoint, best-effort.
    /// <para>
    /// <b>Not the caller's token.</b> The commonest reason to be here is that the token was cancelled, and
    /// issuing the recovery on it would throw before reaching the server — leaving the transaction in
    /// exactly the state this exists to get it out of.
    /// </para>
    /// <para>
    /// A failure is swallowed: the transaction is past saving either way, and the error worth reporting is
    /// the one that got us here, not the one raised trying to recover from it.
    /// </para>
    /// </summary>
    private static async Task UnwindCountAsync(PgRent rent)
    {
        try
        {
            await ExecNonQueryAsync(rent, "rollback to savepoint " + CountSavepoint, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch { /* see above */ }
    }

    /// <summary>Named, not anonymous: it is released on the way out, so a long-lived transaction does not
    /// accumulate one per count.</summary>
    private const string CountSavepoint = "bearing_count_probe";

    private static async Task ExecNonQueryAsync(PgRent rent, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, rent.Connection, rent.Transaction);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the failure means the query's <em>shape</em> can't be wrapped in
    /// <c>select count(*) from (…)</c> — the only case that may be reported as "no total available".
    /// A multi-statement batch or a non-SELECT dies on <c>syntax_error</c>; a data-modifying CTE, which
    /// must be top-level, on <c>feature_not_supported</c>. Everything else (server down mid-session,
    /// table dropped under us, permission denied, statement timeout, and the <c>query_canceled</c> Npgsql
    /// raises for a cancelled command) is a real failure and is left to propagate: swallowing it showed an
    /// unpageable-looking result with no total instead of telling the user the count failed.
    /// </summary>
    private static bool IsUncountableShape(PostgresException pg)
        => pg.SqlState is PostgresErrorCodes.SyntaxError or PostgresErrorCodes.FeatureNotSupported;

    /// <summary>
    /// Bind one generated-DML parameter. A string goes as Postgres <c>unknown</c>, not <c>text</c>, so the
    /// server types it from the column it is assigned to or compared with — exactly as it would a quoted
    /// literal. The grid hands back a string for every column Npgsql reads as one (<c>jsonb</c>, <c>json</c>,
    /// <c>xml</c>, enums, <c>citext</c>, …), and there is no implicit <c>text → jsonb</c> cast: bound as
    /// <c>text</c>, every edit of such a column failed with "is of type jsonb but expression is of type text".
    /// A text column is unaffected — <c>unknown</c> resolves to <c>text</c> there.
    /// </summary>
    /// <summary>
    /// Open a reader over <paramref name="sql"/> with a run's <c>:name</c> values (#103) bound to the named
    /// placeholders the caller wrote. Nothing is bound when there are none, so a run without variables sends
    /// its text exactly as before. Named, so Npgsql rewrites the placeholders and still splits a run of
    /// several statements.
    /// <para>
    /// <b>Untyped first.</b> Every value goes as <c>unknown</c>, numbers included, so the server types it from
    /// where it stands — <c>where film_id = $1</c> as the column's type, <c>limit $1</c> as bigint — exactly
    /// as it would the same value written as a quoted literal. Where nothing around the value says what it is
    /// (<c>select $1</c>, <c>$1 is null</c>) Postgres refuses with 42P18 rather than guess, and only then is the
    /// command sent again with the types the values were written as: text, or numeric for a number. Safe to
    /// resend: 42P18 is raised while the statement is parsed, before anything runs, and outside a held
    /// transaction the batch was its own. Inside one (#131) the refusal has already aborted it, so there is no
    /// second try — the error says how to cast instead (<see cref="MessageFor"/>).
    /// </para>
    /// </summary>
    private static async Task<(NpgsqlCommand Command, NpgsqlDataReader Reader)> OpenReaderAsync(
        PgRent rent, string sql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
    {
        var cmd = Command(rent, sql, parameters, typed: false);
        try
        {
            return (cmd, await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false));
        }
        catch (PostgresException pg) when (pg.SqlState == IndeterminateDatatype && parameters.Count > 0
                                           && rent.Transaction is null)
        {
            await cmd.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            await cmd.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var typed = Command(rent, sql, parameters, typed: true);
        try
        {
            return (typed, await typed.ExecuteReaderAsync(ct).ConfigureAwait(false));
        }
        catch
        {
            await typed.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>42P18 — "could not determine data type of parameter".</summary>
    private const string IndeterminateDatatype = "42P18";

    private static NpgsqlCommand Command(PgRent rent, string sql, IReadOnlyList<QueryParameter> parameters, bool typed)
    {
        var cmd = new NpgsqlCommand(sql, rent.Connection, rent.Transaction);
        foreach (var p in parameters)
            cmd.Parameters.Add(typed ? Typed(p) : new NpgsqlParameter(p.Name, NpgsqlTypes.NpgsqlDbType.Unknown)
            {
                Value = (object?)p.Value ?? DBNull.Value,
            });
        return cmd;
    }

    /// <summary>A value with the type it was written as: a number as numeric, everything else as text.</summary>
    private static NpgsqlParameter Typed(QueryParameter p)
        => p.Kind == QueryParameterKind.Number && decimal.TryParse(p.Value,
               System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
               System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? new NpgsqlParameter(p.Name, NpgsqlTypes.NpgsqlDbType.Numeric) { Value = number }
            : new NpgsqlParameter(p.Name, NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)p.Value ?? DBNull.Value };

    /// <summary>The server's message, plus what to do about a 42P18 that a variable caused — the user wrote
    /// <c>:name</c>, and "parameter $1" names something they never typed.</summary>
    private static string MessageFor(PostgresException pg, IReadOnlyList<QueryParameter> parameters)
        => pg.SqlState == IndeterminateDatatype && parameters.Count > 0
            ? pg.MessageText + " — nothing around a :name value says what type it is; add a cast, e.g. :name::int"
            : pg.MessageText;

    private static NpgsqlParameter WriteParameter(SqlParameter p) => p.Value is string s
        ? new NpgsqlParameter(p.Name, NpgsqlTypes.NpgsqlDbType.Unknown) { Value = s }
        : new NpgsqlParameter(p.Name, p.Value ?? DBNull.Value);

    public async Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(
        IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<QueryResult>(commands.Count);
        await using var rent = await _source.RentAsync(ct).ConfigureAwait(false);

        // One transaction for the whole batch: any failure disposes the tx uncommitted → rollback.
        // In manual-commit mode one is already open and it is the user's, so the batch *joins* it and is
        // deliberately not committed here — that is the whole of #131 as far as this method is concerned.
        // `own` is non-null exactly when this call opened one, which is also what says whether to commit it.
        var own = rent.Transaction is null
            ? await rent.Connection.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        var tx = rent.Transaction ?? own!;
        try
        {
            foreach (var c in commands)
            {
                await using var cmd = new NpgsqlCommand(c.Sql, rent.Connection, tx);
                foreach (var p in c.Parameters)
                    cmd.Parameters.Add(WriteParameter(p));
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                results.Add(await ReadResultSetAsync(reader, new QueryOptions { MaxRows = null }, sw, ct).ConfigureAwait(false));
            }
            if (own is not null) await own.CommitAsync(ct).ConfigureAwait(false);
            return results;
        }
        catch (PostgresException pg)
        {
            sw.Stop();
            var pos = pg.Position > 0 ? pg.Position : (int?)null;
            return new[] { Failure(sw.Elapsed, new QueryError(pg.MessageText, pg.SqlState, pos)) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new[] { Failure(sw.Elapsed, new QueryError(PostgresErrorText.Explain(ex), null, null)) };
        }
        finally
        {
            // Disposing an uncommitted transaction rolls it back — which is the batch's own rollback on the
            // pooled path, and must never reach a transaction this call did not open.
            if (own is not null) await own.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tell each result set which statement of the batch it came from
    /// (<see cref="QueryResult.StatementIndex"/>) — but only when the batch left no room for doubt.
    /// <para>
    /// <b>The walk is not one set per statement.</b> <c>NextResult</c> skips a statement that returned no
    /// rows, so <c>select …; insert …; select …</c> is three statements and two result sets, and the second
    /// set is statement 2 rather than statement 1. Measured, not assumed: a 13-statement probe against
    /// PostgreSQL 18 walked 8 sets.
    /// </para>
    /// <para>
    /// Which ones were skipped is not answerable through Npgsql's public surface. Only a statement's
    /// RowDescription distinguishes them, and it is internal; <c>StatementType</c> is not a substitute —
    /// <c>explain</c> and <c>analyze</c> are both <c>Other</c> though only the first returns rows, and
    /// <c>create table as</c> reports as <c>Select</c> and returns none. (Nor is the per-statement text
    /// public: every entry's <c>CommandText</c> is the whole batch, and the split text is on the internal
    /// <c>FinalCommandText</c>.)
    /// </para>
    /// <para>
    /// So the mapping is claimed on the one condition that <i>proves</i> it: as many result sets as the
    /// driver parsed statements means nothing was skipped, and set <c>k</c> is statement <c>k</c>. That is
    /// the ordinary multi-select batch. Anything else reports null — an honest "can't say", which the caller
    /// answers with the whole run's text (see <c>ResultSetBuilder.StatementBehind</c>). Guessing here would
    /// caption a grid with a neighbouring statement, which is worse than captioning it with all of them.
    /// </para>
    /// </summary>
    private static List<QueryResult> Attributed(List<QueryResult> results, NpgsqlDataReader reader)
    {
        // Obsoleted in favour of the DbBatch API, which is the right advice for code that *builds* a batch
        // and no help to code asking what the driver made of one string of user-typed SQL. Nothing
        // non-obsolete reports it, and the read is one count.
#pragma warning disable CS0618
        var parsed = reader.Statements.Count;
#pragma warning restore CS0618
        if (parsed != results.Count) return results;

        for (var i = 0; i < results.Count; i++) results[i] = results[i] with { StatementIndex = i };
        return results;
    }

    private static async Task<QueryResult> ReadResultSetAsync(
        NpgsqlDataReader reader, QueryOptions options, Stopwatch sw, CancellationToken ct, bool withBaseTables = false)
    {
        // A non-row-returning statement (INSERT/UPDATE/DDL) still reports affected rows.
        if (reader.FieldCount == 0)
            return new QueryResult(
                Array.Empty<ColumnDescriptor>(), Array.Empty<object?[]>(),
                RowCount: reader.RecordsAffected, sw.Elapsed,
                Message: DescribeNonQuery(reader.RecordsAffected), Error: null, Truncated: false);

        var columns = ReadColumns(reader, withBaseTables);
        var rows = new List<object?[]>();
        var truncated = false;

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (options.MaxRows is { } max && rows.Count >= max) { truncated = true; break; }

            var row = new object?[reader.FieldCount];
            // Sync IsDBNull, deliberately: the reader isn't in sequential-access mode, so ReadAsync has
            // already buffered the whole row and the null check can't touch the socket. The async form
            // cost an awaited state-machine hop per *cell* to await an always-completed task.
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return new QueryResult(columns, rows, rows.Count, sw.Elapsed,
            Message: null, Error: null, Truncated: truncated);
    }

    private static IReadOnlyList<ColumnDescriptor> ReadColumns(NpgsqlDataReader reader, bool withBaseTables = false)
    {
        // Column origin (table OID + attribute number) comes free from the wire RowDescription — no
        // catalog round-trip. Captured for raw queries; skipped for the wrapped paging query, whose
        // columns are the subquery's (no table origin) anyway.
        var schema = withBaseTables ? reader.GetColumnSchema() : null;

        var cols = new ColumnDescriptor[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var npg = schema?[i] as NpgsqlDbColumn;
            cols[i] = new ColumnDescriptor(
                reader.GetName(i), reader.GetDataTypeName(i), reader.GetFieldType(i),
                BaseTableId: npg?.TableOID ?? 0,
                BaseColumnOrdinal: (short)(npg?.ColumnAttributeNumber ?? 0));
        }
        return cols;
    }

    private static QueryResult Failure(TimeSpan elapsed, QueryError error) => new(
        Array.Empty<ColumnDescriptor>(), Array.Empty<object?[]>(),
        RowCount: 0, elapsed, Message: null, Error: error, Truncated: false);

    private static string DescribeNonQuery(int affected) =>
        affected >= 0 ? $"{affected} row(s) affected" : "Statement executed";
}
