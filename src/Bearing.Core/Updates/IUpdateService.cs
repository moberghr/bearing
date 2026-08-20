namespace Bearing.Core.Updates;

/// <summary>
/// A version on the release feed that is newer than the running one. <paramref name="Handle"/> is the
/// update backend's own descriptor for it, carried back opaquely so a download/apply refers to exactly the
/// version that was found — Core never looks inside it, which is what keeps the updater's package out of
/// this project (§2.1).
/// </summary>
public sealed record UpdateCheck(string Version, object? Handle = null);

/// <summary>
/// Checking for, downloading and applying an app update, behind an interface so the app layer never
/// references the updater directly and the whole flow is testable with a fake (§4.1/§4.3 — none of this
/// can be driven headlessly against the real installer).
/// <para>
/// Every method throws on failure: an unreachable feed, a checksum mismatch, a concurrent updater. That is
/// deliberate — deciding what a failure *means* is one caller's job (the app's update coordinator turns it
/// into a status-bar line, never a crash), not something to be spread across return codes here.
/// </para>
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Whether this build can update itself at all. False for a run from source or from a plain archive:
    /// there is no installed layout to replace, so callers must do nothing rather than report a failure the
    /// user can't act on.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Why <see cref="IsSupported"/> is false, when the reason is something other than "this simply isn't an
    /// installed build" — a malformed feed URL, say. Null when there is nothing to add.
    /// <para>
    /// Exists so an unsupported updater can never be silently indistinguishable from a misconfigured one,
    /// the same reason <c>ISecretStore.UnavailableReason</c> exists (§1.1): never assert a cause nobody
    /// checked, and never hide one that was.
    /// </para>
    /// </summary>
    string? UnavailableReason { get; }

    /// <summary>The newest version on the feed, or null when the running version is current.</summary>
    Task<UpdateCheck?> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetch <paramref name="update"/> into the local package store, reporting 0–100 as it goes. A delta is
    /// preferred and the full package is the fallback; that choice belongs to the implementation.
    /// </summary>
    Task DownloadAsync(UpdateCheck update, IProgress<int>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Stage <paramref name="update"/> to be installed once this process exits, then let the app shut down
    /// normally. This is the path a user-facing "Restart" takes: the app's own close pipeline (unsaved-work
    /// prompt, editor flush, session save, connection disposal) still runs, which it would not if the
    /// updater tore the process down from under the UI.
    /// </summary>
    void ApplyOnExit(UpdateCheck update);

    /// <summary>
    /// Install <paramref name="update"/> and relaunch immediately, ending this process from inside the
    /// call. Skips the app's shutdown pipeline, so only use it where there is nothing to lose.
    /// </summary>
    void ApplyAndRestart(UpdateCheck update);
}
