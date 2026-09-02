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
    private readonly NpgsqlConnectionFactory _factory;

    public PostgresQueryExecutor(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<QueryResult>();
        try
        {
            await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            // One QueryResult per statement's result set — NextResult walks a multi-statement batch.
            // withBaseTables: capture column origin (schema/table/column) for FK-nav + inline edit.
            do
            {
                results.Add(await ReadResultSetAsync(reader, options, sw, ct, withBaseTables: true).ConfigureAwait(false));
            }
            while (await reader.NextResultAsync(ct).ConfigureAwait(false));

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
    }

    public async Task<QueryResult> ExecutePageAsync(string pageSql, CancellationToken ct)
    {
        // The caller (PageSql) already shaped the paging; we just run it as one uncapped result set.
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(pageSql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await ReadResultSetAsync(reader, new QueryOptions { MaxRows = null }, sw, ct).ConfigureAwait(false);
        }
        catch (PostgresException pg)
        {
            sw.Stop();
            var pos = pg.Position > 0 ? pg.Position : (int?)null;
            return Failure(sw.Elapsed, new QueryError(pg.MessageText, pg.SqlState, pos));
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
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (reader.FieldCount == 0) yield break; // not row-returning — nothing to stream

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
                yield return new RowBatch(batch, Truncated: false);
                batch = new List<object?[]>(batchSize);
            }
        }

        // The tail. Also emitted when it is empty but the cap stopped us, since Truncated has to reach the
        // caller even when the ceiling happens to land on a batch boundary.
        if (batch.Count > 0 || truncated) yield return new RowBatch(batch, truncated);
    }

    public async Task<long?> CountAsync(string sql, CancellationToken ct)
    {
        var wrapped = $"select count(*) from (\n{StripTrailingSemicolon(sql)}\n) as _sq";
        try
        {
            await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(wrapped, conn);
            var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return scalar is null or DBNull ? null : Convert.ToInt64(scalar);
        }
        catch (PostgresException pg) when (IsUncountableShape(pg))
        {
            return null; // this query can't be wrapped at all — caller just hides the total
        }
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

    public async Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(
        IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<QueryResult>(commands.Count);
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        // One transaction for the whole batch: any failure disposes the tx uncommitted → rollback.
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var c in commands)
            {
                await using var cmd = new NpgsqlCommand(c.Sql, conn, tx);
                foreach (var p in c.Parameters)
                    cmd.Parameters.Add(new NpgsqlParameter(p.Name, p.Value ?? DBNull.Value));
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                results.Add(await ReadResultSetAsync(reader, new QueryOptions { MaxRows = null }, sw, ct).ConfigureAwait(false));
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
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
    }

    private static string StripTrailingSemicolon(string sql)
    {
        var s = sql.TrimEnd();
        return s.EndsWith(';') ? s[..^1] : s;
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
