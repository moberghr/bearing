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
        set.RevertPending();   // let the harness close it: a quit over pending edits opens the real prompt
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

    /// <summary>✕ on a zone whose set the collapsed pane also holds: nothing is lost, so no question and nothing
    /// reverted — the edits stay in the pane, and the status line says where.</summary>
    [Fact]
    public Task Closing_a_zone_keeps_edits_the_collapsed_pane_holds_and_says_so() => _ui.Run(async () =>
    {
        var (shell, editor, tab, dialogs) = await Demo(nameof(Closing_a_zone_keeps_edits_the_collapsed_pane_holds_and_says_so));
        using var _ = shell;
        var set = DirtyZone(shell, tab);   // the tab's results and the zone's
        try
        {
            Assert.False(InlineResultsTests.PaneResults(shell).IsVisible);

            InlineResultsTests.Click(InlineResultsTests.ZoneButton(Assert.Single(InlineResultsTests.Zones(editor)), "Close these results"));
            await InlineZoneInputTests.WaitFor(shell, () => tab.InlineZones.Count == 0);

            Assert.Empty(dialogs.DiscardEditsPrompts);
            Assert.True(set.HasPendingChanges);
            Assert.Contains("1 unsaved change remains in the results pane", shell.Vm.StatusText);
        }
        finally
        {
            set.RevertPending();   // let the harness close it
        }
    });

    /// <summary>FK → edit → Back in the pane asks before the edits go with the frame; Cancel stays, Discard goes
    /// back and reverts them.</summary>
    [Fact]
    public Task Back_in_the_pane_asks_about_the_frames_edits() => _ui.Run(async () =>
    {
        var (shell, _, tab, dialogs) = await Demo(nameof(Back_in_the_pane_asks_about_the_frames_edits), ResultsPlacement.Pane);
        using var _ = shell;
        var first = InlineResultsTests.OneSet();
        tab.SetRunResults([first]);
        var fk = InlineZoneGridTests.Editable();
        tab.PushResults([fk]);
        fk.SetCell(fk.Rows[0], 1, "changed");
        var pane = InlineResultsTests.PaneResults(shell);

        dialogs.DiscardEditsAnswer = false;
        pane.GoBack!.Invoke();
        Assert.Equal("go back", Assert.Single(dialogs.DiscardEditsPrompts).Action);
        Assert.Same(fk, Assert.Single(tab.Results));
        Assert.True(fk.HasPendingChanges);

        dialogs.DiscardEditsAnswer = true;
        pane.GoBack!.Invoke();
        Assert.Same(first, Assert.Single(tab.Results));
        Assert.False(fk.HasPendingChanges);
    });

    /// <summary>The same in a zone: Back over a frame with edits asks, and Discard reverts them.</summary>
    [Fact]
    public Task Back_in_a_zone_asks_about_the_frames_edits() => _ui.Run(async () =>
    {
        var (shell, editor, tab, dialogs) = await Demo(nameof(Back_in_a_zone_asks_about_the_frames_edits));
        using var _ = shell;
        var first = InlineZoneGridTests.Editable();
        InlineZoneGridTests.Place(shell, tab, first);
        var zoneView = Assert.Single(InlineResultsTests.Zones(editor));
        var fk = InlineZoneGridTests.Editable();
        zoneView.Zone.Push([fk]);
        zoneView.Zone.NotifyChanged();
        shell.Pump();
        fk.SetCell(fk.Rows[0], 1, "changed");

        dialogs.DiscardEditsAnswer = false;
        zoneView.Results.GoBack!.Invoke();
        Assert.Single(dialogs.DiscardEditsPrompts);
        Assert.Same(fk, Assert.Single(zoneView.Zone.Sets));

        dialogs.DiscardEditsAnswer = true;
        zoneView.Results.GoBack!.Invoke();
        Assert.Same(first, Assert.Single(zoneView.Zone.Sets));
        Assert.False(fk.HasPendingChanges);
    });

    /// <summary>A zone holding edits whose statement is deleted, with the tab switched away before its retirement
    /// runs: kept, marked "statement removed", never dropped.</summary>
    [Fact]
    public Task A_zone_with_edits_survives_its_retirement_running_after_a_tab_switch() => _ui.Run(async () =>
    {
        var (shell, editor, tab, _) = await Demo(nameof(A_zone_with_edits_survives_its_retirement_running_after_a_tab_switch));
        using var _ = shell;
        var set = DirtyZone(shell, tab);
        try
        {
            var zone = Assert.Single(tab.InlineZones);
            editor.Document.Remove(0, "select id from shop.payment;\n".Length);   // retirement queued
            shell.Vm.Workspace.NewTab("select 42;");                              // before it runs
            shell.Pump();

            Assert.Same(zone, Assert.Single(tab.InlineZones));
            Assert.True(zone.StatementRemoved);
            Assert.Equal(0, zone.Offset);   // where its text was deleted, in that tab's text
            Assert.True(set.HasPendingChanges);
        }
        finally
        {
            set.RevertPending();   // let the harness close it
        }
    });

    /// <summary>The pane's Back works while its tab runs, as before inline results: what a run placed is told
    /// apart by its stamp, so a frame popped meanwhile is harmless.</summary>
    [Fact]
    public Task The_panes_back_works_while_the_tab_runs() => _ui.Run(async () =>
    {
        var (shell, _, tab, _) = await Demo(nameof(The_panes_back_works_while_the_tab_runs), ResultsPlacement.Pane);
        using var _ = shell;
        var first = InlineResultsTests.OneSet();
        tab.SetRunResults([first]);
        tab.PushResults([InlineResultsTests.OneSet()]);
        Assert.True(tab.CanGoBack);
        tab.BeginRun();
        try
        {
            InlineResultsTests.PaneResults(shell).GoBack!.Invoke();
            Assert.Same(first, Assert.Single(tab.Results));
            Assert.False(tab.CanGoBack);
        }
        finally
        {
            tab.EndRun();
        }
    });

    /// <summary>Back waits while an operation that changes the frames runs on the tab — an FK lookup would land
    /// on the frame Back popped — and says so. (An ordinary run doesn't block it:
    /// <see cref="The_panes_back_works_while_the_tab_runs"/>.)</summary>
    [Fact]
    public Task Back_waits_while_the_frames_are_being_changed() => _ui.Run(async () =>
    {
        var (shell, _, tab, _) = await Demo(nameof(Back_waits_while_the_frames_are_being_changed), ResultsPlacement.Pane);
        using var _ = shell;
        tab.SetRunResults([InlineResultsTests.OneSet()]);
        var navigated = InlineResultsTests.OneSet();
        tab.PushResults([navigated]);
        var pane = InlineResultsTests.PaneResults(shell);
        tab.BeginRun(RunKind.FkLookup);
        try
        {
            pane.GoBack!.Invoke();
            Assert.Same(navigated, Assert.Single(tab.Results));
            Assert.True(tab.CanGoBack);
            Assert.Equal("Wait for the foreign-key lookup to finish before going back.", shell.Vm.StatusText);
        }
        finally
        {
            tab.EndRun();
        }
        pane.GoBack!.Invoke();
        Assert.False(tab.CanGoBack);
    });

    /// <summary>Back waits while a save runs on the tab: going back would ask to discard — and revert — the edits
    /// being written.</summary>
    [Fact]
    public Task Back_waits_while_a_save_runs() => _ui.Run(async () =>
    {
        var (shell, _, tab, dialogs) = await Demo(nameof(Back_waits_while_a_save_runs), ResultsPlacement.Pane);
        using var _ = shell;
        tab.SetRunResults([InlineResultsTests.OneSet()]);
        var fk = InlineZoneGridTests.Editable();
        tab.PushResults([fk]);
        fk.SetCell(fk.Rows[0], 1, "changed");
        var pane = InlineResultsTests.PaneResults(shell);
        tab.BeginRun(RunKind.Save);
        try
        {
            pane.GoBack!.Invoke();
            Assert.Empty(dialogs.DiscardEditsPrompts);
            Assert.Same(fk, Assert.Single(tab.Results));
            Assert.True(fk.HasPendingChanges);
            Assert.Equal("Wait for the save to finish before going back.", shell.Vm.StatusText);
        }
        finally
        {
            tab.EndRun();
            fk.RevertPending();
        }
    });

    /// <summary>"Discard and go back" answered after the frame changed under the question: nothing happens, and
    /// the status line says why.</summary>
    [Fact]
    public Task Back_says_why_when_the_frame_changed_under_the_question() => _ui.Run(async () =>
    {
        var (shell, _, tab, dialogs) = await Demo(nameof(Back_says_why_when_the_frame_changed_under_the_question), ResultsPlacement.Pane);
        using var _ = shell;
        tab.SetRunResults([InlineResultsTests.OneSet()]);
        var fk = InlineZoneGridTests.Editable();
        tab.PushResults([fk]);
        fk.SetCell(fk.Rows[0], 1, "changed");
        var other = InlineResultsTests.OneSet();
        dialogs.DiscardEditsAnswer = true;
        dialogs.WhileDiscardEditsAsked = () => tab.PushResults([other]);   // e.g. a lookup landed meanwhile

        InlineResultsTests.PaneResults(shell).GoBack!.Invoke();

        Assert.Same(other, Assert.Single(tab.Results));
        Assert.True(fk.HasPendingChanges);
        Assert.Equal(Views.ResultViewWiring.BackAbandoned, shell.Vm.StatusText);
        fk.RevertPending();
    });

    /// <summary>"Discard and go back" answered after a run replaced the zone under the question: nothing is
    /// reverted, and the status line says why.</summary>
    [Fact]
    public Task Back_in_a_zone_replaced_under_the_question_does_nothing_and_says_why() => _ui.Run(async () =>
    {
        var (shell, editor, tab, dialogs) = await Demo(nameof(Back_in_a_zone_replaced_under_the_question_does_nothing_and_says_why));
        using var _ = shell;
        InlineZoneGridTests.Place(shell, tab, InlineZoneGridTests.Editable());
        var zoneView = Assert.Single(InlineResultsTests.Zones(editor));
        var zone = zoneView.Zone;
        var fk = InlineZoneGridTests.Editable();
        zone.Push([fk]);
        zone.NotifyChanged();
        shell.Pump();
        fk.SetCell(fk.Rows[0], 1, "changed");
        dialogs.DiscardEditsAnswer = true;
        dialogs.WhileDiscardEditsAsked = () => tab.RemoveInlineZone(zone.Id);   // a run replaced it meanwhile

        zoneView.Results.GoBack!.Invoke();

        Assert.Equal(Views.ResultViewWiring.BackAbandoned, shell.Vm.StatusText);
        Assert.True(fk.HasPendingChanges);
        Assert.Same(fk, Assert.Single(zone.Sets));
        fk.RevertPending();
    });

    /// <summary>Back with nothing to go back to — a stale key or click — asks nothing, even over pending edits.</summary>
    [Fact]
    public Task Back_with_nowhere_to_go_asks_nothing() => _ui.Run(async () =>
    {
        var (shell, _, tab, dialogs) = await Demo(nameof(Back_with_nowhere_to_go_asks_nothing), ResultsPlacement.Pane);
        using var _ = shell;
        var set = InlineZoneGridTests.Editable();
        tab.SetRunResults([set]);
        set.SetCell(set.Rows[0], 1, "changed");

        InlineResultsTests.PaneResults(shell).GoBack!.Invoke();

        Assert.Empty(dialogs.DiscardEditsPrompts);
        Assert.Same(set, Assert.Single(tab.Results));
        Assert.True(set.HasPendingChanges);
        set.RevertPending();
    });

    /// <summary>✕ on a zone whose set is on the hidden pane's Back stack: the status line says how to reach it —
    /// show the pane, then Back.</summary>
    [Fact]
    public Task Closing_a_zone_points_at_the_panes_history_for_edits_on_its_back_stack() => _ui.Run(async () =>
    {
        var (shell, editor, tab, _) = await Demo(nameof(Closing_a_zone_points_at_the_panes_history_for_edits_on_its_back_stack));
        using var _ = shell;
        var set = DirtyZone(shell, tab);
        try
        {
            tab.PushResults([InlineResultsTests.OneSet()]);   // the zone's set is now on the pane's Back stack

            InlineResultsTests.Click(InlineResultsTests.ZoneButton(Assert.Single(InlineResultsTests.Zones(editor)), "Close these results"));
            await InlineZoneInputTests.WaitFor(shell, () => tab.InlineZones.Count == 0);

            Assert.Contains("in the results pane's history", shell.Vm.StatusText);
            Assert.Contains("shows the pane, Back reaches them", shell.Vm.StatusText);
            Assert.True(set.HasPendingChanges);
        }
        finally
        {
            set.RevertPending();
        }
    });

    /// <summary>The same tab re-bound before a queued retirement ran: the zone's live anchor is moved to where its
    /// text was deleted, not left at its stale offset.</summary>
    [Fact]
    public Task A_retirement_after_the_same_tab_is_rebound_moves_the_live_anchor() => _ui.Run(async () =>
    {
        var (shell, editor, tab, _) = await Demo(nameof(A_retirement_after_the_same_tab_is_rebound_moves_the_live_anchor));
        using var _ = shell;
        var set = DirtyZone(shell, tab);
        try
        {
            var zone = Assert.Single(tab.InlineZones);
            editor.Document.Remove(0, "select id from shop.payment;\n".Length);   // retirement queued
            shell.Vm.Workspace.NewTab("select 42;");                              // away…
            shell.Vm.Workspace.SelectedTab = tab;                                 // …and back, before it runs
            shell.Pump();

            Assert.True(zone.StatementRemoved);
            Assert.Equal(0, zone.Offset);
            editor.Document.Insert(editor.Document.TextLength, " ");   // a later edit reads the live anchor
            shell.Pump();
            Assert.Equal(0, zone.Offset);
        }
        finally
        {
            set.RevertPending();
        }
    });

    /// <summary>✕ on a zone while a save runs on the tab is refused and says why: closing could revert the edits
    /// being written.</summary>
    [Fact]
    public Task Closing_a_zone_waits_while_a_save_runs() => _ui.Run(async () =>
    {
        var (shell, editor, tab, dialogs) = await Demo(nameof(Closing_a_zone_waits_while_a_save_runs));
        using var _ = shell;
        var set = InlineZoneGridTests.Editable();
        InlineZoneGridTests.Place(shell, tab, set);
        set.SetCell(set.Rows[0], 1, "changed");
        tab.BeginRun(RunKind.Save);
        try
        {
            InlineResultsTests.Click(InlineResultsTests.ZoneButton(Assert.Single(InlineResultsTests.Zones(editor)), "Close these results"));
            shell.Pump();

            Assert.Single(tab.InlineZones);
            Assert.Empty(dialogs.DiscardEditsPrompts);
            Assert.True(set.HasPendingChanges);
            Assert.Equal("Wait for the save to finish before closing these results.", shell.Vm.StatusText);
        }
        finally
        {
            tab.EndRun();
            set.RevertPending();
        }
    });

    /// <summary>A second deletion reported for a zone whose retirement is already queued moves it to the later
    /// point instead of being dropped.</summary>
    [Fact]
    public Task A_queued_retirement_takes_the_latest_deletion_point() => _ui.Run(async () =>
    {
        var (shell, editor, tab, _) = await Demo(nameof(A_queued_retirement_takes_the_latest_deletion_point));
        using var _ = shell;
        var set = DirtyZone(shell, tab);   // a zone at 9, inside "select id from …"
        try
        {
            var zone = Assert.Single(tab.InlineZones);
            editor.Document.Remove(0, "select id from shop.payment;\n".Length);   // retirement queued at 0
            shell.Vm.Workspace.NewTab("select 42;");
            shell.Vm.Workspace.SelectedTab = tab;   // re-bound: a live anchor at the stale offset 9, inside "select 2;"
            editor.Document.Remove(5, 5);           // deletes it too: a second deletion point, 5, while still queued
            shell.Pump();

            Assert.True(zone.StatementRemoved);
            Assert.Equal(5, zone.Offset);
        }
        finally
        {
            set.RevertPending();
        }
    });

    /// <summary>Back in a zone off a frame whose edits the hidden pane also holds: nothing asked, nothing reverted,
    /// and the status line says where they stay — as ✕ does.</summary>
    [Fact]
    public Task Back_in_a_zone_says_where_edits_the_hidden_pane_holds_stay() => _ui.Run(async () =>
    {
        var (shell, editor, tab, dialogs) = await Demo(nameof(Back_in_a_zone_says_where_edits_the_hidden_pane_holds_stay));
        using var _ = shell;
        InlineZoneGridTests.Place(shell, tab, InlineZoneGridTests.Editable());
        var zoneView = Assert.Single(InlineResultsTests.Zones(editor));
        var shared = InlineZoneGridTests.Editable();
        tab.SetRunResults([shared]);              // the pane's current frame…
        zoneView.Zone.Push([shared]);             // …is also the zone's top frame
        zoneView.Zone.NotifyChanged();
        shell.Pump();
        shared.SetCell(shared.Rows[0], 1, "changed");
        try
        {
            Assert.False(InlineResultsTests.PaneResults(shell).IsVisible);

            zoneView.Results.GoBack!.Invoke();

            Assert.Empty(dialogs.DiscardEditsPrompts);
            Assert.True(shared.HasPendingChanges);
            Assert.Contains("1 unsaved change remains in the results pane", shell.Vm.StatusText);
        }
        finally
        {
            shared.RevertPending();
        }
    });
}
