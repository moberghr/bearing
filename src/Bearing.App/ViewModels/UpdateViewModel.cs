using System.Threading.Tasks;
using Avalonia.Threading;
using Bearing.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bearing.App.ViewModels;

/// <summary>
/// The update strip above the status bar: silent until there is something to say, then "downloading…" and
/// finally the restart offer. A thin mirror of <see cref="UpdateCoordinator"/> — every decision (whether to
/// check, what a failure means, how to apply) belongs to the coordinator, so this holds display text and two
/// commands and nothing else.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateCoordinator _coordinator;

    public UpdateViewModel(UpdateCoordinator coordinator)
    {
        _coordinator = coordinator;
        // The coordinator runs its work off the UI thread, so its notifications arrive there too.
        _coordinator.Changed += () =>
        {
            if (Dispatcher.UIThread.CheckAccess()) Sync();
            else Dispatcher.UIThread.Post(Sync);
        };
        Sync();
    }

    /// <summary>What the strip says. Empty while there is nothing to report.</summary>
    [ObservableProperty] private string _message = "";

    /// <summary>Whether the strip is on screen at all — a download in progress or an update waiting to install.</summary>
    [ObservableProperty] private bool _isVisible;

    /// <summary>Whether the update is staged, so the Restart / Later buttons apply.</summary>
    [ObservableProperty] private bool _canRestart;

    /// <summary>
    /// Help ▸ Check for Updates. Reports its outcome to the status bar either way — unlike the startup check,
    /// the user is waiting for an answer here.
    /// </summary>
    [RelayCommand]
    private Task CheckNowAsync() => _coordinator.CheckNowAsync();

    /// <summary>Apply the update by closing the app normally; the updater relaunches it.</summary>
    [RelayCommand]
    private void Restart() => _coordinator.RestartToApply();

    /// <summary>Hide the offer for this session. The update stays staged for the next launch.</summary>
    [RelayCommand]
    private void Dismiss() => _coordinator.Dismiss();

    private void Sync()
    {
        var version = _coordinator.AvailableVersion;
        switch (_coordinator.Phase)
        {
            case UpdatePhase.Downloading:
                Message = $"Downloading Bearing {version} — {_coordinator.Progress}%";
                IsVisible = true;
                CanRestart = false;
                break;
            case UpdatePhase.Ready:
                Message = $"Bearing {version} is ready to install.";
                IsVisible = true;
                CanRestart = true;
                break;
            default:
                // Checking, up to date, dismissed, or failed. A failure has already gone to the status bar;
                // repeating it in a strip the user has to close would make a missed update feel like a fault.
                Message = "";
                IsVisible = false;
                CanRestart = false;
                break;
        }
    }
}
