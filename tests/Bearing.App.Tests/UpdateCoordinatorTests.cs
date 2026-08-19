using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Bearing.App.Services;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The update policy, which is the part of #20 that can be tested at all — the installer and the restart
/// itself are eyeball QA (§4.3). What matters here: a dev build never phones home, the setting is obeyed, a
/// failure is a one-line report rather than a retry loop, and applying an update goes through the app's own
/// shutdown rather than around it.
/// </summary>
public class UpdateCoordinatorTests
{
    private static (UpdateCoordinator Coordinator, FakeUpdateService Service, List<string> Reports, List<int> Shutdowns)
        Build(bool autoUpdate = true, string? available = "0.3.0")
    {
        var service = new FakeUpdateService { Available = available };
        var reports = new List<string>();
        var shutdowns = new List<int>();
        var coordinator = new UpdateCoordinator(
            service,
            autoUpdateEnabled: () => autoUpdate,
            requestShutdown: () => shutdowns.Add(1),
            report: reports.Add);
        return (coordinator, service, reports, shutdowns);
    }

    [Fact]
    public async Task A_build_that_cannot_update_itself_never_touches_the_feed()
    {
        var (coordinator, service, reports, _) = Build();
        service.IsSupported = false;

        await coordinator.StartAsync();

        Assert.Equal(0, service.Checks);
        Assert.Equal(UpdatePhase.Idle, coordinator.Phase);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task Auto_update_off_means_no_check_at_all()
    {
        var (coordinator, service, _, _) = Build(autoUpdate: false);

        await coordinator.StartAsync();

        Assert.Equal(0, service.Checks);
        Assert.Equal(UpdatePhase.Idle, coordinator.Phase);
    }

    [Fact]
    public async Task An_explicit_check_runs_even_with_auto_update_off()
    {
        // The user asked; the preference governs the background check, not the menu item.
        var (coordinator, service, reports, _) = Build(autoUpdate: false);

        await coordinator.CheckNowAsync();

        Assert.Equal(1, service.Checks);
        Assert.Equal(UpdatePhase.Ready, coordinator.Phase);
        Assert.Contains("Checking for updates…", reports);
    }

    [Fact]
    public async Task A_found_update_is_downloaded_and_left_waiting_for_a_restart()
    {
        var (coordinator, service, reports, shutdowns) = Build();

        await coordinator.StartAsync();

        Assert.Equal(UpdatePhase.Ready, coordinator.Phase);
        Assert.Equal("0.3.0", coordinator.AvailableVersion);
        Assert.Equal(100, coordinator.Progress);
        Assert.True(coordinator.IsStaged);
        Assert.Equal(1, service.Downloads);
        // Nothing was installed and nothing was closed: the user has not been asked yet.
        Assert.Null(service.AppliedOnExit);
        Assert.Empty(shutdowns);
        // The startup path stays quiet — the strip carries the news, not the status bar.
        Assert.Empty(reports);
    }

    [Fact]
    public async Task Progress_is_reported_as_the_download_runs()
    {
        var (coordinator, service, _, _) = Build();
        service.ProgressSteps = new[] { 10, 40, 90 };
        var seen = new List<int>();
        coordinator.Changed += () =>
        {
            if (coordinator.Phase == UpdatePhase.Downloading) seen.Add(coordinator.Progress);
        };

        await coordinator.StartAsync();

        Assert.Equal(new[] { 0, 10, 40, 90 }, seen);
    }

    [Fact]
    public async Task Being_current_says_so_only_when_the_user_asked()
    {
        var (auto, _, autoReports, _) = Build(available: null);
        await auto.StartAsync();
        Assert.Equal(UpdatePhase.Idle, auto.Phase);
        Assert.Empty(autoReports);

        var (manual, _, manualReports, _) = Build(available: null);
        await manual.CheckNowAsync();
        Assert.Equal(UpdatePhase.Idle, manual.Phase);
        Assert.Contains("Bearing is up to date.", manualReports);
    }

    [Fact]
    public async Task An_unreachable_feed_is_reported_once_and_not_retried()
    {
        // The private-repo case with no BEARING_UPDATE_TOKEN set looks exactly like this.
        var (coordinator, service, reports, _) = Build();
        service.CheckThrows = new IOException("404 (Not Found)");

        await coordinator.StartAsync();
        await coordinator.StartAsync();   // a second startup call must not re-run it

        Assert.Equal(UpdatePhase.Failed, coordinator.Phase);
        Assert.Equal(1, service.Checks);
        Assert.Single(reports);
        Assert.Contains("404 (Not Found)", reports[0]);
        Assert.Contains("404 (Not Found)", coordinator.FailureMessage);
    }

    [Fact]
    public async Task A_failed_download_leaves_nothing_staged()
    {
        var (coordinator, service, reports, _) = Build();
        service.DownloadThrows = new IOException("connection reset");

        await coordinator.StartAsync();

        Assert.Equal(UpdatePhase.Failed, coordinator.Phase);
        Assert.False(coordinator.IsStaged);
        Assert.Single(reports);
        Assert.Contains("0.3.0", reports[0]);
    }

    [Fact]
    public async Task Restarting_stages_the_update_and_closes_the_app_normally()
    {
        var (coordinator, service, _, shutdowns) = Build();
        await coordinator.StartAsync();

        coordinator.RestartToApply();

        Assert.NotNull(service.AppliedOnExit);
        Assert.Equal("0.3.0", service.AppliedOnExit!.Version);
        Assert.Single(shutdowns);
        // Never the path that ends the process from under the UI — unsaved work would go with it.
        Assert.Null(service.AppliedImmediately);
    }

    [Fact]
    public async Task A_failure_to_stage_keeps_the_app_open()
    {
        var (coordinator, service, reports, shutdowns) = Build();
        await coordinator.StartAsync();
        service.ApplyThrows = new IOException("update.exe is busy");

        coordinator.RestartToApply();

        Assert.Empty(shutdowns);
        Assert.Equal(UpdatePhase.Failed, coordinator.Phase);
        Assert.Single(reports);
        Assert.Contains("update.exe is busy", reports[0]);
    }

    [Fact]
    public void Restarting_with_nothing_staged_does_nothing()
    {
        var (coordinator, service, _, shutdowns) = Build();

        coordinator.RestartToApply();

        Assert.Null(service.AppliedOnExit);
        Assert.Empty(shutdowns);
    }

    [Fact]
    public async Task Dismissing_hides_the_offer_but_keeps_the_update_staged()
    {
        var (coordinator, _, _, _) = Build();
        await coordinator.StartAsync();

        coordinator.Dismiss();

        Assert.Equal(UpdatePhase.Idle, coordinator.Phase);
        Assert.True(coordinator.IsStaged);
        Assert.Equal("0.3.0", coordinator.AvailableVersion);
    }

    [Fact]
    public async Task Checking_again_after_a_download_restates_the_offer_instead_of_re_downloading()
    {
        var (coordinator, service, reports, _) = Build();
        await coordinator.StartAsync();
        coordinator.Dismiss();

        await coordinator.CheckNowAsync();

        Assert.Equal(UpdatePhase.Ready, coordinator.Phase);
        Assert.Equal(1, service.Checks);
        Assert.Equal(1, service.Downloads);
        Assert.Contains("0.3.0", reports[0]);
    }
}
