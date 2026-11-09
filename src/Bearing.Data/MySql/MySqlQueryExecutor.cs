using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Bearing.Core.Data;
using MySqlConnector;
// Bearing.Core.Data has its own driver-agnostic SqlParameter, so the driver's is aliased.
using DriverParameter = MySqlConnector.MySqlParameter;

namespace Bearing.Data.MySql;

/// <summary>
/// Executes SQL against MySQL or MariaDB and materializes (or streams) the result — the sibling of the
/// Postgres and SQL Server executors, and deliberately the same shape. Every suspending await carries
/// <c>ConfigureAwait(false)</c> for the reason the others give: this is called from the UI thread.
/// <para>
/// Column origin is cheap here and always on: the protocol sends every result column's database, physical
/// table and physical column name, so there is no <c>KeyInfo</c>-style mode to opt into and no id to map —
/// origin is by name, qualified by the database as a catalog, so a <c>select * from other_db.t</c> cannot
/// resolve to the connected database's table of the same name (the bug SQL Server's three-part names had).
/// </para>
/// </summary>
public sealed class MySqlQueryExecutor : IQueryExecutor
{
    private readonly IMySqlConnectionSource _source;

    public MySqlQueryExecutor(MySqlConnectionFactory factory) : this(new PooledMySqlConnections(factory)) { }

    internal MySqlQueryExecutor(IMySqlConnectionSource source) => _source = source;

    public async Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var results = new List<QueryResult>();
            await using var rent = await _source.RentAsync(ct).ConfigureAwait(false);
            await using var cmd = new MySqlCommand(sql, rent.Connection, rent.Transaction);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            // RecordsAffected is the running total across the reader (the ADO.NET convention), so each
            // non-row-returning statement's own count is the delta since the previous one.
            var counted = 0;
            do
            {
                var result = await ReadResultSetAsync(reader, options, sw, ct, withOrigin: true).ConfigureAwait(false);
                if (result.Columns.Count == 0)
                {
                    var total = Math.Max(reader.RecordsAffected, 0);
                    var own = total - counted;
                    counted = total;
                    result = result with { RowCount = own, Message = DescribeNonQuery(own) };
                }
                results.Add(result);
            }
            while (await reader.NextResultAsync(ct).ConfigureAwait(false));

            return results;
        }
        catch (MySqlException ex)
        {
            ThrowIfCancelled(ex, ct);
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
        var sw = Stopwatch.StartNew();
        try
        {
            await using var rent = await _source.RentAsync(ct).ConfigureAwait(false);
            await using var cmd = new MySqlCommand(pageSql, rent.Connection, rent.Transaction);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await ReadResultSetAsync(reader, new QueryOptions { MaxRows = null }, sw, ct).ConfigureAwait(false);
        }
        catch (MySqlException ex)
        {
            ThrowIfCancelled(ex, ct);
            sw.Stop();
            return Failure(sw.Elapsed, ErrorFrom(ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return Failure(sw.Elapsed, new QueryError(SafeErrorText.Of(ex), null, null));
        }
    }

    /// <summary>Streams one row-returning query in batches. Throws on failure — see the interface.</summary>
    public async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string sql, QueryOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        var batchSize = Math.Max(1, options.BatchRows);
        await using var rent = await _source.RentAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(sql, rent.Connection, rent.Transaction);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (reader.FieldCount == 0)
        {
            yield break;
        }

        var columns = ReadColumns(reader, withOrigin: false);
        var batch = new List<object?[]>(batchSize);
        var read = 0;
        var truncated = false;

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (options.MaxRows is { } max && read >= max)
            {
                truncated = true;
                break;
            }

            batch.Add(ReadRow(reader));
            read++;

            if (batch.Count >= batchSize)
            {
                yield return new RowBatch(columns, batch, Truncated: false);
                batch = new List<object?[]>(batchSize);
            }
        }

        // The tail, always — see the other executors for why it is unconditional.
        yield return new RowBatch(columns, batch, truncated);
    }

    public async Task<long?> CountAsync(string countSql, CancellationToken ct)
    {
        await using var rent = await _source.RentAsync(ct).ConfigureAwait(false);

        // Inside a held transaction (#131) the probe runs under a savepoint, as on the other engines. MySQL
        // does not doom a transaction on an ordinary error the way Postgres does, but a deadlock or a lock
        // wait timeout can roll back work, and a count of ours must never be what costs the user theirs.
        var guarded = false;
        try
        {
            if (rent.Transaction is not null)
            {
                await ExecNonQueryAsync(rent, "savepoint " + CountSavepoint, ct).ConfigureAwait(false);
                guarded = true;
            }
            await using var cmd = new MySqlCommand(countSql, rent.Connection, rent.Transaction);
            var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (guarded)
            {
                await ExecNonQueryAsync(rent, "release savepoint " + CountSavepoint, ct).ConfigureAwait(false);
            }
            return scalar is null or DBNull ? null : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            if (guarded)
            {
                await UnwindAsync(rent, CountSavepoint).ConfigureAwait(false);
            }
            if (ex is MySqlException my && IsUncountableShape(my))
            {
                return null;
            }
            throw;
        }
    }

    private const string CountSavepoint = "bearing_count_probe";
    private const string BatchSavepoint = "bearing_write_batch";

    /// <summary>
    /// The failures the count wrapper itself provokes, and nothing else: <b>1064</b> (a shape that parses
    /// on its own and not as a derived table — a batch, a non-SELECT) and <b>1060</b> (duplicate column
    /// name — a join selecting two columns of the same name is legal at the top level and illegal as a
    /// derived table, which Postgres does not mind). A lost connection, a dropped table, a permission
    /// refusal or a timeout propagate, so the UI can say the count failed rather than show no total.
    /// </summary>
    private static bool IsUncountableShape(MySqlException ex)
        => (int)ex.ErrorCode is 1064 or 1060;

    public async Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(
        IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var results = new List<QueryResult>(commands.Count);
            await using var rent = await _source.RentAsync(ct).ConfigureAwait(false);

            // MySQL has no INSERT … RETURNING at all and MariaDB only from 10.5, so the plain form is chosen
            // up front for a server that cannot read the other — it would be a syntax error, not a fallback
            // case. The grid then keeps the values the user typed (ResultEditModel.ApplySavedChanges).
            var withReturning = rent.Flavor.SupportsInsertReturning;

            var own = rent.Transaction is null
                ? await rent.Connection.BeginTransactionAsync(ct).ConfigureAwait(false)
                : null;
            var tx = rent.Transaction ?? own!;
            var guarded = rent.Transaction is not null;
            if (guarded)
            {
                await ExecNonQueryAsync(rent, "savepoint " + BatchSavepoint, ct).ConfigureAwait(false);
            }

            try
            {
                foreach (var c in commands)
                {
                    var sql = withReturning ? c.Sql : c.SqlWithoutReturning ?? c.Sql;
                    await using var cmd = new MySqlCommand(sql, rent.Connection, tx);
                    foreach (var p in c.Parameters)
                    {
                        cmd.Parameters.Add(new DriverParameter(p.Name, p.Value ?? DBNull.Value));
                    }
                    // Prepared, so the values travel bound in the binary protocol. Unprepared, MySqlConnector
                    // inlines them as escaped literals — and how a backslash must be escaped depends on the
                    // server's sql_mode (NO_BACKSLASH_ESCAPES), which the driver does not read: under that mode
                    // a key of 'C:\' would match nothing and a typed value would gain a backslash.
                    if (c.Parameters.Count > 0)
                    {
                        await cmd.PrepareAsync(ct).ConfigureAwait(false);
                    }
                    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    results.Add(await ReadResultSetAsync(reader, new QueryOptions { MaxRows = null }, sw, ct)
                        .ConfigureAwait(false));
                }
                if (own is not null)
                {
                    await own.CommitAsync(ct).ConfigureAwait(false);
                }
                else if (guarded)
                {
                    await ExecNonQueryAsync(rent, "release savepoint " + BatchSavepoint, ct).ConfigureAwait(false);
                }
                return results;
            }
            catch when (guarded)
            {
                // Put the user's transaction back where this batch found it; the failure still travels.
                await UnwindAsync(rent, BatchSavepoint).ConfigureAwait(false);
                throw;
            }
            finally
            {
                if (own is not null)
                {
                    await own.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (MySqlException ex)
        {
            ThrowIfCancelled(ex, ct);
            sw.Stop();
            return new[] { Failure(sw.Elapsed, ErrorFrom(ex)) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new[] { Failure(sw.Elapsed, new QueryError(SafeErrorText.Of(ex), null, null)) };
        }
    }

    /// <summary>Roll back to a savepoint, best-effort and uncancellable — the unwinding must happen even
    /// when the thing that failed was a cancellation.</summary>
    private static async Task UnwindAsync(MySqlRent rent, string savepoint)
    {
        try
        {
            await ExecNonQueryAsync(rent, "rollback to savepoint " + savepoint, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // The transaction is past saving either way; the original failure is the one to report.
        }
    }

    private static async Task ExecNonQueryAsync(MySqlRent rent, string sql, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, rent.Connection, rent.Transaction);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A cancellation the caller asked for surfaces as <see cref="OperationCanceledException"/>, as on the
    /// other engines — MySqlConnector reports the server's "query execution was interrupted" (1317) when the
    /// token fired, and that is the user's Esc, not a failure to show.
    /// </summary>
    private static void ThrowIfCancelled(MySqlException ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested && (int)ex.ErrorCode == 1317)
        {
            throw new OperationCanceledException(ex.Message, ex, ct);
        }
    }

    private static async Task<QueryResult> ReadResultSetAsync(
        MySqlDataReader reader, QueryOptions options, Stopwatch sw, CancellationToken ct, bool withOrigin = false)
    {
        if (reader.FieldCount == 0)
        {
            var rows = reader.RecordsAffected;
            return new QueryResult(
                Array.Empty<ColumnDescriptor>(), Array.Empty<object?[]>(),
                RowCount: rows, sw.Elapsed, Message: DescribeNonQuery(rows), Error: null, Truncated: false);
        }

        var columns = ReadColumns(reader, withOrigin);
        var rowList = new List<object?[]>();
        var truncated = false;

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (options.MaxRows is { } max && rowList.Count >= max)
            {
                truncated = true;
                break;
            }
            rowList.Add(ReadRow(reader));
        }

        return new QueryResult(columns, rowList, rowList.Count, sw.Elapsed,
            Message: null, Error: null, Truncated: truncated);
    }

    /// <summary>One row. Sync <c>IsDBNull</c>: the row is buffered by <c>ReadAsync</c> already.</summary>
    private static object?[] ReadRow(MySqlDataReader reader)
    {
        var row = new object?[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }
        return row;
    }

    private static IReadOnlyList<ColumnDescriptor> ReadColumns(MySqlDataReader reader, bool withOrigin)
    {
        var schema = withOrigin ? reader.GetColumnSchema() : null;
        var cols = new ColumnDescriptor[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var origin = schema is not null && i < schema.Count ? schema[i] : null;
            var table = origin?.BaseTableName is { Length: > 0 } t ? t : null;
            var database = table is null ? null : origin?.BaseSchemaName is { Length: > 0 } d ? d : null;
            cols[i] = new ColumnDescriptor(
                reader.GetName(i), reader.GetDataTypeName(i), reader.GetFieldType(i),
                BaseSchemaName: database,
                BaseTableName: table,
                BaseColumnName: table is null ? null : BaseColumnNameOf(origin!, reader.GetName(i)),
                // The database is MySQL's catalog and schema at once. Carried as the catalog too so the
                // resolver refuses a column from another database rather than matching the connected one's
                // table of the same name — the generated UPDATE would otherwise run against the wrong table.
                BaseCatalogName: database);
        }
        return cols;
    }

    /// <summary>The physical column name, or the exposed one when the driver gave none — the column was not
    /// renamed, so its exposed name is its own.</summary>
    private static string BaseColumnNameOf(DbColumn origin, string exposedName)
        => string.IsNullOrEmpty(origin.BaseColumnName) ? exposedName : origin.BaseColumnName;

    /// <summary>
    /// A failed statement as the neutral <see cref="QueryError"/>. <c>SqlState</c> carries the MySQL
    /// <b>error number</b> rather than the SQLSTATE, the choice SQL Server's executor makes for the same
    /// reason: the five-character state is coarse here (dozens of errors share <c>HY000</c>), while the number
    /// is what every reference and <c>MySqlProvider.Classify</c> are written against. Text through
    /// <see cref="SafeErrorText.Of"/> (§1.1).
    /// </summary>
    private static QueryError ErrorFrom(MySqlException ex)
        => new(SafeErrorText.Of(ex), ((int)ex.ErrorCode).ToString(CultureInfo.InvariantCulture), null);

    private static QueryResult Failure(TimeSpan elapsed, QueryError error) => new(
        Array.Empty<ColumnDescriptor>(), Array.Empty<object?[]>(),
        RowCount: 0, elapsed, Message: null, Error: error, Truncated: false);

    private static string DescribeNonQuery(int affected)
        => affected >= 0 ? $"{affected} row(s) affected" : "Statement executed";
}
