using System.Linq;
using Bearing.Core.Logging;
using Bearing.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Bearing.Persistence.Tests;

/// <summary>
/// What #113 needed from the store: a connection id and an environment on every new row, a date range, an
/// unbounded read — and, the part that is easy to get wrong, an existing log that keeps every row it had.
/// </summary>
public class QueryLogAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bearing-audit-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    private static QueryLogEntry Entry(
        DateTimeOffset at,
        string sql = "select 1",
        string connection = "prod",
        Guid? id = null,
        string? environment = null) => new()
        {
            ExecutedAt = at,
            ProviderId = "postgres",
            ConnectionName = connection,
            ConnectionId = id,
            Environment = environment,
            Database = "pagila",
            SqlText = sql,
            Duration = TimeSpan.FromMilliseconds(7),
            RowCount = 1,
            Success = true,
        };

    private static async Task<IReadOnlyList<QueryLogEntry>> Read(SqliteQueryLog log, QueryLogQuery query)
        => await log.SearchAsync(query, CancellationToken.None);

    /// <summary>
    /// Wait until the background writer has landed <paramref name="expected"/> rows.
    /// <para>
    /// Driven by the log's own <see cref="IQueryLog.Appended"/> event, which fires once a row is actually
    /// readable (#78) — so this waits exactly as long as the writer needs and no longer. It used to poll on
    /// a fixed 2-second budget, which was ample for a few rows on a fast disk and not for 250 on a shared
    /// CI runner: the 250-row test passed on every machine here and failed the first time CI ran it. A
    /// timing budget guessed from one machine is not a wait.
    /// </para>
    /// <para>
    /// The subscription has to be in place <b>before</b> the appends, so callers hand the entries over
    /// rather than appending first — an event that has already fired cannot be waited for.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<QueryLogEntry>> Appended(
        SqliteQueryLog log, params QueryLogEntry[] entries)
    {
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = 0;
        void OnAppended(QueryLogEntry _)
        {
            if (Interlocked.Increment(ref seen) >= entries.Length) landed.TrySetResult();
        }

        log.Appended += OnAppended;
        try
        {
            foreach (var entry in entries) log.Append(entry);
            var timeout = Task.Delay(TimeSpan.FromSeconds(60));
            if (await Task.WhenAny(landed.Task, timeout).ConfigureAwait(false) == timeout)
                Assert.Fail($"only {seen} of {entries.Length} entries reached the log");
        }
        finally
        {
            log.Appended -= OnAppended;
        }

        return await Read(log, new QueryLogQuery { Limit = null });
    }

    [Fact]
    public async Task The_connection_id_and_environment_round_trip()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid();
        await using var log = new SqliteQueryLog(Path_("log.sqlite"));

        var row = Assert.Single(
            await Appended(log, Entry(DateTimeOffset.UtcNow, id: id, environment: "Production")));

        Assert.Equal(id, row.ConnectionId);
        Assert.Equal("Production", row.Environment);
    }

    [Fact]
    public async Task A_connection_with_no_environment_stores_null_rather_than_an_empty_label()
    {
        // An empty string would show up in a report as an environment named "", which reads as a value
        // rather than as an absence.
        Directory.CreateDirectory(_dir);
        await using var log = new SqliteQueryLog(Path_("log.sqlite"));

        var row = Assert.Single(await Appended(log, Entry(DateTimeOffset.UtcNow, id: Guid.NewGuid())));

        Assert.Null(row.Environment);
    }

    /// <summary>
    /// The migration's real job: an existing v1 log keeps its rows, and they read back with a null id rather
    /// than failing. This is the case a rebuild-instead-of-migrate would silently destroy — the user's own
    /// record of what they ran.
    /// </summary>
    [Fact]
    public async Task An_existing_v1_log_keeps_its_rows_and_reads_them_with_no_connection_id()
    {
        Directory.CreateDirectory(_dir);
        var path = Path_("legacy.sqlite");
        WriteV1Log(path, sql: "delete from rental where rental_id = 1", connection: "old-name");

        await using var log = new SqliteQueryLog(path);
        var rows = await Read(log, new QueryLogQuery { Limit = null });

        var row = Assert.Single(rows);
        Assert.Equal("delete from rental where rental_id = 1", row.SqlText);
        Assert.Equal("old-name", row.ConnectionName);
        Assert.Null(row.ConnectionId);      // there was no column to read it from
        Assert.Null(row.Environment);
        Assert.Equal(2, SchemaVersion(path));
    }

    [Fact]
    public async Task A_migrated_log_accepts_new_rows_with_ids_beside_the_old_ones()
    {
        Directory.CreateDirectory(_dir);
        var path = Path_("mixed.sqlite");
        WriteV1Log(path, sql: "select 1", connection: "old-name");

        var id = Guid.NewGuid();
        await using var log = new SqliteQueryLog(path);

        var rows = await Appended(log, Entry(DateTimeOffset.UtcNow, sql: "select 2", id: id, environment: "Staging"));
        Assert.Equal(2, rows.Count);   // the v1 row plus this one
        // Newest first, as SearchAsync orders.
        Assert.Equal(id, rows[0].ConnectionId);
        Assert.Null(rows[1].ConnectionId);
    }

    [Fact]
    public async Task Opening_the_same_log_twice_does_not_re_run_the_migration()
    {
        // ALTER TABLE ADD COLUMN is not idempotent, so a version check that failed to stick would throw on
        // the second open — which is every launch after the upgrade.
        Directory.CreateDirectory(_dir);
        var path = Path_("twice.sqlite");
        await using (var first = new SqliteQueryLog(path)) { }
        await using var second = new SqliteQueryLog(path);

        Assert.Equal(2, SchemaVersion(path));
        Assert.Empty(await Read(second, new QueryLogQuery { Limit = null }));
    }

    /// <summary>
    /// The migration step is one transaction, so a half-applied schema cannot happen.
    /// <para>
    /// Simulated the only way the outcome can be: by applying the <em>first</em> ALTER by hand and leaving
    /// <c>user_version</c> at 1, which is exactly what an interrupted non-transactional batch left behind.
    /// The old code then threw "duplicate column name" on every subsequent start — a log that could not be
    /// opened at all, which is the crash §5.2 says persistence must never cause.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_log_left_half_migrated_by_an_older_build_still_opens()
    {
        Directory.CreateDirectory(_dir);
        var path = Path_("half.sqlite");
        WriteV1Log(path, sql: "select 1", connection: "old-name");

        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // The first of the two ALTERs, and no version bump.
            cmd.CommandText = "ALTER TABLE query_log ADD COLUMN connection_id TEXT;";
            cmd.ExecuteNonQuery();
            SqliteConnection.ClearPool(conn);
        }

        // Opening must not throw, and the row that was already there must survive.
        await using var log = new SqliteQueryLog(path);
        var rows = await Read(log, new QueryLogQuery { Limit = null });

        Assert.Single(rows);
        Assert.Equal("select 1", rows[0].SqlText);
        Assert.Equal(2, SchemaVersion(path));
    }

    [Fact]
    public async Task A_date_range_keeps_only_what_ran_inside_it()
    {
        Directory.CreateDirectory(_dir);
        await using var log = new SqliteQueryLog(Path_("range.sqlite"));

        var day = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        await Appended(
            log,
            Entry(day.AddDays(-2), sql: "select 'before'"),
            Entry(day, sql: "select 'inside'"),
            Entry(day.AddDays(2), sql: "select 'after'"));

        var inside = await Read(log, new QueryLogQuery
        {
            From = day.AddHours(-1),
            To = day.AddHours(1),
            Limit = null,
        });

        Assert.Equal(["select 'inside'"], inside.Select(e => e.SqlText));
    }

    /// <summary>
    /// The bound comparison has to be on instants, not on the stored text: an execution recorded at
    /// <c>00:30+02:00</c> happened <em>before</em> one recorded at <c>01:00Z</c>, and string ordering says
    /// the opposite. The retention prune already uses julianday for this reason.
    /// </summary>
    [Fact]
    public async Task Offsets_are_compared_as_instants_not_as_text()
    {
        Directory.CreateDirectory(_dir);
        await using var log = new SqliteQueryLog(Path_("offsets.sqlite"));

        // Both are the same instant: 22:30 UTC on the 2nd.
        var withOffset = new DateTimeOffset(2026, 9, 3, 0, 30, 0, TimeSpan.FromHours(2));
        await Appended(log, Entry(withOffset, sql: "select 'shifted'"));

        var utc = withOffset.ToUniversalTime();
        var found = await Read(log, new QueryLogQuery
        {
            From = utc.AddMinutes(-1),
            To = utc.AddMinutes(1),
            Limit = null,
        });
        Assert.Equal(["select 'shifted'"], found.Select(e => e.SqlText));

        // …and a window around its *local* wall-clock reading finds nothing, which is the correct answer
        // and the one a text comparison would get wrong.
        var missed = await Read(log, new QueryLogQuery
        {
            From = new DateTimeOffset(2026, 9, 3, 0, 29, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 9, 3, 0, 31, 0, TimeSpan.Zero),
            Limit = null,
        });
        Assert.Empty(missed);
    }

    [Fact]
    public async Task A_null_limit_reads_the_whole_period_and_the_default_still_caps()
    {
        Directory.CreateDirectory(_dir);
        await using var log = new SqliteQueryLog(Path_("limit.sqlite"));

        var at = DateTimeOffset.UtcNow;
        await Appended(
            log,
            [.. Enumerable.Range(0, 250).Select(i => Entry(at.AddSeconds(-i), sql: $"select {i}"))]);

        Assert.Equal(250, (await Read(log, new QueryLogQuery { Limit = null })).Count);
        // The history panel's screenful is unchanged — an audit export must not have redefined it.
        Assert.Equal(200, (await Read(log, new QueryLogQuery())).Count);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <summary>
    /// A log in exactly the shape v1 wrote, with one row in it. Hand-built rather than produced by an older
    /// build, so the migration is tested against the schema it actually has to upgrade.
    /// </summary>
    private static void WriteV1Log(string path, string sql, string connection)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE query_log (
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
            CREATE INDEX ix_query_log_executed_at ON query_log(executed_at);
            CREATE VIRTUAL TABLE query_log_fts
                USING fts5(sql_text, content='query_log', content_rowid='id');
            CREATE TRIGGER query_log_ai AFTER INSERT ON query_log BEGIN
                INSERT INTO query_log_fts(rowid, sql_text) VALUES (new.id, new.sql_text);
            END;
            PRAGMA user_version = 1;
            """;
        cmd.ExecuteNonQuery();

        using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO query_log
                (executed_at, provider_id, connection, db, sql_text, duration_ms, row_count, success)
            VALUES ($at, 'postgres', $conn, 'pagila', $sql, 9, 1, 1);
            """;
        insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("o"));
        insert.Parameters.AddWithValue("$conn", connection);
        insert.Parameters.AddWithValue("$sql", sql);
        insert.ExecuteNonQuery();
    }

    private static long SchemaVersion(string path)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt64(cmd.ExecuteScalar());
        SqliteConnection.ClearPool(conn);
        return version;
    }
}
