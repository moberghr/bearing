using Bearing.Persistence;
using Xunit;

namespace Bearing.Persistence.Tests;

/// <summary>
/// The pipe `bearing file.sql` reaches an open window through. Each test listens on its own name, so they
/// neither meet each other nor a Bearing running on the machine.
/// </summary>
public class ScriptOpenChannelTests
{
    private static string UniqueName() => "bearing-test-" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task A_listening_window_receives_the_paths_in_order_and_says_it_took_them()
    {
        var name = UniqueName();
        var received = new TaskCompletionSource<IReadOnlyList<string>>();
        await using var listener = ScriptOpenChannel.Listen(paths => received.TrySetResult(paths), name);
        Assert.NotNull(listener);

        var sent = await ScriptOpenChannel.TrySendAsync(["/tmp/a b.sql", "/tmp/č.sql"], name);

        Assert.True(sent);
        Assert.Equal(["/tmp/a b.sql", "/tmp/č.sql"], await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task It_keeps_listening_after_a_request()
    {
        var name = UniqueName();
        var count = 0;
        await using var listener = ScriptOpenChannel.Listen(_ => Interlocked.Increment(ref count), name);

        Assert.True(await ScriptOpenChannel.TrySendAsync(["/tmp/1.sql"], name));
        Assert.True(await ScriptOpenChannel.TrySendAsync(["/tmp/2.sql"], name));
        Assert.Equal(2, count);
    }

    /// <summary>The ordinary case of the app not being open — the caller starts one.</summary>
    [Fact]
    public async Task Nobody_listening_is_a_quick_no_not_an_exception()
    {
        var started = DateTime.UtcNow;

        Assert.False(await ScriptOpenChannel.TrySendAsync(["/tmp/a.sql"], UniqueName()));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    /// <summary>A window that quit leaves the name free for the next one (on Unix, its socket file).</summary>
    [Fact]
    public async Task A_stopped_listener_gives_the_name_back()
    {
        var name = UniqueName();
        await (ScriptOpenChannel.Listen(_ => { }, name)!).DisposeAsync();
        Assert.False(await ScriptOpenChannel.TrySendAsync(["/tmp/a.sql"], name));

        var received = new TaskCompletionSource<IReadOnlyList<string>>();
        await using var again = ScriptOpenChannel.Listen(paths => received.TrySetResult(paths), name);
        Assert.NotNull(again);
        Assert.True(await ScriptOpenChannel.TrySendAsync(["/tmp/a.sql"], name));
    }

    [Fact]
    public async Task A_path_with_a_line_break_is_not_sent_at_all()
    {
        var name = UniqueName();
        var called = false;
        await using var listener = ScriptOpenChannel.Listen(_ => called = true, name);

        Assert.False(await ScriptOpenChannel.TrySendAsync(["/tmp/a\n/etc/b.sql"], name));
        Assert.False(called);
    }

    /// <summary>A dev profile, a redirected data directory and another user each get their own window.</summary>
    [Fact]
    public void The_name_follows_the_data_directory()
    {
        Assert.Equal(ScriptOpenChannel.NameFor("/home/u/.local/share/bearing"),
                     ScriptOpenChannel.NameFor("/home/u/.local/share/bearing"));
        Assert.NotEqual(ScriptOpenChannel.NameFor("/home/u/.local/share/bearing"),
                        ScriptOpenChannel.NameFor("/home/u/.local/share/bearing-dev"));
        Assert.NotEqual(ScriptOpenChannel.NameFor("/home/u/.local/share/bearing"),
                        ScriptOpenChannel.NameFor("/home/v/.local/share/bearing"));
        Assert.True(ScriptOpenChannel.NameFor("/x").Length <= 30);
    }
}
