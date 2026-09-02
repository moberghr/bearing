using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Bearing.Core.Logging;

namespace Bearing.Persistence;

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
    private readonly Func<string, string>? _redact;

    /// <param name="redactSql">
    /// Applied to every entry's SQL before it is written (#22), or null to store it verbatim. A delegate
    /// rather than a call into the redactor, because that lives in <c>Bearing.Sql</c> and this project depends
    /// on <c>Core</c> alone (§2.2) — and because what counts as a literal is a dialect question, not a storage
    /// one.
    /// </param>
    public SqliteQueryLog(string? dbPath = null, int retentionDays = 0, Func<string, string>? redactSql = null)
    {
        dbPath ??= Path.Combine(BearingPaths.DataDir, "query-log.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        _redact = redactSql;

        _writeConnection = Open();
        Migrate(_writeConnection);
        Prune(_writeConnection, retentionDays);
        // After the schema exists, so there is a file (and a -wal) to narrow. Best-effort: a filesystem that
        // cannot express the mode must not stop the app from starting (§5.2).
        Hardening = LocalFilePermissions.HardenDatabase(dbPath);

        _channel = Channel.CreateUnbounded<QueryLogEntry>(new UnboundedChannelOptions { SingleReader = true });
        _writerLoop = Task.Run(WriteLoopAsync);
    }

    /// <summary>
    /// Drop history older than <paramref name="retentionDays"/> (≤0 keeps everything). Compared with
    /// <c>julianday()</c> so it's correct regardless of the stored timestamps' timezone offsets, then
    /// the external-content FTS index is rebuilt to shed the deleted rows.
    /// </summary>
    private static void Prune(SqliteConnection conn, int retentionDays)
    {
        if (retentionDays <= 0) return;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("o", CultureInfo.InvariantCulture);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM query_log WHERE julianday(executed_at) < julianday($cutoff);
            INSERT INTO query_log_fts(query_log_fts) VALUES('rebuild');
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        cmd.ExecuteNonQuery();
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

    /// <summary>What the log's own files ended up permitted to (#22). Reported rather than assumed, so the
    /// posture can be surfaced instead of claimed.</summary>
    public FileHardening Hardening { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Redaction happens here, at the boundary, rather than in the insert: the entry that reaches the
    /// database, the <see cref="Appended"/> subscribers and the history panel is then one consistent record.
    /// A panel showing the verbatim SQL of a row stored redacted would be the more misleading of the two.
    /// </remarks>
    public void Append(QueryLogEntry entry)
        => _channel.Writer.TryWrite(_redact is null ? entry : Redacted(entry));

    /// <summary>
    /// The entry with its SQL redacted. The error message is left alone: a driver quotes the offending
    /// <i>value</i> in some messages, but it also carries the SQLSTATE and position that make a logged failure
    /// worth keeping, and it has already been through <c>SafeErrorText</c> for credentials (§1.1).
    /// </summary>
    private QueryLogEntry Redacted(QueryLogEntry entry)
    {
        try { return entry with { SqlText = _redact!(entry.SqlText) }; }
        // A redactor that throws must not put the verbatim SQL in the log instead — that is the one outcome
        // this feature exists to prevent. Store the shape of the statement and nothing else.
        catch (Exception) { return entry with { SqlText = "(redaction failed)" }; }
    }

    /// <inheritdoc />
    public event Action<QueryLogEntry>? Appended;

    private async Task WriteLoopAsync()
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { Insert(entry); }
            catch { continue; /* logging must never surface an error to the app */ }

            // After the insert, never before: a subscriber's whole reason to listen is that the row is now
            // readable. Raised on this loop's thread, and a throwing subscriber must not take the writer
            // down with it — the log would then silently stop recording.
            try { Appended?.Invoke(entry); }
            catch { /* a listener's problem is not the log's */ }
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
        await conn.OpenAsync(ct).ConfigureAwait(false);
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
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
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

    /// <summary>
    /// Flush pending writes and close.
    /// <para>
    /// The pool is released too, and that is the part that matters to a caller who then wants the file gone:
    /// reads open their own connection and hand it back to Microsoft.Data.Sqlite's pool on dispose, so the
    /// handle outlives this object and a delete fails with "used by another process". Only this log's pool —
    /// keyed by its own connection string — so a second store on another file is untouched.
    /// </para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { await _writerLoop.ConfigureAwait(false); } catch { }
        await _writeConnection.DisposeAsync().ConfigureAwait(false);
        try { SqliteConnection.ClearPool(new SqliteConnection(_connectionString)); } catch { /* best-effort */ }
    }
}
