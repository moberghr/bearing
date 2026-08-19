using Bearing.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace Bearing.Updates;

/// <summary>
/// <see cref="IUpdateService"/> over Velopack against <see cref="UpdateFeed"/>. Nothing outside this project
/// knows the updater exists.
/// </summary>
public sealed class VelopackUpdateService : IUpdateService
{
    private readonly string _repoUrl;
    private readonly string? _accessToken;
    private UpdateManager? _manager;
    private bool _unavailable;

    public VelopackUpdateService(string? repoUrl = null, string? accessToken = null)
    {
        _repoUrl = repoUrl ?? UpdateFeed.RepoUrl;
        _accessToken = accessToken ?? UpdateFeed.AccessToken;
    }

    /// <summary>
    /// False when this build has no installed layout to replace — a run from source, from the plain archive
    /// build/release.sh produces, or a test host. Asked before anything else so a dev run never touches the
    /// network and never reports a failure the user can do nothing about.
    /// </summary>
    public bool IsSupported => Manager?.IsInstalled == true;

    public async Task<UpdateCheck?> CheckAsync(CancellationToken ct = default)
    {
        var info = await Required().CheckForUpdatesAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return info is null ? null : new UpdateCheck(info.TargetFullRelease.Version.ToString(), info);
    }

    public Task DownloadAsync(UpdateCheck update, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var info = Info(update);
        return Required().DownloadUpdatesAsync(info, p => progress?.Report(p), cancelToken: ct);
    }

    public void ApplyOnExit(UpdateCheck update)
    {
        var info = Info(update);
        Required().WaitExitThenApplyUpdates(info, silent: false, restart: true);
    }

    public void ApplyAndRestart(UpdateCheck update)
    {
        var info = Info(update);
        Required().ApplyUpdatesAndRestart(info);
    }

    /// <summary>
    /// Built on first use, never in the constructor: Velopack resolves its install layout from a locator that
    /// only exists once <c>VelopackApp.Build().Run()</c> has run, so constructing one eagerly would throw
    /// anywhere that hook is absent — a test host, a designer, any future entry point — and the composition
    /// root builds this during startup. A missing locator is not an error here, it is the answer to
    /// <see cref="IsSupported"/>: this build cannot update itself.
    /// </summary>
    private UpdateManager? Manager
    {
        get
        {
            if (_manager is not null || _unavailable) return _manager;
            try
            {
                _manager = new UpdateManager(new GithubSource(_repoUrl, _accessToken, prerelease: false));
            }
            catch (Exception)
            {
                _unavailable = true;
            }

            return _manager;
        }
    }

    private UpdateManager Required()
        => Manager ?? throw new InvalidOperationException(
            "This build cannot update itself: it was not installed by the Bearing installer.");

    /// <summary>
    /// Unwrap the descriptor <see cref="CheckAsync"/> handed out. A handle from somewhere else is a
    /// programming error, not a user-facing failure — say so rather than silently updating to whatever
    /// "latest" happens to be by then. Checked before the manager is touched, so a bad call is reported as a
    /// bad call whatever the install state.
    /// </summary>
    private static UpdateInfo Info(UpdateCheck update)
        => update.Handle as UpdateInfo
           ?? throw new ArgumentException(
               $"Update {update.Version} did not come from this service — its handle is not a Velopack UpdateInfo.",
               nameof(update));
}
