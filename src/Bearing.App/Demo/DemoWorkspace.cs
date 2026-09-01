using System;
using System.IO;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Bearing.Core.Workspace;
using Bearing.Demo;
using Bearing.Persistence;

namespace Bearing.App.Demo;

/// <summary>
/// The throwaway workspace a demo session runs in (#64) — a temp directory holding the project, its session
/// state, its query log and its recent-projects list, deleted when the session ends.
/// <para>
/// This is the bulk of demo mode, and the reason it could not simply reuse <c>SeedDemoConnectionAsync</c>:
/// that path persists the connection into the user's real project manifest and pushes a password through
/// <c>ISecretStore</c>, so a demo would leave demo junk in a real project and a real keychain, and leave it
/// there afterwards.
/// </para>
/// <para>
/// Nothing here touches <see cref="BearingPaths.DataDir"/> or <see cref="BearingPaths.ConfigDir"/>: no
/// manifest write, no recent-projects entry, no query-log rows beside the user's own history (§1.3), and no
/// secret-store call at all (§1.1). The stores were already path-parameterised, so this is composition rather
/// than new persistence code.
/// </para>
/// </summary>
public sealed class DemoWorkspace : IAsyncDisposable
{
    private readonly string _root;

    private DemoWorkspace(string root) => _root = root;

    /// <summary>Where the demo project lives — under the OS temp directory, never the app's data directory.</summary>
    public string ProjectDirectory => Path.Combine(_root, "demo-project");

    /// <summary>A demo session's stores, all pointed inside <see cref="_root"/>.</summary>
    public IProjectStore Projects { get; } = new JsonProjectStore();

    public ISessionStore Sessions { get; } = new JsonSessionStore();

    public IQueryLog QueryLog { get; private set; } = null!;

    public IRecentProjects RecentProjects { get; private set; } = null!;

    /// <summary>
    /// The secret store a demo session gets: one that keeps nothing and is asked for nothing.
    /// <para>
    /// Not the real one, and not a probe for it. The demo connection needs no secret — the stored-password
    /// path sends none on its first attempt, which is exactly the passwordless case — so reaching for the OS
    /// keychain would be a call with no purpose and a prompt the user cannot answer usefully.
    /// </para>
    /// </summary>
    public ISecretStore Secrets { get; } = new NoSecretStore();

    /// <summary>
    /// Create the directory and the stores inside it. A unique name per launch, so two demo windows do not
    /// share a query log and neither inherits what the last one left behind — a demo that remembers the
    /// previous demo's tabs is not the clean first run this exists to give.
    /// </summary>
    public static DemoWorkspace Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "bearing-demo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = new DemoWorkspace(root)
        {
            // retentionDays: 0 keeps everything — there is nothing to prune in a session this short, and the
            // file goes with the directory.
            QueryLog = new SqliteQueryLog(Path.Combine(root, "query-log.sqlite"), retentionDays: 0),
            RecentProjects = new FileRecentProjects(Path.Combine(root, "recent.json")),
        };
        return workspace;
    }

    /// <summary>
    /// Close the stores, then delete the whole directory.
    /// <para>
    /// The query log has to be closed <b>first</b>, and awaited: it writes on a background channel and holds
    /// an open SQLite connection, so deleting underneath it leaves the directory in place — the demo would
    /// then leave exactly the residue it exists not to leave. Its own <c>DisposeAsync</c> drains the channel
    /// before closing, so nothing in flight is lost either.
    /// </para>
    /// <para>
    /// Then a short retry, because releasing a file handle on Windows is not always synchronous with closing
    /// it: SQLite pools connections, and the first delete can still see a live <c>-wal</c>. Best-effort at
    /// the end regardless (§5.2) — a demo that cannot tidy up must not become a crash on exit, and the OS
    /// clears its own temp directory.
    /// </para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (QueryLog is IAsyncDisposable log)
        {
            // ConfigureAwait(false) on every await here, and it is load-bearing rather than hygiene: the
            // caller is the window's Closed handler, which blocks the UI thread waiting for this. Without it
            // the continuation is posted back to that same thread and can never run — the wait times out and
            // the directory survives, which is exactly the residue this class exists to remove.
            try { await log.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* closing is best-effort too */ }
        }

        for (var attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(_root)) return;
                Directory.Delete(_root, recursive: true);
                return;
            }
            // Both, because a still-held handle on Windows surfaces as either: IOException for a file in use,
            // UnauthorizedAccessException for one a pooled SQLite connection has open. Catching only the
            // first abandoned the delete on the first attempt, which defeated the retry entirely.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       && attempt < DeleteAttempts - 1)
            {
                await Task.Delay(DeleteRetryDelay).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // the OS will collect it
            }
        }
    }

    /// <summary>How many times to try the delete, and how long to wait between — enough for a pooled SQLite
    /// handle to be released, not enough to be felt on the way out.</summary>
    private const int DeleteAttempts = 4;

    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(75);
}
