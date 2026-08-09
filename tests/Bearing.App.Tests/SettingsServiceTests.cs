using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Settings;
using Bearing.Core.Workspace;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The settings window has no Save button, so <see cref="SettingsService"/> is what makes an edit real:
/// apply, persist, broadcast. These cover that contract, including what an unwritable file does.
/// </summary>
public class SettingsServiceTests
{
    [Fact]
    public void Update_applies_persists_and_broadcasts()
    {
        var store = new FakeSettingsStore();
        var service = new SettingsService(store);
        var broadcasts = new List<AppSettings>();
        service.Changed += broadcasts.Add;

        service.Update(s => s with { EditorFontSize = 18 });

        Assert.Equal(18, service.Current.EditorFontSize);
        Assert.Equal(18, Assert.Single(store.Saves).EditorFontSize);
        Assert.Equal(18, Assert.Single(broadcasts).EditorFontSize);
    }

    [Fact]
    public void An_edit_that_changes_nothing_neither_writes_nor_broadcasts()
    {
        // Re-picking the value a dropdown already shows must not churn the file on disk.
        var store = new FakeSettingsStore();
        var service = new SettingsService(store);
        var broadcasts = 0;
        service.Changed += _ => broadcasts++;

        service.Update(s => s with { EditorFontSize = service.Current.EditorFontSize });
        service.Set(SettingsCatalog.Find("editor.autosaveMode")!, AutosaveMode.OnEdit.ToString());

        Assert.Empty(store.Saves);
        Assert.Equal(0, broadcasts);
    }

    [Fact]
    public void An_unwritable_file_reports_but_still_applies_for_the_session()
    {
        // Reverting the value under the user's cursor because the disk is full would be the worse
        // failure: the edit stands, the failure is surfaced, nothing throws (§5.2).
        var store = new FakeSettingsStore { ThrowOnSave = true };
        var service = new SettingsService(store);
        string? reported = null;
        service.SaveFailed = m => reported = m;

        service.Update(s => s with { EditorFontSize = 20 });

        Assert.Equal(20, service.Current.EditorFontSize);
        Assert.NotNull(reported);
        Assert.Contains("disk full", reported);
    }

    [Fact]
    public void A_save_failure_with_no_sink_is_dropped_not_thrown()
    {
        var service = new SettingsService(new FakeSettingsStore { ThrowOnSave = true });
        service.Update(s => s with { EditorFontSize = 20 });   // must not throw
        Assert.Equal(20, service.Current.EditorFontSize);
    }

    [Fact]
    public void Set_and_Reset_go_through_the_descriptor()
    {
        var service = new SettingsService(new FakeSettingsStore());
        var font = SettingsCatalog.Find("editor.fontSize")!;

        service.Set(font, 22);
        Assert.Equal(22, service.Current.EditorFontSize);

        service.Reset(font);
        Assert.Equal(AppSettings.Defaults.EditorFontSize, service.Current.EditorFontSize);
    }

    [Fact]
    public void ResetAll_clears_preferences_but_keeps_persisted_window_size()
    {
        var service = new SettingsService(new FakeSettingsStore(new AppSettings
        {
            EditorFontSize = 22,
            AutosaveMode = AutosaveMode.Off,
            QueryLogRetentionDays = 7,
            WindowWidth = 1234,
            WindowHeight = 900,
        }));

        service.ResetAll();

        Assert.Equal(AppSettings.Defaults.EditorFontSize, service.Current.EditorFontSize);
        Assert.Equal(AppSettings.Defaults.AutosaveMode, service.Current.AutosaveMode);
        Assert.Equal(AppSettings.Defaults.QueryLogRetentionDays, service.Current.QueryLogRetentionDays);

        // Window size isn't a preference — resetting "settings" must not resize the window next launch.
        Assert.Equal(1234, service.Current.WindowWidth);
        Assert.Equal(900, service.Current.WindowHeight);
    }

    [Fact]
    public void Load_supplies_the_starting_values()
    {
        var service = new SettingsService(new FakeSettingsStore(new AppSettings { EditorFontSize = 11 }));
        Assert.Equal(11, service.Current.EditorFontSize);
    }
}
