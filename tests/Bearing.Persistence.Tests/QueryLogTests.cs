using Bearing.Core.Logging;
using Bearing.Persistence;
using Xunit;

namespace Bearing.Persistence.Tests;

public class QueryLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bearing-log-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private QueryLogEntry Entry(string sql, bool ok = true, string? err = null, string? script = null) => new()
    {
        ExecutedAt = DateTimeOffset.UtcNow,
        ProviderId = "postgres",
        ConnectionName = "prod",
        Database = "pagila",
        SqlText = sql,
        Duration = TimeSpan.FromMilliseconds(12),
        RowCount = ok ? 5 : 0,
        Success = ok,
        ErrorMessage = err,
        ScriptPath = script,
    };

    [Fact]
    public async Task Appended_fires_only_once_the_row_can_be_read()
    {
        // The whole point of the event (#78): Append hands the entry to a background writer and returns, so
        // a listener that reloads on Append races the insert. By the time this fires, the row is there.
        await using var log = new SqliteQueryLog(_dir);
        var seen = new TaskCompletionSource<QueryLogEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<QueryLogEntry> atNotification = [];
        log.Appended += async e =>
        {
            atNotification = await log.SearchAsync(new QueryLogQuery(), CancellationToken.None);
            seen.TrySetResult(e);
        };

        log.Append(Entry("select 1 from readable_now"));

        var entry = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("select 1 from readable_now", entry.SqlText);
        Assert.Contains(atNotification, e => e.SqlText == "select 1 from readable_now");
    }

    [Fact]
    public async Task Appended_fires_once_per_entry()
    {
        await using var log = new SqliteQueryLog(_dir);
        var count = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        log.Appended += _ => { if (Interlocked.Increment(ref count) == 3) all.TrySetResult(); };

        log.Append(Entry("select 1"));
        log.Append(Entry("select 2"));
        log.Append(Entry("select 3"));

        await all.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, Volatile.Read(ref count));
    }

    [Fact]
    public async Task A_throwing_listener_does_not_stop_the_log()
    {
        // A listener's problem must not take the writer down: the log would then silently stop recording,
        // and nothing would report it.
        await using var log = new SqliteQueryLog(_dir);
        log.Appended += _ => throw new InvalidOperationException("listener is broken");

        log.Append(Entry("select 1 from before"));
        log.Append(Entry("select 2 from after"));

        var found = await SearchUntil(log, new QueryLogQuery(), 2);
        Assert.Equal(2, found.Count);
    }

    private static async Task<IReadOnlyList<QueryLogEntry>> SearchUntil(
        IQueryLog log, QueryLogQuery q, int expected)
    {
        for (var i = 0; i < 50; i++)
        {
            var r = await log.SearchAsync(q, CancellationToken.None);
            if (r.Count >= expected) return r;
            await Task.Delay(20);
        }
        return await log.SearchAsync(q, CancellationToken.None);
    }

    [Fact]
    public async Task Appends_and_full_text_searches_across_history()
    {
        var path = Path.Combine(_dir, "log.sqlite");
        await using var log = new SqliteQueryLog(path);

        log.Append(Entry("select * from film"));
        log.Append(Entry("select * from orders where total > 100", script: "scripts/orders.sql"));
        log.Append(Entry("select bogus from nope", ok: false, err: "relation does not exist"));

        // All three recorded, newest first.
        var all = await SearchUntil(log, new QueryLogQuery(), expected: 3);
        Assert.Equal(3, all.Count);
        Assert.Equal("select bogus from nope", all[0].SqlText); // most recent

        // FTS over SQL text.
        var orders = await log.SearchAsync(new QueryLogQuery { Text = "orders" }, CancellationToken.None);
        Assert.Single(orders);
        Assert.Equal("scripts/orders.sql", orders[0].ScriptPath);

        // Structured filter: successes only excludes the error row.
        var ok = await log.SearchAsync(new QueryLogQuery { SuccessOnly = true }, CancellationToken.None);
        Assert.Equal(2, ok.Count);
        Assert.All(ok, e => Assert.True(e.Success));

        // Error row preserves its message.
        var err = all.Single(e => !e.Success);
        Assert.Equal("relation does not exist", err.ErrorMessage);
    }

    [Fact]
    public async Task Search_matches_partial_words_and_tolerates_punctuation()
    {
        var path = Path.Combine(_dir, "fts.sqlite");
        await using var log = new SqliteQueryLog(path);
        log.Append(Entry("select * from film join film_actor using (film_id)"));
        await SearchUntil(log, new QueryLogQuery(), expected: 1);

        // Partial token ("fil" → film / film_actor / film_id).
        Assert.Single(await log.SearchAsync(new QueryLogQuery { Text = "fil" }, CancellationToken.None));
        // Punctuation-heavy input must not throw and still matches on its tokens.
        Assert.Single(await log.SearchAsync(new QueryLogQuery { Text = "select * from film" }, CancellationToken.None));
        // Multiple tokens are AND-ed.
        Assert.Single(await log.SearchAsync(new QueryLogQuery { Text = "film actor" }, CancellationToken.None));
        // A token that isn't present yields nothing.
        Assert.Empty(await log.SearchAsync(new QueryLogQuery { Text = "customer" }, CancellationToken.None));
    }

    [Fact]
    public async Task Prune_drops_history_older_than_retention_window_and_rebuilds_fts()
    {
        var path = Path.Combine(_dir, "prune.sqlite");
        await using (var log = new SqliteQueryLog(path))
        {
            log.Append(Entry("select old_row") with { ExecutedAt = DateTimeOffset.UtcNow.AddDays(-400) });
            log.Append(Entry("select recent_row"));
            await SearchUntil(log, new QueryLogQuery(), expected: 2);
        }

        // Reopen with a 180-day window → the 400-day-old row is pruned on startup.
        await using var pruned = new SqliteQueryLog(path, retentionDays: 180);
        var all = await pruned.SearchAsync(new QueryLogQuery(), CancellationToken.None);
        Assert.Single(all);
        Assert.Equal("select recent_row", all[0].SqlText);

        // FTS index was rebuilt after the delete: the pruned row's text no longer matches.
        Assert.Empty(await pruned.SearchAsync(new QueryLogQuery { Text = "old_row" }, CancellationToken.None));
        Assert.Single(await pruned.SearchAsync(new QueryLogQuery { Text = "recent_row" }, CancellationToken.None));
    }

    [Fact]
    public async Task Retention_of_zero_keeps_everything()
    {
        var path = Path.Combine(_dir, "keep-all.sqlite");
        await using (var log = new SqliteQueryLog(path))
        {
            log.Append(Entry("select ancient") with { ExecutedAt = DateTimeOffset.UtcNow.AddYears(-5) });
            await SearchUntil(log, new QueryLogQuery(), expected: 1);
        }

        await using var reopened = new SqliteQueryLog(path, retentionDays: 0);
        Assert.Single(await reopened.SearchAsync(new QueryLogQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task Log_survives_reopen_so_history_is_persistent()
    {
        var path = Path.Combine(_dir, "persist.sqlite");
        await using (var log = new SqliteQueryLog(path))
        {
            log.Append(Entry("select 1 -- remembered"));
            await SearchUntil(log, new QueryLogQuery(), 1);
        }

        await using var reopened = new SqliteQueryLog(path);
        var found = await reopened.SearchAsync(new QueryLogQuery { Text = "remembered" }, CancellationToken.None);
        Assert.Single(found);
    }
}
