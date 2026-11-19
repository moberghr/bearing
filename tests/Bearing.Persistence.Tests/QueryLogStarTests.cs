using Bearing.Core.Logging;
using Bearing.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Bearing.Persistence.Tests;

/// <summary>
/// Bookmarks in the query log (#111): a starred entry is kept past retention, can be listed on its own, and
/// starring never rewrites the logged row itself.
/// </summary>
public class QueryLogStarTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bearing-star-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private static QueryLogEntry Entry(string sql, int daysAgo = 0) => new()
    {
        ExecutedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        ProviderId = "postgres",
        ConnectionName = "prod",
        Database = "pagila",
        SqlText = sql,
        Success = true,
    };

    private static async Task<IReadOnlyList<QueryLogEntry>> Until(IQueryLog log, int count)
    {
        for (var i = 0; i < 100; i++)
        {
            var found = await log.SearchAsync(new QueryLogQuery(), CancellationToken.None);
            if (found.Count >= count) return found;
            await Task.Delay(20);
        }
        throw new TimeoutException($"the log never reached {count} rows");
    }

    [Fact]
    public async Task A_starred_entry_reads_back_starred_and_is_listed_on_its_own()
    {
        await using var log = new SqliteQueryLog(Path.Combine(_dir, "star.sqlite"));
        log.Append(Entry("select kept"));
        log.Append(Entry("select other"));
        var rows = await Until(log, 2);
        var kept = rows.Single(r => r.SqlText == "select kept");
        Assert.All(rows, r => Assert.False(r.IsStarred));

        await log.SetStarredAsync(kept.Id, true, CancellationToken.None);

        var starred = await log.SearchAsync(new QueryLogQuery { StarredOnly = true }, CancellationToken.None);
        Assert.Equal("select kept", Assert.Single(starred).SqlText);
        Assert.True(starred[0].IsStarred);

        // Starring twice is a no-op, and unstarring takes it back.
        await log.SetStarredAsync(kept.Id, true, CancellationToken.None);
        Assert.Single(await log.SearchAsync(new QueryLogQuery { StarredOnly = true }, CancellationToken.None));
        await log.SetStarredAsync(kept.Id, false, CancellationToken.None);
        Assert.Empty(await log.SearchAsync(new QueryLogQuery { StarredOnly = true }, CancellationToken.None));
    }

    [Fact]
    public async Task The_starred_filter_finds_an_entry_older_than_the_newest_screenful()
    {
        await using var log = new SqliteQueryLog(Path.Combine(_dir, "deep.sqlite"));
        log.Append(Entry("select starred_long_ago", daysAgo: 30));
        for (var i = 0; i < 5; i++) log.Append(Entry($"select newer_{i}"));
        var rows = await Until(log, 6);
        await log.SetStarredAsync(rows.Single(r => r.SqlText == "select starred_long_ago").Id, true, CancellationToken.None);

        // A screenful of 3 doesn't reach it; the starred filter does.
        Assert.DoesNotContain(await log.SearchAsync(new QueryLogQuery { Limit = 3 }, CancellationToken.None),
            r => r.SqlText == "select starred_long_ago");
        var found = await log.SearchAsync(new QueryLogQuery { StarredOnly = true, Limit = 3 }, CancellationToken.None);
        Assert.Equal("select starred_long_ago", Assert.Single(found).SqlText);
    }

    [Fact]
    public async Task Retention_keeps_a_starred_entry_and_prunes_the_rest()
    {
        var path = Path.Combine(_dir, "prune.sqlite");
        await using (var log = new SqliteQueryLog(path))
        {
            log.Append(Entry("select old_starred", daysAgo: 400));
            log.Append(Entry("select old_plain", daysAgo: 400));
            log.Append(Entry("select recent"));
            var rows = await Until(log, 3);
            await log.SetStarredAsync(rows.Single(r => r.SqlText == "select old_starred").Id, true, CancellationToken.None);
        }

        await using var pruned = new SqliteQueryLog(path, retentionDays: 180);
        var left = await pruned.SearchAsync(new QueryLogQuery(), CancellationToken.None);
        Assert.Equal(["select recent", "select old_starred"], left.Select(r => r.SqlText).ToArray());
        Assert.True(left.Single(r => r.SqlText == "select old_starred").IsStarred);
        // The FTS rebuild after the prune still finds the kept row by its text.
        Assert.Single(await pruned.SearchAsync(new QueryLogQuery { Text = "old_starred" }, CancellationToken.None));
    }

    [Fact]
    public async Task Starring_an_id_that_is_not_in_the_log_stars_nothing()
    {
        await using var log = new SqliteQueryLog(Path.Combine(_dir, "missing.sqlite"));
        await log.SetStarredAsync(9999, true, CancellationToken.None);
        Assert.Empty(await log.SearchAsync(new QueryLogQuery { StarredOnly = true }, CancellationToken.None));
    }

    [Fact]
    public async Task A_v5_log_upgrades_in_place_and_keeps_its_rows()
    {
        // Build a v5 file by hand: the shape every existing install has today.
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "v5.sqlite");
        await using (var log = new SqliteQueryLog(path))
        {
            log.Append(Entry("select from_v5"));
            await Until(log, 1);
        }
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE query_log_star; PRAGMA user_version = 5;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        await using var upgraded = new SqliteQueryLog(path);
        var row = Assert.Single(await upgraded.SearchAsync(new QueryLogQuery(), CancellationToken.None));
        Assert.False(row.IsStarred);
        await upgraded.SetStarredAsync(row.Id, true, CancellationToken.None);
        Assert.Single(await upgraded.SearchAsync(new QueryLogQuery { StarredOnly = true }, CancellationToken.None));
    }
}
