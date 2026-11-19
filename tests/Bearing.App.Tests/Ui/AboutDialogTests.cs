using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Bearing.App.Services;
using Bearing.App.ViewModels;
using Bearing.App.Views;
using Bearing.Updates;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The About window, realized: who makes Bearing and where its links go, and that its Check for Updates
/// answers in the window — the sentence the status bar gets, then the offer to restart.
/// <para>
/// Nothing here claims a visual (§0.2/§4.3): these read text and button state off a shown window. Whether it
/// looks right still needs eyeball QA.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class AboutDialogTests
{
    private readonly UiTestSession _ui;

    public AboutDialogTests(UiTestSession ui) => _ui = ui;

    private static (UpdateViewModel Vm, FakeUpdateService Service, List<int> Shutdowns) Updates(string? available)
    {
        var service = new FakeUpdateService { Available = available };
        var shutdowns = new List<int>();
        var coordinator = new UpdateCoordinator(
            service, autoUpdateEnabled: () => true, requestShutdown: () => shutdowns.Add(1));
        return (new UpdateViewModel(coordinator), service, shutdowns);
    }

    private static AboutDialog Show(UpdateViewModel? updates, List<string?>? opened = null)
    {
        var dialog = new AboutDialog(updates, url => { opened?.Add(url); return true; });
        dialog.Show();
        Pump(dialog);
        return dialog;
    }

    private static void Pump(Window dialog)
    {
        for (var i = 0; i < 2; i++)
        {
            dialog.UpdateLayout();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        }
    }

    private static Button ButtonLabelled(Window dialog, string label)
        => dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == label);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The fake finishes synchronously, but the click is an async command: wait for what it settles to.</summary>
    private static async Task Until(Window dialog, Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(10);
            Pump(dialog);
        }
        Assert.True(done(), "the update check did not settle");
    }

    [Fact]
    public Task It_names_the_version_and_carries_Mobergs_copyright() => _ui.Run(() =>
    {
        var dialog = Show(updates: null);
        try
        {
            var texts = dialog.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

            Assert.Equal("About Bearing", dialog.Title);
            Assert.Contains("Bearing", texts);
            Assert.Contains($"Version {AppVersion.Display}", texts);
            Assert.Contains("© 2026 Moberg d.o.o.", texts);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task Its_links_open_Moberg_and_the_Bearing_repository() => _ui.Run(() =>
    {
        var opened = new List<string?>();
        var dialog = Show(updates: null, opened);
        try
        {
            Click(ButtonLabelled(dialog, "moberg.hr"));
            Click(ButtonLabelled(dialog, "Bearing on GitHub"));

            Assert.Equal(["https://www.moberg.hr", UpdateFeed.RepoUrl], opened);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task Without_an_updater_there_is_no_update_button() => _ui.Run(() =>
    {
        var dialog = Show(updates: null);
        try
        {
            Assert.Null(dialog.UpdateButton);
            Assert.DoesNotContain(dialog.GetLogicalDescendants().OfType<Button>(),
                b => b.Content as string == "Check for Updates");
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task A_check_that_finds_nothing_says_so_in_the_window() => _ui.Run(async () =>
    {
        var (vm, service, _) = Updates(available: null);
        var dialog = Show(vm);
        try
        {
            Assert.False(dialog.UpdateStatus.IsVisible);

            Click(ButtonLabelled(dialog, "Check for Updates"));
            await Until(dialog, () => dialog.UpdateStatus.Text == "Bearing is up to date.");

            Assert.Equal(1, service.Checks);
            Assert.True(dialog.UpdateStatus.IsVisible);
            Assert.Equal("Check for Updates", dialog.UpdateButton!.Content);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task A_check_that_finds_an_update_turns_the_button_into_the_restart() => _ui.Run(async () =>
    {
        var (vm, _, shutdowns) = Updates(available: "9.9.9");
        var dialog = Show(vm);
        try
        {
            Click(ButtonLabelled(dialog, "Check for Updates"));
            await Until(dialog, () => dialog.UpdateButton!.Content as string == "Restart to Update");

            Assert.Equal("Bearing 9.9.9 is ready to install.", dialog.UpdateStatus.Text);
            Assert.True(dialog.UpdateButton!.IsEnabled);

            Click(dialog.UpdateButton);
            Assert.Single(shutdowns);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task An_answer_from_before_the_window_opened_is_not_shown() => _ui.Run(async () =>
    {
        var (vm, _, _) = Updates(available: null);
        await vm.CheckNowCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Bearing is up to date.", vm.CheckStatus);

        var dialog = Show(vm);
        try
        {
            Assert.False(dialog.UpdateStatus.IsVisible);
        }
        finally { dialog.Close(); }
    });
}
