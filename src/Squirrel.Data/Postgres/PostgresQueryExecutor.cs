using System.Diagnostics;
using Npgsql;
using Squirrel.Core.Data;

namespace Squirrel.Data.Postgres;

/// <summary>Executes SQL against PostgreSQL and materializes (or streams) the result.</summary>
public sealed class PostgresQueryExecutor : IQueryExecutor
{
    private readonly NpgsqlConnectionFactory _factory;

    public PostgresQueryExecutor(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<QueryResult> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await _factory.DataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            // A non-row-returning statement (INSERT/UPDATE/DDL) still reports affected rows.
            if (reader.FieldCount == 0)
            {
                sw.Stop();
                return new QueryResult(
                    Array.Empty<ColumnDescriptor>(), Array.Empty<object?[]>(),
                    RowCount: reader.RecordsAffected, sw.Elapsed,
                    Message: DescribeNonQuery(reader.RecordsAffected), Error: null, Truncated: false);
            }

            var columns = ReadColumns(reader);
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

            sw.Stop();
            return new QueryResult(columns, rows, rows.Count, sw.Elapsed,
                Message: null, Error: null, Truncated: truncated);
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

    public async IAsyncEnumerable<ResultBatch> StreamAsync(
        string sql, QueryOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (reader.FieldCount == 0) yield break;

        var columns = ReadColumns(reader);
        const int batchSize = 500;
        var batch = new List<object?[]>(batchSize);

        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
            batch.Add(row);

            if (batch.Count >= batchSize)
            {
                yield return new ResultBatch(columns, batch);
                batch = new List<object?[]>(batchSize);
            }
        }

        if (batch.Count > 0)
            yield return new ResultBatch(columns, batch);
    }

    private static IReadOnlyList<ColumnDescriptor> ReadColumns(NpgsqlDataReader reader)
    {
        var cols = new ColumnDescriptor[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            cols[i] = new ColumnDescriptor(reader.GetName(i), reader.GetDataTypeName(i), reader.GetFieldType(i));
        return cols;
    }

    private static QueryResult Failure(TimeSpan elapsed, QueryError error) => new(
        Array.Empty<ColumnDescriptor>(), Array.Empty<object?[]>(),
        RowCount: 0, elapsed, Message: null, Error: error, Truncated: false);

    private static string DescribeNonQuery(int affected) =>
        affected >= 0 ? $"{affected} row(s) affected" : "Statement executed";
}
