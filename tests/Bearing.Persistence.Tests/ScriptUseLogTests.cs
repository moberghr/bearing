using Bearing.Core.Logging;
using Bearing.Persistence;
using Xunit;

namespace Bearing.Persistence.Tests;

/// <summary>
/// The log's answer to "where and when was each script last run" (<see cref="IQueryLog.LastScriptUsesAsync"/>):
/// what the Scripts panel's trailer and a server's recent scripts are read from.
/// </summary>
public class ScriptUseLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bearing-script-use-tests", Guid.NewGuid().ToString("N"));
    private static readonly Guid Prod = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Test = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private static QueryLogEntry Run(string? script, Guid? connection, string database, int minute = 0) => new()
    {
        ExecutedAt = new DateTimeOffset(2026, 11, 19, 10, minute, 0, TimeSpan.Zero),
        ProviderId = "postgres",
        ConnectionName = connection == Prod ? "prod" : "test",
        ConnectionId = connection,
        Database = database,
        SqlText = "select 1",
        Duration = TimeSpan.FromMilliseconds(1),
        Success = true,
        ScriptPath = script,
    };

    /// <summary>Append and wait until every row is readable (Append returns before the insert).</summary>
    private static async Task Append(SqliteQueryLog log, params QueryLogEntry[] entries)
    {
        var left = entries.Length;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnAppended(QueryLogEntry _) { if (Interlocked.Decrement(ref left) == 0) done.TrySetResult(); }
        log.Appended += OnAppended;
        foreach (var e in entries) log.Append(e);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        log.Appended -= OnAppended;
    }

    [Fact]
    public async Task Each_script_appears_once_with_its_latest_run_newest_first()
    {
        await using var log = new SqliteQueryLog(_dir);
        await Append(log,
            Run("/s/a.sql", Prod, "pagila", 1),
            Run("/s/b.sql", Prod, "pagila", 2),
            Run("/s/a.sql", Test, "netgiro", 3));

        var uses = await log.LastScriptUsesAsync(null, null, CancellationToken.None);

        Assert.Equal(["/s/a.sql", "/s/b.sql"], uses.Select(u => u.ScriptPath));
        var a = uses[0];
        Assert.Equal("netgiro", a.Database);
        Assert.Equal("test", a.ConnectionName);
        Assert.Equal(Test, a.ConnectionId);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 10, 3, 0, TimeSpan.Zero), a.ExecutedAt);
    }

    [Fact]
    public async Task Runs_with_no_script_are_not_a_script()
    {
        await using var log = new SqliteQueryLog(_dir);
        await Append(log, Run(null, Prod, "pagila"), Run("", Prod, "pagila", 1));

        Assert.Empty(await log.LastScriptUsesAsync(null, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_connection_filter_gives_each_scripts_latest_run_on_that_connection()
    {
        await using var log = new SqliteQueryLog(_dir);
        await Append(log,
            Run("/s/a.sql", Prod, "pagila", 1),
            Run("/s/a.sql", Test, "netgiro", 2),   // newer, but elsewhere
            Run("/s/b.sql", Test, "netgiro", 3));

        var uses = await log.LastScriptUsesAsync(Prod, null, CancellationToken.None);

        var only = Assert.Single(uses);
        Assert.Equal("/s/a.sql", only.ScriptPath);
        Assert.Equal("pagila", only.Database);
    }

    [Fact]
    public async Task The_limit_keeps_the_most_recent_scripts()
    {
        await using var log = new SqliteQueryLog(_dir);
        await Append(log, Enumerable.Range(0, 12).Select(i => Run($"/s/{i}.sql", Prod, "pagila", i)).ToArray());

        var uses = await log.LastScriptUsesAsync(Prod, 10, CancellationToken.None);

        Assert.Equal(10, uses.Count);
        Assert.Equal("/s/11.sql", uses[0].ScriptPath);
        Assert.Equal("/s/2.sql", uses[^1].ScriptPath);
    }
}
