using Bearing.Core.Logging;
using Bearing.Persistence;
using Xunit;

namespace Bearing.Persistence.Tests;

/// <summary>
/// What the query log does and does not leave lying about (#22): the file's permissions, and whether the SQL
/// it stores still carries the values it was run with.
/// </summary>
public class QueryLogPrivacyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bearing-qlp", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "query-log.sqlite");

    public QueryLogPrivacyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static QueryLogEntry Entry(string sql) => new()
    {
        ExecutedAt = DateTimeOffset.UtcNow,
        ProviderId = "postgres",
        ConnectionName = "prod",
        Database = "app",
        SqlText = sql,
        Duration = TimeSpan.FromMilliseconds(3),
        RowCount = 1,
        Success = true,
    };

    /// <summary>Append and wait for the background writer to have stored it.</summary>
    private static async Task<QueryLogEntry> RoundTripAsync(SqliteQueryLog log, string sql)
    {
        log.Append(Entry(sql));
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var rows = await log.SearchAsync(new QueryLogQuery(), CancellationToken.None);
            if (rows.Count > 0) return rows[0];
            await Task.Delay(20);
        }
        throw new InvalidOperationException("the entry was never written");
    }

    // ---- redaction ------------------------------------------------------------------------------

    [Fact]
    public async Task Without_redaction_the_sql_is_stored_verbatim()
    {
        // The documented default (§1.3), asserted so a change to it is deliberate rather than incidental.
        await using var log = new SqliteQueryLog(DbPath);

        var stored = await RoundTripAsync(log, "select * from t where email = 'ada@example.com'");

        Assert.Contains("ada@example.com", stored.SqlText);
    }

    [Fact]
    public async Task With_redaction_the_value_never_reaches_the_file()
    {
        // Not just the row the panel reads back — the bytes. A redaction that only hid the value from the UI
        // would leave the log exactly as exposed as before.
        await using (var log = new SqliteQueryLog(DbPath, redactSql: Fake))
        {
            var stored = await RoundTripAsync(log, "select * from t where email = 'ada@example.com'");
            Assert.DoesNotContain("ada@example.com", stored.SqlText);
        }

        // Sharing read *and* write: Microsoft.Data.Sqlite pools its connections, so the handle outlives the
        // dispose above and an exclusive open fails with "used by another process" — the same trap that made
        // #83 look like an autosave bug.
        Assert.DoesNotContain("ada@example.com", await ReadAllTextSharedAsync(DbPath));

        // A stand-in for the real redactor, which lives in Bearing.Sql — this project depends on Core alone.
        static string Fake(string sql) => sql.Replace("'ada@example.com'", "'?'");
    }

    [Fact]
    public async Task Subscribers_see_the_same_text_that_was_stored()
    {
        // A history panel showing the verbatim SQL of a row stored redacted would be the more misleading of
        // the two, so redaction happens at the boundary rather than in the insert.
        await using var log = new SqliteQueryLog(DbPath, redactSql: _ => "select '?'");
        var seen = new TaskCompletionSource<string>();
        log.Appended += e => seen.TrySetResult(e.SqlText);

        var stored = await RoundTripAsync(log, "select 'secret'");

        Assert.Equal("select '?'", stored.SqlText);
        Assert.Equal("select '?'", await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_redactor_that_throws_does_not_fall_back_to_the_verbatim_sql()
    {
        // The one outcome this feature exists to prevent. Storing the original because redaction failed would
        // be a silent opt-out at exactly the wrong moment.
        await using var log = new SqliteQueryLog(DbPath, redactSql: _ => throw new InvalidOperationException("boom"));

        var stored = await RoundTripAsync(log, "select * from t where email = 'ada@example.com'");

        Assert.DoesNotContain("ada@example.com", stored.SqlText);
        Assert.Equal("(redaction failed)", stored.SqlText);
    }

    [Fact]
    public async Task Redaction_does_not_stop_the_log_being_searchable()
    {
        // The FTS index is built over the stored text, so a redacted log still finds statements by shape —
        // which is what makes the setting usable rather than just safe.
        await using var log = new SqliteQueryLog(DbPath, redactSql: s => s.Replace("'ada'", "'?'"));
        await RoundTripAsync(log, "select * from customers where name = 'ada'");

        var hits = await log.SearchAsync(new QueryLogQuery { Text = "customers" }, CancellationToken.None);

        Assert.Single(hits);
    }

    /// <summary>Read a file that something else still holds open.</summary>
    private static async Task<string> ReadAllTextSharedAsync(string path)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    // ---- file permissions -----------------------------------------------------------------------

    [Fact]
    public async Task The_log_reports_what_its_files_ended_up_permitted_to()
    {
        // Reported rather than assumed: claiming to have hardened something is worse than saying the platform
        // did it.
        await using var log = new SqliteQueryLog(DbPath);

        Assert.Equal(
            OperatingSystem.IsWindows() ? FileHardening.PlatformDefault : FileHardening.OwnerOnly,
            log.Hardening);
    }

    [SkippableFact]
    public async Task On_unix_the_log_and_its_wal_are_owner_only()
    {
        // The -wal file holds committed pages that have not been checkpointed yet, so a log hardened without
        // it is a log whose most recent entries are still world-readable.
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes; %LOCALAPPDATA% is already ACL'd on Windows");

        await using var log = new SqliteQueryLog(DbPath);
        await RoundTripAsync(log, "select 1");

        foreach (var path in new[] { DbPath, DbPath + "-wal" }.Where(File.Exists))
        {
            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    [Fact]
    public void Hardening_a_file_that_is_not_there_says_so_rather_than_throwing()
    {
        // Persistence is best-effort throughout (§5.2): a missing file is an outcome, not a crash.
        var outcome = LocalFilePermissions.Harden(Path.Combine(_dir, "nope.sqlite"));

        Assert.Equal(
            OperatingSystem.IsWindows() ? FileHardening.PlatformDefault : FileHardening.Missing,
            outcome);
    }
}
