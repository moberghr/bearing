using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Bearing.Core.Data;
// Bearing.Core.Data has its own driver-agnostic SqlParameter (see SqlWriteCommand), so the driver's is
// aliased rather than imported into an ambiguity.
using DriverParameter = Microsoft.Data.SqlClient.SqlParameter;

namespace Bearing.Data.SqlServer;

/// <summary>
/// Executes SQL against Microsoft SQL Server and materializes (or streams) the result — the sibling of
/// <see cref="Postgres.PostgresQueryExecutor"/>, and deliberately the same shape.
/// <para>
/// <b>Every await that can suspend carries <c>ConfigureAwait(false)</c>.</b> This layer is called straight
/// from view-models running on Avalonia's UI thread, whose <c>SynchronizationContext</c> would otherwise be
/// captured — and since the row loop awaits once per row, result materialization would resume on the UI
/// thread and compete with rendering. (A synchronously-completed await posts nothing, so the plain
/// <c>await using</c> disposals below are safe: by the time they run, an earlier configured await has
/// already moved us off the UI context.)
/// </para>
/// <para>
/// Two SqlClient behaviours differ from Npgsql's and shape this file: column origin arrives as names and
/// only under <see cref="CommandBehavior.KeyInfo"/> (see <see cref="ReadColumns"/>), and
/// <see cref="SqlDataReader.RecordsAffected"/> accumulates across a batch instead of resetting per
/// statement (see <see cref="BatchAffected"/>).
/// </para>
/// </summary>
public sealed class SqlServerQueryExecutor : IQueryExecutor
{
    private readonly SqlServerConnectionFactory _factory;

    public SqlServerQueryExecutor(SqlServerConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<QueryResult>();
        var affected = new BatchAffected();
        try
        {
            await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = new SqlCommand(sql, conn);
            // KeyInfo is the *only* way SqlDataReader reports base schema/table/column names, and this is
            // the one path that wants them (FK navigation + inline edit). It costs extra server-side
            // description work, so paging, counting and streaming below run without it.
            await using var reader = await cmd
                .ExecuteReaderAsync(CommandBehavior.KeyInfo, ct).ConfigureAwait(false);

            // One QueryResult per statement's result set — NextResult walks a multi-statement batch.
            // withBaseTables: capture column origin (schema/table/column) for FK-nav + inline edit.
            do
            {
                results.Add(await ReadResultSetAsync(reader, options, sw, affected, ct, withBaseTables: true)
                    .ConfigureAwait(false));
            }
            while (await reader.NextResultAsync(ct).ConfigureAwait(false));

            return results;
        }
        catch (SqlException ex)
        {
            sw.Stop();
            return new[] { Failure(sw.Elapsed, ErrorFrom(ex)) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new[] { Failure(sw.Elapsed, new QueryError(SafeErrorText.Of(ex), null, null)) };
        }
    }

    public async Task<QueryResult> ExecutePageAsync(string pageSql, CancellationToken ct)
    {
        // The caller (PageSql, through the connection's dialect) already shaped the paging; we just run it
        // as one uncapped result set. No KeyInfo: the columns belong to the wrapper's derived table, so there is
        // no origin to be had and no reason to pay for the lookup.
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = new SqlCommand(pageSql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await ReadResultSetAsync(
                reader, new QueryOptions { MaxRows = null }, sw, new BatchAffected(), ct).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            sw.Stop();
            return Failure(sw.Elapsed, ErrorFrom(ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return Failure(sw.Elapsed, new QueryError(SafeErrorText.Of(ex), null, null));
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
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
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

    public async Task<long?> CountAsync(string countSql, CancellationToken ct)
    {
        // The caller (SqlServerDialect.CountWrap, reached through the connection's traits) already shaped
        // the wrapper — including the OFFSET 0 ROWS repair a derived table needs before it may carry an
        // ORDER BY. We just run it, exactly as ExecutePageAsync runs a page the caller shaped. That is also
        // what keeps this project free of a Bearing.Sql reference (§2.2).
        try
        {
            await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = new SqlCommand(countSql, conn);
            var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return scalar is null or DBNull ? null : Convert.ToInt64(scalar);
        }
        catch (SqlException ex) when (IsUncountableShape(ex))
        {
            return null; // this query can't be wrapped at all — caller just hides the total
        }
    }

    /// <summary>
    /// Whether the failure means the query's <em>shape</em> can't be wrapped in
    /// <c>select count(*) from (…)</c> — the only case that may be reported as "no total available".
    /// Same contract as the Postgres executor's, restated in SQL Server's error numbers; each is here
    /// because the wrap itself provokes it, and nothing else is swallowed:
    /// <list type="bullet">
    ///   <item><b>102</b> "Incorrect syntax near …" and <b>156</b> "Incorrect syntax near the keyword …" —
    ///     what a multi-statement batch, a non-SELECT, or a CTE that must stay top-level dies of once it is
    ///     sitting inside a derived table.</item>
    ///   <item><b>1033</b> "The ORDER BY clause is invalid in views, inline functions, derived tables …" —
    ///     the rule the caller's <c>CountWrap</c> repairs; it can still fire on a shape the repair does
    ///     not reach (an ORDER BY the PG lexer read as nested, for one).</item>
    ///   <item><b>8155</b> "No column name was specified for column N of '_sq'" — a derived table demands a
    ///     name for every column, so <c>select 1</c> or <c>select count(*)</c> without an alias is
    ///     uncountable as written. Postgres wraps those happily; this is a shape difference, not a
    ///     failure.</item>
    ///   <item><b>8156</b> "The column 'x' was specified multiple times for '_sq'" — a join selecting two
    ///     columns of the same name is legal at the top level and illegal as a derived table. Again a
    ///     shape, and a common one.</item>
    /// </list>
    /// Everything else propagates: a lost connection, a dropped table, permission denied (229/262), a
    /// statement timeout (-2) and a cancelled command (0) are real failures, and swallowing them showed an
    /// unpageable-looking result with no total instead of telling the user the count failed. That is also
    /// why this is a filtered catch and not a bare one.
    /// </summary>
    private static bool IsUncountableShape(SqlException ex)
        => ex.Number is 102 or 156 or 1033 or 8155 or 8156;

    public async Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(
        IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await RunBatchAsync(commands, sw, withReturning: true, ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == OutputNotAllowedWithTrigger
                                      && commands.Any(c => c.SqlWithoutReturning is not null))
        {
            // Msg 334: "the target table of the INSERT statement cannot have any enabled triggers if the
            // statement contains an OUTPUT clause without INTO". It is a compile error, so nothing ran and
            // the transaction rolled back — the whole batch is safe to re-run. An audited table is an
            // ordinary shape, and refusing it would make a grid insert impossible rather than merely
            // unable to refill the row, so the batch runs again with the returning clause dropped. The
            // caller then falls back to the values the user typed (ResultEditModel.ApplySavedChanges), and
            // the only thing lost is the server-generated identity/defaults on screen until a refresh.
            try
            {
                return await RunBatchAsync(commands, sw, withReturning: false, ct).ConfigureAwait(false);
            }
            catch (SqlException retried)
            {
                sw.Stop();
                return new[] { Failure(sw.Elapsed, ErrorFrom(retried)) };
            }
        }
        catch (SqlException ex)
        {
            sw.Stop();
            return new[] { Failure(sw.Elapsed, ErrorFrom(ex)) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new[] { Failure(sw.Elapsed, new QueryError(SafeErrorText.Of(ex), null, null)) };
        }
    }

    /// <summary>"…cannot have any enabled triggers if the statement contains an OUTPUT clause without
    /// INTO." Raised at compile time, so the batch has not partially applied when it fires.</summary>
    private const int OutputNotAllowedWithTrigger = 334;

    /// <summary>
    /// One attempt at the batch, in one transaction: any failure disposes the transaction uncommitted, so
    /// the whole batch rolls back. <paramref name="withReturning"/> false runs each command's
    /// <see cref="SqlWriteCommand.SqlWithoutReturning"/> where it has one. Failures <em>throw</em> here so
    /// the caller can decide whether to retry; it is the caller that turns them into an error result.
    /// </summary>
    private async Task<IReadOnlyList<QueryResult>> RunBatchAsync(
        IReadOnlyList<SqlWriteCommand> commands, Stopwatch sw, bool withReturning, CancellationToken ct)
    {
        var results = new List<QueryResult>(commands.Count);
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        foreach (var c in commands)
        {
            var sql = withReturning ? c.Sql : c.SqlWithoutReturning ?? c.Sql;
            await using var cmd = new SqlCommand(sql, conn, tx);
            foreach (var p in c.Parameters)
                cmd.Parameters.Add(new DriverParameter(p.Name, p.Value ?? DBNull.Value));
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            // A fresh counter per command: each command has its own reader, so its RecordsAffected
            // starts from nothing rather than continuing the previous command's total.
            results.Add(await ReadResultSetAsync(
                reader, new QueryOptions { MaxRows = null }, sw, new BatchAffected(), ct).ConfigureAwait(false));
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return results;
    }

    private static async Task<QueryResult> ReadResultSetAsync(
        SqlDataReader reader, QueryOptions options, Stopwatch sw, BatchAffected affected,
        CancellationToken ct, bool withBaseTables = false)
    {
        // A non-row-returning statement (INSERT/UPDATE/DDL) still reports affected rows.
        if (reader.FieldCount == 0)
        {
            var rows = affected.Take(reader.RecordsAffected);
            return new QueryResult(
                Array.Empty<ColumnDescriptor>(), Array.Empty<object?[]>(),
                RowCount: rows, sw.Elapsed,
                Message: DescribeNonQuery(rows), Error: null, Truncated: false);
        }

        var columns = ReadColumns(reader, withBaseTables);
        var rowList = new List<object?[]>();
        var truncated = false;

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (options.MaxRows is { } max && rowList.Count >= max) { truncated = true; break; }

            var row = new object?[reader.FieldCount];
            // Sync IsDBNull, deliberately: the reader isn't in sequential-access mode, so ReadAsync has
            // already buffered the whole row and the null check can't touch the socket. The async form
            // cost an awaited state-machine hop per *cell* to await an always-completed task.
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rowList.Add(row);
        }

        return new QueryResult(columns, rowList, rowList.Count, sw.Elapsed,
            Message: null, Error: null, Truncated: truncated);
    }

    private static IReadOnlyList<ColumnDescriptor> ReadColumns(SqlDataReader reader, bool withBaseTables = false)
    {
        // Column origin arrives as *names* — SqlClient has no equivalent of Postgres' table OID + attribute
        // number on the wire — and only when the command ran under CommandBehavior.KeyInfo. The id fields
        // are therefore left at 0 on purpose: ColumnDescriptor.HasBaseColumn accepts either form, but an id
        // takes precedence in the resolver, so filling both would make these names dead weight.
        var origins = withBaseTables ? OriginByOrdinal(reader.GetColumnSchema(), reader.FieldCount) : null;

        var cols = new ColumnDescriptor[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var origin = origins?[i];
            cols[i] = new ColumnDescriptor(
                reader.GetName(i), reader.GetDataTypeName(i), reader.GetFieldType(i),
                BaseSchemaName: origin?.BaseSchemaName,
                BaseTableName: origin?.BaseTableName,
                BaseColumnName: origin?.BaseColumnName,
                // T-SQL reaches another database with a three-part name, which Postgres has no analogue
                // for. Dropping the catalog let `select * from reporting.dbo.Orders` on an `app`
                // connection resolve against app.dbo.Orders — identical schemas across databases on one
                // instance being the norm — and the generated UPDATE then ran on the wrong database,
                // with a confirm dialog showing [dbo].[Orders] either way.
                BaseCatalogName: origin?.BaseCatalogName);
        }
        return cols;
    }

    /// <summary>
    /// The column schema indexed by reader ordinal. KeyInfo also asks the server for the key columns the
    /// SELECT did not list, and those come back as extra <c>IsHidden</c> entries — they are not readable
    /// fields, so matching schema entries to fields by position alone would shift origins onto the wrong
    /// columns. <see cref="DbColumn.ColumnOrdinal"/> is the authoritative mapping when the driver fills it
    /// in; visible order is the fallback the contract leaves when it doesn't.
    /// </summary>
    private static DbColumn?[] OriginByOrdinal(ReadOnlyCollection<DbColumn> schema, int fieldCount)
    {
        var byOrdinal = new DbColumn?[fieldCount];
        var visible = new List<DbColumn>(fieldCount);
        foreach (var c in schema)
        {
            if (c.IsHidden is true) continue;
            visible.Add(c);
            if (c.ColumnOrdinal is { } ordinal && ordinal >= 0 && ordinal < fieldCount)
                byOrdinal[ordinal] = c;
        }
        for (var i = 0; i < fieldCount; i++)
            byOrdinal[i] ??= i < visible.Count ? visible[i] : null;
        return byOrdinal;
    }

    /// <summary>
    /// Turns SqlClient's cumulative <see cref="SqlDataReader.RecordsAffected"/> into the per-statement count
    /// the app reports. Npgsql resets that value at every result set; SqlClient adds the whole batch up, so
    /// a naive port makes <c>update a; update b</c> claim the second statement touched both statements' rows
    /// — and the number grows with every statement after it. The increase since the previous result set is
    /// the honest answer.
    /// <para>
    /// A negative value means "no statement so far affected rows" (a SELECT-only batch, or SET NOCOUNT ON)
    /// and is passed through untouched: it is the same sentinel <see cref="DescribeNonQuery"/> already reads
    /// as "no count to report", and subtracting from it would invent one.
    /// </para>
    /// </summary>
    private sealed class BatchAffected
    {
        private int _consumed;

        public int Take(int cumulative)
        {
            if (cumulative < 0) return cumulative;
            var delta = cumulative - _consumed;
            _consumed = cumulative;
            return delta;
        }
    }

    /// <summary>
    /// A failed statement as the neutral <see cref="QueryError"/>. Two field names lie a little for SQL
    /// Server, so both choices are recorded here rather than at four catch sites:
    /// <list type="bullet">
    ///   <item><c>SqlState</c> carries the <b>error number</b> as text. SQL Server has no SQLSTATE of its
    ///     own (the five-character states are an ODBC-side mapping), and the number is what every reference,
    ///     and <c>SqlServerProvider.Classify</c>, is written against.</item>
    ///   <item><c>Position</c> stays <b>null</b>. It is a character offset — the editor puts the caret there
    ///     — and SQL Server reports a line number instead; there is no honest conversion without re-lexing
    ///     the batch, and a line number in that field would aim the caret at an arbitrary character. The
    ///     line is appended to the message instead, where it reads as what it is (and only when it says
    ///     something: line 1 of a one-line statement does not).</item>
    /// </list>
    /// The text goes through <see cref="SafeErrorText.Of"/>: a connect- or parse-time SqlException can
    /// quote connection settings, and this message reaches the results pane, the status bar and the query
    /// log (§1.1).
    /// </summary>
    private static QueryError ErrorFrom(SqlException ex)
    {
        var line = ex.Errors.Count > 0 ? ex.Errors[0].LineNumber : 0;
        var text = SafeErrorText.Of(ex);
        return new QueryError(
            line > 1 ? $"{text} (line {line})" : text,
            ex.Number.ToString(CultureInfo.InvariantCulture),
            null);
    }

    private static QueryResult Failure(TimeSpan elapsed, QueryError error) => new(
        Array.Empty<ColumnDescriptor>(), Array.Empty<object?[]>(),
        RowCount: 0, elapsed, Message: null, Error: error, Truncated: false);

    private static string DescribeNonQuery(int affected) =>
        affected >= 0 ? $"{affected} row(s) affected" : "Statement executed";
}
