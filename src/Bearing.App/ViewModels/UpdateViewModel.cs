using System.Threading.Tasks;
using Avalonia.Threading;
using Bearing.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bearing.App.ViewModels;

/// <summary>
/// The update strip above the status bar: silent until there is something to say, then "downloading…" and
/// finally the restart offer. A thin mirror of <see cref="UpdateCoordinator"/> — every decision (whether to
/// check, what a failure means, how to apply) belongs to the coordinator, so this holds display text and the
/// commands that forward to one, and nothing else. The release-notes entries sit here too because the strip
/// and the Help menu are the surfaces that offer them, and the version to open at is the coordinator's.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateCoordinator _coordinator;
    private readonly ReleaseNotesCoordinator? _notes;

    public UpdateViewModel(UpdateCoordinator coordinator, ReleaseNotesCoordinator? notes = null)
    {
        _coordinator = coordinator;
        _notes = notes;
        CanShowNotes = notes is not null;
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
    /// Whether release notes can be reached at all — false only where no notes feed was supplied (headless
    /// runs, tests). Fixed for the lifetime of the view-model, so the strip's link and the Help entry are
    /// simply absent rather than present and inert.
    /// </summary>
    [ObservableProperty] private bool _canShowNotes;

    /// <summary>
    /// Help ▸ Check for Updates. Reports its outcome to the status bar either way — unlike the startup check,
    /// the user is waiting for an answer here.
    /// </summary>
    [RelayCommand]
    private Task CheckNowAsync() => _coordinator.CheckNowAsync();

    /// <summary>Help ▸ What's New. Opens the whole published history, newest first.</summary>
    [RelayCommand]
    private Task WhatsNewAsync() => _notes?.OpenAsync() ?? Task.CompletedTask;

    /// <summary>
    /// The strip's "what's new" link: the same window, scrolled to the version being offered — the one
    /// question a user actually has before deciding whether to restart now or later.
    /// </summary>
    [RelayCommand]
    private Task UpdateNotesAsync() => _notes?.OpenAsync(_coordinator.AvailableVersion) ?? Task.CompletedTask;

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
            case UpdatePhase.Applying:
                // The close can still be refused — a running query prompts — so say what will happen rather
                // than pretending the app is already gone.
                Message = $"Bearing {version} installs when Bearing closes.";
                IsVisible = true;
                CanRestart = false;
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
