using System;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Updates;

namespace Bearing.App.Services;

/// <summary>Where the update flow has got to. Display strings live in the view-model, not here.</summary>
public enum UpdatePhase
{
    /// <summary>Nothing found, nothing to say — including "this build cannot update itself".</summary>
    Idle,
    Checking,
    Downloading,

    /// <summary>Downloaded and staged. The only phase the user is prompted about.</summary>
    Ready,

    /// <summary>
    /// The user accepted, so the update installs when the app closes. A distinct phase because the close can
    /// still be refused (a running query prompts), and the strip has to keep telling the truth if it is.
    /// </summary>
    Applying,

    /// <summary>The feed could not be reached, or the download failed. Never retried on its own.</summary>
    Failed,
}

/// <summary>
/// Drives <see cref="IUpdateService"/> for the running app: one check per launch, download in the
/// background, and then <b>stop</b> — installing waits for the user. The single place that decides an update
/// failure is a reportable message rather than a crash, which is why every call out of here is wrapped.
/// <para>
/// Deliberately free of Avalonia so the whole state machine is unit-testable (§2.5/§4.3) — the view-model
/// mirrors it onto the UI thread.
/// </para>
/// </summary>
public sealed class UpdateCoordinator
{
    private readonly IUpdateService _service;
    private readonly Func<bool> _autoUpdateEnabled;
    private readonly Action _requestShutdown;

    /// <summary>
    /// One check or download at a time. The phase alone can't police this: the startup check runs on a
    /// background thread while Help ▸ Check for Updates comes off the UI thread, so both could pass a phase
    /// test before either had set it and end up downloading the same package twice.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private UpdateCheck? _staged;
    private bool _applyOnExit;

    // 0/1 rather than a bool because the check and the set have to be one operation: StartAsync is the
    // once-per-launch guard, and a plain `if (!_autoRun) _autoRun = true` lets two callers both see false.
    private int _autoRun;

    /// <param name="service">The update mechanism; <see cref="IUpdateService.IsSupported"/> gates everything.</param>
    /// <param name="autoUpdateEnabled">Reads the live setting — re-read per check, never cached, so toggling
    /// it takes effect without a restart.</param>
    /// <param name="requestShutdown">Closes the app the ordinary way (unsaved-work prompt, editor flush,
    /// session save, connection disposal), which is how an update gets applied without losing work.</param>
    /// <param name="report">Status-bar sink, as <c>SettingsService.SaveFailed</c> uses. Only ever given
    /// messages the user asked for — see <see cref="Announce"/>. Null drops them.</param>
    public UpdateCoordinator(
        IUpdateService service,
        Func<bool> autoUpdateEnabled,
        Action requestShutdown,
        Action<string>? report = null)
    {
        _service = service;
        _autoUpdateEnabled = autoUpdateEnabled;
        _requestShutdown = requestShutdown;
        Report = report;
    }

    /// <summary>Raised after any state change, on whatever thread the work happened on.</summary>
    public event Action? Changed;

    /// <summary>
    /// Sink for messages the user is waiting on. Deliberately <b>not</b> used by the startup check: it writes
    /// to the shared status line, which nothing restores, so a background failure would park itself over the
    /// connection status for the rest of the session.
    /// </summary>
    public Action<string>? Report { get; set; }

    public UpdatePhase Phase { get; private set; } = UpdatePhase.Idle;

    /// <summary>The version found, once there is one. Null before that.</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>Download progress, 0-100.</summary>
    public int Progress { get; private set; }

    /// <summary>
    /// Why the last attempt failed, whether or not it was reported. Survives so an explicit check can say
    /// what a silent background failure ran into.
    /// </summary>
    public string? FailureMessage { get; private set; }

    /// <summary>True once an update is downloaded and only a restart is missing.</summary>
    public bool IsStaged => _staged is not null;

    /// <summary>
    /// The startup path: honours the setting, runs at most once per launch, and stays silent throughout —
    /// including on failure. Safe to call from a background thread; nothing here touches the UI.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _autoRun, 1) != 0) return;
        if (!_service.IsSupported || !_autoUpdateEnabled()) return;
        await RunAsync(announce: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The Help ▸ Check for Updates path: ignores the setting (the user just asked) and reports the outcome
    /// either way, because a menu item that appears to do nothing is worse than a slow one.
    /// </summary>
    public async Task CheckNowAsync(CancellationToken ct = default)
    {
        if (Phase == UpdatePhase.Applying)
        {
            Announce($"Bearing {AvailableVersion} installs when Bearing closes.");
            return;
        }

        if (IsStaged)
        {
            // Already downloaded. Re-checking would fetch the same package again, so restate the offer —
            // and put it back on screen if it was dismissed.
            Set(UpdatePhase.Ready);
            Announce($"Bearing {AvailableVersion} is ready to install — restart to apply it.");
            return;
        }

        if (!_service.IsSupported)
        {
            // A build that can't update itself is ordinary; a *broken* updater is not, and must not look the
            // same (§1.1's rule about never asserting a cause nobody checked, applied to updates).
            Announce(_service.UnavailableReason is { Length: > 0 } reason
                ? $"Couldn't set up the updater: {reason}"
                : "This build cannot update itself — it was not installed by the Bearing installer.");
            return;
        }

        await RunAsync(announce: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Accept the staged update. Only requests the close — the install is staged on the way out, in
    /// <see cref="ApplyIfPending"/>, precisely because this close can be refused: the quit guard cancels it
    /// while a query is running. Staging first would leave the updater waiting on a process that then carries
    /// on running.
    /// </summary>
    public void RestartToApply()
    {
        if (_staged is null) return;
        _applyOnExit = true;
        Set(UpdatePhase.Applying);
        _requestShutdown();
    }

    /// <summary>
    /// Hand the update to the updater, now that the app really is closing. Call from the window's
    /// <c>Closed</c> event — which only fires for a close that was not cancelled — while the process is still
    /// alive, since the updater waits on its exit.
    /// </summary>
    /// <returns>Null on success or when there was nothing to apply; otherwise why it failed, for the caller
    /// to log. There is no UI left to show it in by this point.</returns>
    public string? ApplyIfPending()
    {
        if (!_applyOnExit || _staged is null) return null;
        _applyOnExit = false;
        try
        {
            _service.ApplyOnExit(_staged);
            return null;
        }
        catch (Exception ex)
        {
            FailureMessage = $"Could not start the update: {ex.Message}";
            Phase = UpdatePhase.Failed;
            return FailureMessage;
        }
    }

    /// <summary>Put the offer away for this session without applying it. It stays staged for the next launch.</summary>
    public void Dismiss()
    {
        if (Phase != UpdatePhase.Ready) return;
        Phase = UpdatePhase.Idle;
        Changed?.Invoke();
    }

    private async Task RunAsync(bool announce, CancellationToken ct)
    {
        // Already staged: re-checking would find the same release and download the same package again.
        // CheckNowAsync restates the offer before it reaches here; the startup check just stops. Without
        // this, "two checks at once do the work once" held only while the two actually overlapped — a
        // Check for Updates that finished first was followed by a second full check and download.
        if (IsStaged) return;
        // Drop rather than queue: whoever holds the gate is already doing this exact work.
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            Set(UpdatePhase.Checking);
            if (announce) Announce("Checking for updates…");

            UpdateCheck? found;
            try
            {
                found = await _service.CheckAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Set(UpdatePhase.Idle);
                return;
            }
            catch (Exception ex)
            {
                Fail($"Could not check for updates: {ex.Message}", announce);
                return;
            }

            if (found is null)
            {
                Set(UpdatePhase.Idle);
                if (announce) Announce("Bearing is up to date.");
                return;
            }

            AvailableVersion = found.Version;
            Progress = 0;
            Set(UpdatePhase.Downloading);

            try
            {
                await _service.DownloadAsync(found, new InlineProgress(p =>
                {
                    Progress = p;
                    Changed?.Invoke();
                }), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Set(UpdatePhase.Idle);
                return;
            }
            catch (Exception ex)
            {
                Fail($"Could not download Bearing {found.Version}: {ex.Message}", announce);
                return;
            }

            _staged = found;
            Progress = 100;
            Set(UpdatePhase.Ready);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Set(UpdatePhase phase)
    {
        Phase = phase;
        if (phase is not UpdatePhase.Failed) FailureMessage = null;
        if (phase is UpdatePhase.Idle && !IsStaged) AvailableVersion = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// A failed update is a dead end for this launch — no retry loop, no dialog. It is only <i>reported</i>
    /// when the user asked for the check; a background failure is kept in <see cref="FailureMessage"/> for the
    /// next explicit check to explain. Missing one update is an inconvenience; hijacking the status line over
    /// it, or crashing, is worse.
    /// </summary>
    private void Fail(string message, bool announce)
    {
        FailureMessage = message;
        Phase = UpdatePhase.Failed;
        Changed?.Invoke();
        if (announce) Report?.Invoke(message);
    }

    private void Announce(string message) => Report?.Invoke(message);

    /// <summary>
    /// Reports on the calling thread, deliberately — <see cref="Progress{T}"/> would capture whatever
    /// synchronization context happened to be current when it was constructed (none, on the background
    /// thread this runs on) and deliver the values later, out of order with the phase changes around them.
    /// The view-model is the one place that marshals to the UI thread.
    /// </summary>
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
