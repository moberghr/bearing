using System;
using System.IO;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The menu bar's two modes. Alt-tap-to-reveal is invisible to anyone who doesn't know the gesture, so it
/// can be pinned open (Settings ▸ General) — and then nothing may auto-hide it, and it must stop counting as
/// a modal-ish surface that owns Escape and suppresses global shortcuts.
/// <para>
/// The four auto-hide paths themselves live in the window's code-behind and all ask
/// <see cref="ShellViewModel.IsMenuTransient"/>; that flag is what's pinned here. Whether the bar actually
/// stays put on screen is eyeball-QA (§4.3).
/// </para>
/// </summary>
public class MenuBarPinTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-menubar", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private (ShellViewModel Vm, SettingsService Settings) NewVm(bool pinned = false)
    {
        var settings = SettingsService.InMemory(new AppSettings { ShowMenuBar = pinned });
        var vm = new ShellViewModel(
            new FakeProvider(),
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            settings: settings);
        return (vm, settings);
    }

    [Fact]
    public void Unpinned_the_bar_starts_hidden_and_a_reveal_is_transient()
    {
        var (vm, _) = NewVm();
        Assert.False(vm.IsMenuVisible);
        Assert.False(vm.IsMenuPinned);

        vm.IsMenuVisible = true;   // an Alt tap

        Assert.True(vm.IsMenuTransient);   // owns Escape, suppresses global keys
    }

    [Fact]
    public void Pinned_the_bar_starts_visible_and_is_never_transient()
    {
        var (vm, _) = NewVm(pinned: true);

        Assert.True(vm.IsMenuVisible);
        Assert.True(vm.IsMenuPinned);
        Assert.False(vm.IsMenuTransient);   // Escape and every global shortcut must go straight past it
    }

    [Fact]
    public void Pinning_at_runtime_shows_the_bar_and_unpinning_takes_it_away()
    {
        var (vm, settings) = NewVm();

        settings.Update(s => s with { ShowMenuBar = true });
        Assert.True(vm.IsMenuPinned);
        Assert.True(vm.IsMenuVisible);
        Assert.False(vm.IsMenuTransient);

        settings.Update(s => s with { ShowMenuBar = false });
        Assert.False(vm.IsMenuPinned);
        // Not left stranded on screen: unpinned, nothing but the hide paths would ever take it down again.
        Assert.False(vm.IsMenuVisible);
    }

    [Fact]
    public void The_transient_flag_notifies_so_the_hide_paths_re_evaluate()
    {
        var (vm, settings) = NewVm();
        var raised = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShellViewModel.IsMenuTransient)) raised++; };

        vm.IsMenuVisible = true;
        Assert.True(raised > 0);

        raised = 0;
        settings.Update(s => s with { ShowMenuBar = true });
        Assert.True(raised > 0);
    }
}
