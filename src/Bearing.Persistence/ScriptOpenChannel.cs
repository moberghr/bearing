using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Bearing.Persistence;

/// <summary>
/// How <c>bearing file.sql</c> reaches a Bearing window that is already open: a local pipe the window
/// listens on and the command (or a second <c>bearing-app</c>) writes the paths to.
/// <para>
/// <b>Why a second process must not simply open its own window.</b> Every open project writes its
/// <c>session.json</c> on the way out, and two processes holding the same project would each write their
/// own tab list over the other's — the last to quit wins, and the first one's tabs are gone. "Open in the
/// current project" is only true of the window the user is looking at, which is a running process, so the
/// request has to go there.
/// </para>
/// <para>
/// <b>Who can send.</b> <see cref="PipeOptions.CurrentUserOnly"/> on both ends: the OS refuses a client
/// running as anyone else, on Windows and on Unix alike. Within the user it is the same boundary as the CLI
/// (§1.11) — anything running as you — and what it can do is narrower: put a file in a tab. Nothing is run.
/// </para>
/// <para>
/// <b>One name per data directory</b>, not one per machine: a dev build (<c>BEARING_PROFILE</c>) and a test
/// redirecting <c>XDG_DATA_HOME</c> must not hand their files to the installed app's window, and two users on
/// one machine must not collide on a Unix socket path in <c>/tmp</c>.
/// </para>
/// </summary>
public static class ScriptOpenChannel
{
    /// <summary>First line of a request. Versioned so a newer command talking to an older window is
    /// told no, rather than having its paths misread.</summary>
    internal const string Hello = "bearing-open 1";

    internal const string Accepted = "ok";

    /// <summary>The pipe name for this user and profile.</summary>
    public static string Name => NameFor(BearingPaths.DataDir);

    /// <summary>Pure: the name for a given data directory. Short, because a Unix socket path is capped
    /// at ~104 bytes and .NET puts this under <c>/tmp/CoreFxPipe_</c>.</summary>
    internal static string NameFor(string dataDirectory)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory)));
        return "bearing-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Hand <paramref name="paths"/> to a running window. True only when one answered that it took them;
    /// false — no window, an older window, or no answer in time — means the caller should start one.
    /// <para>
    /// Never throws: every failure here is "nobody is listening", which is the ordinary case of the app not
    /// being open, and the answer to it is to open it.
    /// </para>
    /// </summary>
    public static async Task<bool> TrySendAsync(
        IReadOnlyList<string> paths, string? pipeName = null, TimeSpan? timeout = null)
    {
        // A path is one line of the request, so one that contains a line break cannot be sent intact. Nobody
        // names a script that way; refusing beats opening the wrong file.
        if (paths.Count == 0 || paths.Any(p => p.Contains('\n') || p.Contains('\r'))) return false;

        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", pipeName ?? Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            // Short on its own: a window that is there accepts at once, and one that is not should cost the
            // command a blink, not the whole deadline.
            await pipe.ConnectAsync(TimeSpan.FromMilliseconds(500), deadline.Token).ConfigureAwait(false);

            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
            await writer.WriteLineAsync(Hello).ConfigureAwait(false);
            foreach (var path in paths) await writer.WriteLineAsync(path).ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.FlushAsync(deadline.Token).ConfigureAwait(false);

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var answer = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            return answer == Accepted;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Start listening. Each request's paths go to <paramref name="open"/>, on the listener's own thread —
    /// the caller marshals to wherever it needs to be. Returns null when the pipe cannot be created at all,
    /// which leaves the window working and merely unreachable from the command (§5.2).
    /// </summary>
    public static Listener? Listen(Action<IReadOnlyList<string>> open, string? pipeName = null)
    {
        try
        {
            return new Listener(pipeName ?? Name, open);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The window's end: accepts one request at a time until disposed.</summary>
    public sealed class Listener : IAsyncDisposable
    {
        private readonly string _name;
        private readonly Action<IReadOnlyList<string>> _open;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        internal Listener(string name, Action<IReadOnlyList<string>> open)
        {
            _name = name;
            _open = open;
            // The first server instance is created here, synchronously, so a name that cannot be taken fails
            // Listen rather than a background loop nobody is watching.
            var first = Create();
            _loop = Task.Run(() => LoopAsync(first));
        }

        private NamedPipeServerStream Create() => new(
            _name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        private async Task LoopAsync(NamedPipeServerStream? pipe)
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    pipe ??= Create();
                    await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    await ServeAsync(pipe).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // One bad client — a dropped connection, a malformed request — must not end the
                    // listening. Back off a little so a pipe that fails on creation cannot spin.
                    try { await Task.Delay(200, _stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                finally
                {
                    if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
                    pipe = null;
                }
            }
        }

        private async Task ServeAsync(NamedPipeServerStream pipe)
        {
            // A client that connects and then says nothing must not hold the only listener forever.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            if (await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) != Hello) return;

            var paths = new List<string>();
            while (await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) is { Length: > 0 } line)
                paths.Add(line);
            if (paths.Count == 0) return;

            _open(paths);

            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
            await writer.WriteLineAsync(Accepted).ConfigureAwait(false);
            await writer.FlushAsync(deadline.Token).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            try { await _loop.ConfigureAwait(false); }
            catch { /* stopping; nothing left to report to */ }
            _stop.Dispose();
        }
    }
}
