using System.Data.Common;
using System.Diagnostics;
using Npgsql;
using Npgsql.Schema;
using Bearing.Core.Data;

namespace Bearing.Data.Postgres;

/// <summary>Executes SQL against PostgreSQL and materializes (or streams) the result.</summary>
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
            await using var conn = await _factory.DataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            // One QueryResult per statement's result set — NextResult walks a multi-statement batch.
            // withBaseTables: capture column origin (schema/table/column) for FK-nav + inline edit.
            do
            {
                results.Add(await ReadResultSetAsync(reader, options, sw, ct, withBaseTables: true));
            }
            while (await reader.NextResultAsync(ct));

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
            return new[] { Failure(sw.Elapsed, new QueryError(ex.Message, null, null)) };
        }
    }

    public async Task<QueryResult> ExecutePageAsync(string pageSql, CancellationToken ct)
    {
        // The caller (PageSql) already shaped the paging; we just run it as one uncapped result set.
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await _factory.DataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(pageSql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await ReadResultSetAsync(reader, new QueryOptions { MaxRows = null }, sw, ct);
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
            return Failure(sw.Elapsed, new QueryError(ex.Message, null, null));
        }
    }

    public async Task<long?> CountAsync(string sql, CancellationToken ct)
    {
        var wrapped = $"select count(*) from (\n{StripTrailingSemicolon(sql)}\n) as _sq";
        try
        {
            await using var conn = await _factory.DataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(wrapped, conn);
            var scalar = await cmd.ExecuteScalarAsync(ct);
            return scalar is null or DBNull ? null : Convert.ToInt64(scalar);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // uncountable (e.g. multi-statement or non-SELECT) — caller just hides the total
        }
    }

    public async Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(
        IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<QueryResult>(commands.Count);
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct);
        // One transaction for the whole batch: any failure disposes the tx uncommitted → rollback.
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (var c in commands)
            {
                await using var cmd = new NpgsqlCommand(c.Sql, conn, tx);
                foreach (var p in c.Parameters)
                    cmd.Parameters.Add(new NpgsqlParameter(p.Name, p.Value ?? DBNull.Value));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                results.Add(await ReadResultSetAsync(reader, new QueryOptions { MaxRows = null }, sw, ct));
            }
            await tx.CommitAsync(ct);
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
            return new[] { Failure(sw.Elapsed, new QueryError(ex.Message, null, null)) };
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

        while (await reader.ReadAsync(ct))
        {
            if (options.MaxRows is { } max && rows.Count >= max) { truncated = true; break; }

            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
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
