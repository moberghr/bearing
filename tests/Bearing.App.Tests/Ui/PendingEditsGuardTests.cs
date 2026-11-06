using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Input;
using AvaloniaEdit;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using Bearing.Demo;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Unsaved grid edits are never dropped silently (docs/specs/2026-11-06-inline-results.md §7) — in a zone or in
/// the pane: a run over them asks, Cancel keeps them, Discard runs; ✕ asks; deleting the statement keeps a
/// zone that holds edits; closing the tab asks.
/// </summary>
[Collection(UiTestCollection.Name)]
public class PendingEditsGuardTests
{
    private readonly UiTestSession _ui;

    public PendingEditsGuardTests(UiTestSession ui) => _ui = ui;

    private const string Text = "select id from shop.payment;\n\nselect 2;\n";

    private static async Task<(ShellHarness Shell, TextEditor Editor, EditorTabViewModel Tab, FakeDialogs Dialogs)> Demo(
        string name, ResultsPlacement placement = ResultsPlacement.Inline)
    {
        var dialogs = new FakeDialogs();
        var providers = new DemoProvider(DemoExecutor.Default(sql => StatementSplitter.Split(sql).Select(s => s.Text).ToList()));
        var shell = await ShellHarness.ShowAsync(name, providers, dialogs);
        await shell.Vm.StartDemoAsync(shell.ProjectDirectory, DemoMode.WelcomeScript);
        var (editor, tab) = InlineResultsTests.Setup(shell, Text);
        shell.Vm.SettingsService.Update(s => s with { ResultsPlacement = placement });
        shell.Pump();
        return (shell, editor, tab, dialogs);
    }

    /// <summary>A zone under the first statement whose set holds one unsaved change.</summary>
    private static ResultSetViewModel DirtyZone(ShellHarness shell, EditorTabViewModel tab)
    {
        var set = InlineZoneGridTests.Editable();
        tab.SetRunResults([set]);
        InlineZoneGridTests.Place(shell, tab, set);
        set.SetCell(set.Rows[0], 1, "changed");
        return set;
    }

    private static void PressF5(ShellHarness shell)
    {
        shell.Window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        shell.Window.KeyRelease(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        shell.Pump();
    }

    [Fact]
    public Task Cancel_keeps_the_zone_and_its_edits_and_runs_nothing() => _ui.Run(async () =>
    {
        var (shell, editor, tab, dialogs) = await Demo(nameof(Cancel_keeps_the_zone_and_its_edits_and_runs_nothing));
        using var _ = shell;
        var set = DirtyZone(shell, tab);
        var zone = tab.InlineZones.Single();
        dialogs.DiscardEditsAnswer = false;
        editor.CaretOffset = 3;

        PressF5(shell);
        await InlineZoneInputTests.WaitFor(shell, () => dialogs.DiscardEditsPrompts.Count > 0);

        var (what, action) = Assert.Single(dialogs.DiscardEditsPrompts);
        Assert.Equal("1 unsaved change", what);
        Assert.Equal("run", action);
        Assert.Same(zone, tab.InlineZones.Single());
        Assert.True(set.HasPendingChanges);
        Assert.Same(set, Assert.Single(tab.Results));
    });

    [Fact]
    public Task Discard_reverts_the_edits_and_runs() => _ui.Run(async () =>
    {
        var (shell, editor, tab, dialogs) = await Demo(nameof(Discard_reverts_the_edits_and_runs));
        using var _ = shell;
        var set = DirtyZone(shell, tab);
        var zone = tab.InlineZones.Single();
        editor.CaretOffset = 3;

        PressF5(shell);
        await InlineZoneInputTests.WaitFor(shell, () => !ReferenceEquals(tab.InlineZones.SingleOrDefault(), zone));

        Assert.Single(dialogs.DiscardEditsPrompts);
        Assert.False(set.HasPendingChanges);
        Assert.NotSame(zone, Assert.Single(tab.InlineZones));
    });

    [Fact]
    public Task A_run_elsewhere_doesnt_ask_about_a_zone_it_wont_replace() => _ui.Run(async () =>
    {
        var (shell, editor, tab, dialogs) = await Demo(nameof(A_run_elsewhere_doesnt_ask_about_a_zone_it_wont_replace));
        using var _ = shell;
        var set = InlineZoneGridTests.Editable();
        InlineZoneGridTests.Place(shell, tab, set);   // a zone only — the pane holds nothing
        set.SetCell(set.Rows[0], 1, "changed");
        editor.CaretOffset = Text.IndexOf("select 2") + 3;

        PressF5(shell);
        await InlineZoneInputTests.WaitFor(shell, () => tab.InlineZones.Count == 2);

        Assert.Empty(dialogs.DiscardEditsPrompts);
        Assert.True(set.HasPendingChanges);
    });

    [Fact]
    public Task In_pane_mode_a_rerun_over_edits_asks_too() => _ui.Run(async () =>
    {
        var (shell, editor, tab, dialogs) = await Demo(nameof(In_pane_mode_a_rerun_over_edits_asks_too), ResultsPlacement.Pane);
        using var _ = shell;
        var set = InlineZoneGridTests.Editable();
        tab.SetRunResults([set]);
        set.SetCell(set.Rows[0], 1, "changed");
        dialogs.DiscardEditsAnswer = false;
        editor.CaretOffset = Text.IndexOf("select 2") + 3;   // even a run of another statement replaces the pane

        PressF5(shell);
        await InlineZoneInputTests.WaitFor(shell, () => dialogs.DiscardEditsPrompts.Count > 0);

        Assert.Single(dialogs.DiscardEditsPrompts);
        Assert.Same(set, Assert.Single(tab.Results));
        Assert.True(set.HasPendingChanges);
    });

    [Fact]
    public Task Closing_a_zone_with_edits_asks_first() => _ui.Run(async () =>
    {
        var (shell, editor, tab, dialogs) = await Demo(nameof(Closing_a_zone_with_edits_asks_first));
        using var _ = shell;
        // Only the zone holds the set: one the pane also shows isn't lost by closing the zone.
        var set = InlineZoneGridTests.Editable();
        InlineZoneGridTests.Place(shell, tab, set);
        set.SetCell(set.Rows[0], 1, "changed");
        dialogs.DiscardEditsAnswer = false;
        var close = InlineResultsTests.ZoneButton(Assert.Single(InlineResultsTests.Zones(editor)), "Close these results");

        InlineResultsTests.Click(close);
        await InlineZoneInputTests.WaitFor(shell, () => dialogs.DiscardEditsPrompts.Count > 0);
        Assert.Single(tab.InlineZones);
        Assert.Equal("close these results", dialogs.DiscardEditsPrompts[0].Action);

        dialogs.DiscardEditsAnswer = true;
        InlineResultsTests.Click(close);
        await InlineZoneInputTests.WaitFor(shell, () => tab.InlineZones.Count == 0);
        Assert.Empty(tab.InlineZones);
    });

    [Fact]
    public Task Deleting_the_statement_keeps_a_zone_holding_edits() => _ui.Run(async () =>
    {
        var (shell, editor, tab, _) = await Demo(nameof(Deleting_the_statement_keeps_a_zone_holding_edits));
        using var _ = shell;
        var set = DirtyZone(shell, tab);

        editor.Document.Remove(0, "select id from shop.payment;\n".Length);
        shell.Pump();

        var zone = Assert.Single(tab.InlineZones);
        Assert.True(zone.StatementRemoved);
        Assert.True(set.HasPendingChanges);
        Assert.Single(InlineResultsTests.Zones(editor));
    });

    [Fact]
    public Task Closing_the_tab_asks_about_its_edits() => _ui.Run(async () =>
    {
        var (shell, _, tab, dialogs) = await Demo(nameof(Closing_the_tab_asks_about_its_edits));
        using var _ = shell;
        DirtyZone(shell, tab);
        dialogs.DiscardEditsAnswer = false;

        Assert.False(await shell.Vm.Workspace.CloseTabAsync(tab));

        var prompt = Assert.Single(dialogs.DiscardEditsPrompts);
        Assert.StartsWith("close ", prompt.Action);
        Assert.Contains(tab, shell.Vm.Workspace.Tabs);
    });
}
