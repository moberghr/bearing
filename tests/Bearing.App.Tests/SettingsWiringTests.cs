using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.Services;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The half that a catalog test can't prove: that changing a setting changes what the app <i>does</i>,
/// without a restart. One test per consumer, since each reaches its setting differently — read-per-use,
/// mirrored onto a bound property, or pushed into a service that caches it.
/// </summary>
public class SettingsWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-settings", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private ShellViewModel NewVm(SettingsService settings, IDialogService? dialogs = null) => new(
        new ProviderRegistry(),
        new JsonProjectStore(),
        new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(Path.Combine(_root, "recent.json")),
        dialogs: dialogs,
        settings: settings);

    [Fact]
    public void Page_size_follows_the_setting_with_no_restart()
    {
        var settings = SettingsService.InMemory();
        var vm = NewVm(settings);
        Assert.Equal(AppSettings.Defaults.ResultPageSize, vm.Execution.PageSize);

        settings.Set(SettingsCatalog.Find("results.pageSize")!, 250);

        Assert.Equal(250, vm.Execution.PageSize);
    }

    [Fact]
    public void Editor_font_size_is_mirrored_onto_the_bound_property()
    {
        var settings = SettingsService.InMemory(new AppSettings { EditorFontSize = 12 });
        var vm = NewVm(settings);
        Assert.Equal(12, vm.EditorFontSize);

        var raised = false;
        vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(vm.EditorFontSize);
        settings.Set(SettingsCatalog.Find("editor.fontSize")!, 19);

        Assert.Equal(19, vm.EditorFontSize);
        Assert.True(raised, "the editor binds FontSize, so the change has to notify");
    }

    [Fact]
    public void Idle_timeout_is_pushed_into_the_session_manager()
    {
        var settings = SettingsService.InMemory(new AppSettings { ConnectionIdleTimeoutMinutes = 5 });
        var ctx = new WorkspaceContext(
            new FakeProvider(), new JsonProjectStore(), new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "idle.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "idle-recent.json")),
            settings: settings);

        var sessions = Assert.IsType<ConnectionSessionManager>(ctx.Sessions);
        Assert.Equal(TimeSpan.FromMinutes(5), sessions.IdleTimeout);

        settings.Set(SettingsCatalog.Find("connections.idleTimeoutMinutes")!, 45);

        Assert.Equal(TimeSpan.FromMinutes(45), sessions.IdleTimeout);
    }

    [Fact]
    public async Task Confirm_tab_close_off_closes_a_dirty_tab_without_asking()
    {
        var settings = SettingsService.InMemory(new AppSettings
        {
            AutosaveMode = AutosaveMode.Off,   // …so a named buffer can actually be dirty
            ConfirmTabClose = false,
        });
        var dialogs = new FakeDialogs(CloseChoice.Cancel);   // would keep the tab open if it were asked
        // No project on purpose: autosave then has nowhere to write, so the buffer is still unsaved by
        // the time the gate is evaluated. This is the same backstop CloseTabPromptTests uses.
        var vm = NewVm(settings, dialogs);
        var tab = vm.Workspace.NewTab();
        tab.Text = "select 1;";
        Assert.True(tab.HasUnsavedWork);

        Assert.True(await vm.Workspace.CloseTabAsync(tab));
        Assert.Empty(dialogs.ClosePrompts);
        Assert.DoesNotContain(tab, vm.Workspace.Tabs);
    }

    [Fact]
    public async Task Turning_the_confirm_back_on_restores_the_prompt()
    {
        // The same VM, mid-session: the setting is read at each close, not captured at construction.
        var settings = SettingsService.InMemory(new AppSettings
        {
            AutosaveMode = AutosaveMode.Off,
            ConfirmTabClose = false,
        });
        var dialogs = new FakeDialogs(CloseChoice.Cancel);
        var vm = NewVm(settings, dialogs);
        settings.Set(SettingsCatalog.Find("editor.confirmTabClose")!, true);

        var tab = vm.Workspace.NewTab();
        tab.Text = "select 1;";

        Assert.False(await vm.Workspace.CloseTabAsync(tab));
        Assert.Single(dialogs.ClosePrompts);
        Assert.Contains(tab, vm.Workspace.Tabs);
    }
}
