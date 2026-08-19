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

    /// <summary>The feed could not be reached, or the download failed. Reported once, never retried on its own.</summary>
    Failed,
}

/// <summary>
/// Drives <see cref="IUpdateService"/> for the running app: one check per launch, download in the
/// background, and then <b>stop</b> — installing waits for the user. The single place that decides an update
/// failure is a status-bar line rather than a crash, which is why every call out of here is wrapped.
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
    private UpdateCheck? _staged;
    private bool _autoRun;

    /// <param name="service">The update mechanism; <see cref="IUpdateService.IsSupported"/> gates everything.</param>
    /// <param name="autoUpdateEnabled">Reads the live setting — re-read per check, never cached, so toggling
    /// it takes effect without a restart.</param>
    /// <param name="requestShutdown">Closes the app the ordinary way (unsaved-work prompt, editor flush,
    /// session save, connection disposal), which is how an update gets applied without losing work.</param>
    /// <param name="report">Status-bar sink, as <c>SettingsService.SaveFailed</c> uses. Null drops the message.</param>
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

    /// <summary>Status-bar sink for the messages a user asked to see (and for a failure, once).</summary>
    public Action<string>? Report { get; set; }

    public UpdatePhase Phase { get; private set; } = UpdatePhase.Idle;

    /// <summary>The version found, once there is one. Null before that, and after a dismissed offer.</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>Download progress, 0-100.</summary>
    public int Progress { get; private set; }

    /// <summary>Why the last attempt failed. Null unless <see cref="Phase"/> is <see cref="UpdatePhase.Failed"/>.</summary>
    public string? FailureMessage { get; private set; }

    /// <summary>True once an update is downloaded and only a restart is missing.</summary>
    public bool IsStaged => _staged is not null;

    /// <summary>
    /// The startup path: honours the setting, runs at most once per launch, and says nothing unless it finds
    /// something (or fails). Safe to call from a background thread — nothing here touches the UI.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_autoRun) return;
        _autoRun = true;
        if (!_service.IsSupported || !_autoUpdateEnabled()) return;
        await RunAsync(announce: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The Help ▸ Check for Updates path: ignores the setting (the user just asked) and reports the outcome
    /// either way, because a menu item that appears to do nothing is worse than a slow one.
    /// </summary>
    public async Task CheckNowAsync(CancellationToken ct = default)
    {
        if (Phase is UpdatePhase.Checking or UpdatePhase.Downloading) return;

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
            Announce("This build cannot update itself — it was not installed by the Bearing installer.");
            return;
        }

        await RunAsync(announce: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Apply the staged update: stage it for install-on-exit, then close the app normally so the shutdown
    /// pipeline still runs. The updater relaunches once this process is gone.
    /// </summary>
    public void RestartToApply()
    {
        if (_staged is null) return;
        try
        {
            _service.ApplyOnExit(_staged);
        }
        catch (Exception ex)
        {
            Fail($"Could not start the update: {ex.Message}");
            return;
        }

        _requestShutdown();
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
            Fail($"Could not check for updates: {ex.Message}");
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
            Fail($"Could not download Bearing {found.Version}: {ex.Message}");
            return;
        }

        _staged = found;
        Progress = 100;
        Set(UpdatePhase.Ready);
    }

    private void Set(UpdatePhase phase)
    {
        Phase = phase;
        if (phase is not UpdatePhase.Failed) FailureMessage = null;
        if (phase is UpdatePhase.Idle && !IsStaged) AvailableVersion = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// A failed update is a status-bar line and a dead end for this launch — no retry loop, no dialog. Missing
    /// one update is an inconvenience; nagging about it, or crashing over it, is worse.
    /// </summary>
    private void Fail(string message)
    {
        FailureMessage = message;
        Phase = UpdatePhase.Failed;
        Changed?.Invoke();
        Report?.Invoke(message);
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
