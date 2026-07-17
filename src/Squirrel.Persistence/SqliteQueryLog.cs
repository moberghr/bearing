using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Squirrel.Core.Logging;

namespace Squirrel.Persistence;

/// <summary>
/// Append-only query history in SQLite with an FTS5 index over the SQL text. Writes are queued on a
/// background channel so logging never blocks execution; reads open their own (WAL) connection.
/// </summary>
public sealed class SqliteQueryLog : IQueryLog, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly Channel<QueryLogEntry> _channel;
    private readonly SqliteConnection _writeConnection;
    private readonly Task _writerLoop;

    public SqliteQueryLog(string? dbPath = null)
    {
        dbPath ??= Path.Combine(SquirrelPaths.DataDir, "query-log.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        _writeConnection = Open();
        Migrate(_writeConnection);

        _channel = Channel.CreateUnbounded<QueryLogEntry>(new UnboundedChannelOptions { SingleReader = true });
        _writerLoop = Task.Run(WriteLoopAsync);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=3000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private static void Migrate(SqliteConnection conn)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt64(check.ExecuteScalar());
        if (version >= 1) return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS query_log (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                executed_at   TEXT    NOT NULL,
                provider_id   TEXT    NOT NULL,
                connection    TEXT    NOT NULL,
                db            TEXT    NOT NULL,
                sql_text      TEXT    NOT NULL,
                duration_ms   INTEGER NOT NULL,
                row_count     INTEGER NOT NULL,
                success       INTEGER NOT NULL,
                error_message TEXT,
                script_path   TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_query_log_executed_at ON query_log(executed_at);
            CREATE VIRTUAL TABLE IF NOT EXISTS query_log_fts
                USING fts5(sql_text, content='query_log', content_rowid='id');
            CREATE TRIGGER IF NOT EXISTS query_log_ai AFTER INSERT ON query_log BEGIN
                INSERT INTO query_log_fts(rowid, sql_text) VALUES (new.id, new.sql_text);
            END;
            PRAGMA user_version = 1;
            """;
        cmd.ExecuteNonQuery();
    }

    public void Append(QueryLogEntry entry) => _channel.Writer.TryWrite(entry);

    private async Task WriteLoopAsync()
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync())
        {
            try { Insert(entry); }
            catch { /* logging must never surface an error to the app */ }
        }
    }

    /// <summary>
    /// Turn free-text into a robust FTS5 query: each alphanumeric token becomes a prefix term
    /// (implicit AND). This matches partial words ("fil" → films) and ignores punctuation like
    /// '*'/';' that would otherwise be phrase-parsed and match nothing. Null = no text filter.
    /// </summary>
    private static string? BuildFtsMatch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var terms = System.Text.RegularExpressions.Regex
            .Matches(text, "[A-Za-z0-9_]+")
            .Select(m => m.Value + "*")
            .ToList();
        return terms.Count == 0 ? null : string.Join(" ", terms);
    }

    private void Insert(QueryLogEntry e)
    {
        using var cmd = _writeConnection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO query_log
                (executed_at, provider_id, connection, db, sql_text, duration_ms, row_count, success, error_message, script_path)
            VALUES ($at, $provider, $conn, $db, $sql, $dur, $rows, $ok, $err, $script);
            """;
        cmd.Parameters.AddWithValue("$at", e.ExecutedAt.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$provider", e.ProviderId);
        cmd.Parameters.AddWithValue("$conn", e.ConnectionName);
        cmd.Parameters.AddWithValue("$db", e.Database);
        cmd.Parameters.AddWithValue("$sql", e.SqlText);
        cmd.Parameters.AddWithValue("$dur", (long)e.Duration.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$rows", e.RowCount);
        cmd.Parameters.AddWithValue("$ok", e.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("$err", (object?)e.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$script", (object?)e.ScriptPath ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public async Task<IReadOnlyList<QueryLogEntry>> SearchAsync(QueryLogQuery query, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();

        var where = new List<string>();
        var match = BuildFtsMatch(query.Text);
        if (match is not null)
        {
            cmd.Parameters.AddWithValue("$match", match);
            where.Add("q.id IN (SELECT rowid FROM query_log_fts WHERE query_log_fts MATCH $match)");
        }
        if (!string.IsNullOrWhiteSpace(query.ConnectionName))
        {
            cmd.Parameters.AddWithValue("$conn", query.ConnectionName);
            where.Add("q.connection = $conn");
        }
        if (query.SuccessOnly == true) where.Add("q.success = 1");

        cmd.Parameters.AddWithValue("$limit", query.Limit);
        cmd.CommandText =
            "SELECT q.id, q.executed_at, q.provider_id, q.connection, q.db, q.sql_text, q.duration_ms, " +
            "q.row_count, q.success, q.error_message, q.script_path FROM query_log q" +
            (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
            " ORDER BY q.id DESC LIMIT $limit;";

        var results = new List<QueryLogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new QueryLogEntry
            {
                Id = reader.GetInt64(0),
                ExecutedAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                ProviderId = reader.GetString(2),
                ConnectionName = reader.GetString(3),
                Database = reader.GetString(4),
                SqlText = reader.GetString(5),
                Duration = TimeSpan.FromMilliseconds(reader.GetInt64(6)),
                RowCount = reader.GetInt64(7),
                Success = reader.GetInt64(8) != 0,
                ErrorMessage = reader.IsDBNull(9) ? null : reader.GetString(9),
                ScriptPath = reader.IsDBNull(10) ? null : reader.GetString(10),
            });
        }
        return results;
    }

    /// <summary>Flush pending writes and close (used by tests; the app can let the process own it).</summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { await _writerLoop; } catch { }
        await _writeConnection.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }
}
