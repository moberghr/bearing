using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Bearing.Core.Workspace;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Guards the contract that makes the settings window generic: the catalog fully describes
/// <see cref="AppSettings"/>, and every descriptor reads and writes the property it claims to. If these
/// hold, a setting added to the catalog renders, searches, persists and resets without any UI change.
/// </summary>
public class SettingsCatalogTests
{
    /// <summary>
    /// Properties of <see cref="AppSettings"/> that are persisted state rather than preferences, and so
    /// deliberately have no row in the settings window. Adding a property without either a descriptor or
    /// an entry here fails <see cref="Every_setting_is_either_described_or_declared_hidden"/> — which is
    /// the point: it's the reminder to finish the second half of "add a setting".
    /// </summary>
    private static readonly HashSet<string> HiddenState = new()
    {
        nameof(AppSettings.WindowWidth),
        nameof(AppSettings.WindowHeight),
        nameof(AppSettings.LastSeenVersion),
    };

    [Fact]
    public void Every_setting_is_either_described_or_declared_hidden()
    {
        var described = SettingsCatalog.All.Select(d => PropertyNameOf(d)).ToHashSet();

        var undescribed = typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => !described.Contains(name) && !HiddenState.Contains(name))
            .ToList();

        Assert.True(undescribed.Count == 0,
            $"AppSettings properties with no SettingsCatalog entry (add one, or list them as hidden state): "
            + string.Join(", ", undescribed));
    }

    [Fact]
    public void Keys_are_unique_and_categories_exist()
    {
        var duplicates = SettingsCatalog.All.GroupBy(d => d.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Duplicate setting keys: " + string.Join(", ", duplicates));

        var categoryIds = SettingsCatalog.Categories.Select(c => c.Id).ToHashSet();
        var orphans = SettingsCatalog.All.Where(d => !categoryIds.Contains(d.CategoryId)).Select(d => d.Key).ToList();
        Assert.True(orphans.Count == 0, "Settings in no declared category: " + string.Join(", ", orphans));
    }

    [Fact]
    public void Every_descriptor_reads_back_what_it_writes()
    {
        foreach (var descriptor in SettingsCatalog.All)
        {
            var changed = descriptor.Write(AppSettings.Defaults, Alternative(descriptor));
            Assert.Equal(Alternative(descriptor), descriptor.Read(changed));

            // …and writing one setting leaves every other alone.
            foreach (var other in SettingsCatalog.All.Where(o => o.Key != descriptor.Key))
                Assert.Equal(other.Default, other.Read(changed));
        }
    }

    [Fact]
    public void Reset_returns_a_changed_setting_to_its_default()
    {
        foreach (var descriptor in SettingsCatalog.All)
        {
            var changed = descriptor.Write(AppSettings.Defaults, Alternative(descriptor));
            Assert.False(descriptor.IsDefault(changed));
            Assert.True(descriptor.IsDefault(descriptor.Reset(changed)));
        }
    }

    [Fact]
    public void Enum_options_are_real_members_and_cover_the_enum()
    {
        foreach (var e in SettingsCatalog.All.OfType<EnumSetting>())
        {
            Assert.NotEmpty(e.Options);

            // The default value must be selectable, or the dropdown opens blank on a fresh install.
            Assert.NotNull(e.Selected(AppSettings.Defaults));

            foreach (var option in e.Options)
            {
                // Each option must round-trip: this is what lets a descriptor's Set use a bare Enum.Parse.
                var applied = e.Write(AppSettings.Defaults, option.Value);
                Assert.Equal(option.Value, e.Read(applied));
            }
        }
    }

    [Fact]
    public void Int_settings_clamp_instead_of_throwing()
    {
        foreach (var i in SettingsCatalog.All.OfType<IntSetting>())
        {
            Assert.Equal(i.Min, i.Read(i.Write(AppSettings.Defaults, i.Min - 1)));
            Assert.Equal(i.Max, i.Read(i.Write(AppSettings.Defaults, i.Max + 1)));

            // A UI control hands back a double; the descriptor must not silently ignore it.
            Assert.Equal(i.Min + 1, i.Read(i.Write(AppSettings.Defaults, (double)(i.Min + 1))));
        }
    }

    [Fact]
    public void Enum_setting_ignores_a_value_that_is_not_an_option()
    {
        var autosave = (EnumSetting)SettingsCatalog.Find("editor.autosaveMode")!;
        var before = new AppSettings { AutosaveMode = AutosaveMode.OnExecute };

        Assert.Equal(before, autosave.Write(before, "NotAMode"));
        Assert.Equal(before, autosave.Write(before, 7));
    }

    [Fact]
    public void Bool_and_int_settings_ignore_a_wrongly_typed_value()
    {
        var confirm = (BoolSetting)SettingsCatalog.Find("editor.confirmTabClose")!;
        var before = new AppSettings { ConfirmTabClose = false };
        Assert.Equal(before, confirm.Write(before, "yes"));

        var font = (IntSetting)SettingsCatalog.Find("editor.fontSize")!;
        var fontBefore = new AppSettings { EditorFontSize = 17 };
        Assert.Equal(17, font.Read(font.Write(fontBefore, "big")));
    }

    [Fact]
    public void Settings_that_do_not_apply_immediately_say_so()
    {
        // The window applies edits as they're made, so a row that can't take effect until later has to
        // carry a note. These two are the known cases; the assertion exists to catch a third being added
        // without one.
        Assert.NotNull(SettingsCatalog.Find("history.retentionDays")!.AppliesNote);
        Assert.NotNull(SettingsCatalog.Find("results.pageSize")!.AppliesNote);
    }

    /// <summary>A value that differs from the descriptor's default, whatever kind it is.</summary>
    private static object Alternative(SettingDescriptor descriptor) => descriptor switch
    {
        BoolSetting b => !(bool)b.Default!,
        IntSetting i => (int)i.Default! == i.Min ? i.Min + 1 : i.Min,
        EnumSetting e => e.Options.First(o => o.Value != (string)e.Default!).Value,
        _ => throw new NotSupportedException($"Unhandled setting kind {descriptor.GetType().Name}"),
    };

    /// <summary>The <see cref="AppSettings"/> property a descriptor actually reads, discovered by writing
    /// a distinct value and seeing which property moved. Keeps the coverage test honest without making
    /// descriptors declare their property name twice.</summary>
    private static string PropertyNameOf(SettingDescriptor descriptor)
    {
        var changed = descriptor.Write(AppSettings.Defaults, Alternative(descriptor));
        var moved = typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => !Equals(p.GetValue(changed), p.GetValue(AppSettings.Defaults)))
            .ToList();

        Assert.True(moved.Count == 1,
            $"Setting '{descriptor.Key}' should change exactly one AppSettings property, changed {moved.Count}.");
        return moved[0].Name;
    }
}
