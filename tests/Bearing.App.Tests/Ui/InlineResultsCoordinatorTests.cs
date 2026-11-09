using System.Threading.Tasks;
using Avalonia.Controls;
using AvaloniaEdit;
using Bearing.App.Editing;
using Bearing.App.Results;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// When the results pane opens for a run (<see cref="InlineResultsCoordinator"/>): in inline mode a run whose
/// results can't be placed under their statement owes the user the pane — now if its tab is shown, else the
/// next time it is. Over a bare editor, with the window's pane and tab switch stood in by delegates.
/// </summary>
[Collection(UiTestCollection.Name)]
public class InlineResultsCoordinatorTests
{
    private readonly UiTestSession _ui;

    public InlineResultsCoordinatorTests(UiTestSession ui) => _ui = ui;

    private const string Buffer = "select 1;\nselect 2;\n";

    private sealed class Harness
    {
        public required TextEditor Editor { get; init; }
        public required InlineResultsCoordinator Coordinator { get; init; }
        public required SettingsService Settings { get; init; }
        public EditorTabViewModel? Selected { get; set; }
        public bool PaneVisible { get; set; }
        public System.Collections.Generic.List<string> Statuses { get; } = new();

        /// <summary>What the window does on a tab switch: unbind, swap the buffer, bind, render results.</summary>
        public void Select(EditorTabViewModel tab)
        {
            Coordinator.Controller.Unbind();
            Editor.Text = tab.Text;
            Selected = tab;
            Coordinator.Controller.Bind(tab);
            Rebuild(tab);
        }

        public void Rebuild(EditorTabViewModel? tab)
        {
            if (Coordinator.PaneVisibilityFor(tab) is { } visible)
            {
                PaneVisible = visible;
            }
        }
    }

    private static Harness Build(InlineResultsCoordinator.Host? host = null,
        ResultsPlacement placement = ResultsPlacement.Inline)
    {
        var editor = new TextEditor();
        var window = new Window { Content = editor, Width = 600, Height = 400 };
        window.Show();
        var settings = SettingsService.InMemory(new AppSettings { ResultsPlacement = placement });
        Harness? h = null;
        var coordinator = new InlineResultsCoordinator(editor, () => settings, () => h!.Selected,
            tab => h!.Rebuild(tab), visible => h!.PaneVisible = visible, () => h!.PaneVisible, text => h!.Statuses.Add(text), () => { }, () => host);
        h = new Harness { Editor = editor, Coordinator = coordinator, Settings = settings };
        settings.Changed += coordinator.OnSettingsChanged;
        coordinator.SyncPlacement();
        return h;
    }

    /// <summary>A host that records the unsaved-edits questions and answers <paramref name="answer"/>.</summary>
    private static InlineResultsCoordinator.Host Asking(System.Collections.Generic.List<string> asked, bool answer,
        System.Action? commitOpenEdits = null)
        => new((_, _, _) => { }, _ => false,
            (what, _) => { asked.Add(what); return Task.FromResult(answer); },
            commitOpenEdits ?? (() => { }), _ => false);

    private static RunScope SecondStatement()
        => InlineResultPlacement.Scope(PostgresDialect.Instance, "select 2;", "select 1;\n".Length, isWholeBuffer: false);

    [Fact]
    public Task A_placed_run_leaves_the_pane_closed() => _ui.Run(async () =>
    {
        var h = Build();
        var tab = new EditorTabViewModel("t", Buffer);
        h.Select(tab);

        await h.Coordinator.RunAsync(SecondStatement(), (_, _) =>
        {
            tab.SetRunResults([InlineResultsTests.OneSet()]);
            return Task.CompletedTask;
        });

        Assert.Single(tab.InlineZones);
        Assert.False(h.PaneVisible);
    });

    /// <summary>The run finished on a background tab whose text changed under a rebind: it can't be placed, so
    /// switching back must open the pane — otherwise its results would be nowhere on screen.</summary>
    [Fact]
    public Task An_unplaceable_run_that_finished_in_the_background_opens_the_pane_on_return() => _ui.Run(async () =>
    {
        var h = Build();
        var tab = new EditorTabViewModel("t", Buffer);
        var other = new EditorTabViewModel("o", "select 42;");
        h.Select(tab);

        await h.Coordinator.RunAsync(SecondStatement(), (_, _) =>
        {
            tab.Text = Buffer + "-- typed while it ran\n";
            h.Select(other);                       // switched away; the run lands there
            tab.SetRunResults([InlineResultsTests.OneSet()]);
            return Task.CompletedTask;
        });
        Assert.Empty(tab.InlineZones);
        Assert.False(h.PaneVisible);               // not on screen yet: another tab is shown

        h.Select(tab);

        Assert.True(h.PaneVisible);
    });

    /// <summary>The debt is paid once: a later switch back to the tab doesn't reopen a pane the user closed.</summary>
    [Fact]
    public Task The_pane_is_owed_only_once() => _ui.Run(async () =>
    {
        var h = Build();
        var tab = new EditorTabViewModel("t", Buffer);
        var other = new EditorTabViewModel("o", "select 42;");
        h.Select(tab);
        await h.Coordinator.RunAsync(SecondStatement(), (_, _) =>
        {
            tab.Text = Buffer + "-- typed while it ran\n";
            h.Select(other);
            tab.SetRunResults([InlineResultsTests.OneSet()]);
            return Task.CompletedTask;
        });
        h.Select(tab);
        h.PaneVisible = false;                     // the user closes it

        h.Select(other);
        h.Select(tab);

        Assert.False(h.PaneVisible);
    });

    /// <summary>A zone under the second statement whose set holds one unsaved change.</summary>
    private static ResultSetViewModel DirtyZoneOnSecond(EditorTabViewModel tab)
    {
        var set = ResultContextTestsAccess.Editable();
        tab.ApplyInlineRun(SecondStatement(), [new InlineResultZone(SecondStatement().End, [set], null)]);
        set.SetCell(set.Rows[0], 1, "changed");
        return set;
    }

    /// <summary>A run that execution refuses (read-only, busy, no connection) never reaches the question, so
    /// nothing is asked and nothing reverted.</summary>
    [Fact]
    public Task A_refused_run_neither_asks_nor_reverts() => _ui.Run(async () =>
    {
        var asked = new System.Collections.Generic.List<string>();
        var h = Build(Asking(asked, answer: true));
        var tab = new EditorTabViewModel("t", Buffer);
        h.Select(tab);
        var set = DirtyZoneOnSecond(tab);

        await h.Coordinator.RunAsync(SecondStatement(), (_, _) => Task.CompletedTask);   // refused: never asks

        Assert.Empty(asked);
        Assert.True(set.HasPendingChanges);
        Assert.Single(tab.InlineZones);
    });

    /// <summary>Discard was chosen, but the run produced no results (failed to connect, cancelled): the zone
    /// is still there, so its edits are too.</summary>
    [Fact]
    public Task Edits_given_up_for_a_run_that_produced_nothing_are_kept() => _ui.Run(async () =>
    {
        var asked = new System.Collections.Generic.List<string>();
        var h = Build(Asking(asked, answer: true));
        var tab = new EditorTabViewModel("t", Buffer);
        h.Select(tab);
        var set = DirtyZoneOnSecond(tab);

        await h.Coordinator.RunAsync(SecondStatement(), async (_, confirm) => Assert.True(await confirm()));

        Assert.Single(asked);
        Assert.True(set.HasPendingChanges);
    });

    [Fact]
    public Task Edits_given_up_are_reverted_once_the_run_replaced_them() => _ui.Run(async () =>
    {
        var asked = new System.Collections.Generic.List<string>();
        var h = Build(Asking(asked, answer: true));
        var tab = new EditorTabViewModel("t", Buffer);
        h.Select(tab);
        var set = DirtyZoneOnSecond(tab);

        await h.Coordinator.RunAsync(SecondStatement(), async (_, confirm) =>
        {
            Assert.True(await confirm());
            tab.SetRunResults([InlineResultsTests.OneSet()]);
        });

        Assert.Single(asked);
        Assert.False(set.HasPendingChanges);
    });

    /// <summary>A value still in a cell editor is committed before the guard counts (§9.15) — here the host's
    /// commit is what makes the edit, so the question is asked only if the commit ran first.</summary>
    [Fact]
    public Task An_open_cell_edit_is_committed_before_the_guard_counts() => _ui.Run(async () =>
    {
        var asked = new System.Collections.Generic.List<string>();
        ResultSetViewModel? set = null;
        var h = Build(Asking(asked, answer: false, commitOpenEdits: () => set!.SetCell(set.Rows[0], 1, "typed")));
        var tab = new EditorTabViewModel("t", Buffer);
        h.Select(tab);
        set = ResultContextTestsAccess.Editable();
        tab.ApplyInlineRun(SecondStatement(), [new InlineResultZone(SecondStatement().End, [set], null)]);
        var ran = false;

        await h.Coordinator.RunAsync(SecondStatement(), async (_, confirm) => ran = await confirm());

        Assert.Equal("1 unsaved change", Assert.Single(asked));
        Assert.False(ran);
        Assert.True(set.HasPendingChanges);
    });

    /// <summary>The placement switched from anywhere (menu, palette, Settings): to Pane renders the pane and
    /// opens it on the tab's results; back to Inline collapses it.</summary>
    [Fact]
    public Task Switching_the_placement_keeps_the_pane_in_step() => _ui.Run(() =>
    {
        var h = Build();
        var tab = new EditorTabViewModel("t", Buffer);
        tab.SetRunResults([InlineResultsTests.OneSet()]);
        h.Select(tab);
        Assert.False(h.PaneVisible);

        h.Settings.Update(s => s with { ResultsPlacement = ResultsPlacement.Pane });
        Assert.True(h.PaneVisible);

        h.Settings.Update(s => s with { ResultsPlacement = ResultsPlacement.Inline });
        Assert.False(h.PaneVisible);

        h.PaneVisible = true;   // the user opens it in inline mode; an unrelated setting leaves it alone
        h.Settings.Update(s => s with { InspectorFontSize = 15 });
        Assert.True(h.PaneVisible);
        return Task.CompletedTask;
    });

    /// <summary>A zone edited while its statement re-runs: those edits were never asked about, so the zone stays
    /// (edits and all) and the run's results go to the pane, which opens — the unplaceable-run path.</summary>
    [Fact]
    public Task Edits_made_in_a_zone_while_its_query_runs_keep_the_zone() => _ui.Run(async () =>
    {
        var h = Build(Asking([], answer: true));
        var tab = new EditorTabViewModel("t", Buffer);
        h.Select(tab);
        var set = ResultContextTestsAccess.Editable();
        tab.ApplyInlineRun(SecondStatement(), [new InlineResultZone(SecondStatement().End, [set], null)]);
        var zone = Assert.Single(tab.InlineZones);

        await h.Coordinator.RunAsync(SecondStatement(), async (_, confirm) =>
        {
            Assert.True(await confirm());                    // nothing pending yet: nothing asked
            set.SetCell(set.Rows[0], 1, "typed meanwhile");  // the user edits while it runs
            tab.SetRunResults([InlineResultsTests.OneSet()]);
        });

        Assert.Same(zone, Assert.Single(tab.InlineZones));
        Assert.True(set.HasPendingChanges);
        Assert.True(h.PaneVisible);
        Assert.Contains(h.Statuses, s => s.Contains("made while the query ran kept"));
    });

    /// <summary>Pane mode: the pane's frame edited while a run is in flight comes back on top when the results
    /// land, with the run's results one Back below it.</summary>
    [Fact]
    public Task Edits_made_in_the_pane_while_a_query_runs_are_put_back_on_top() => _ui.Run(async () =>
    {
        var h = Build(Asking([], answer: true), ResultsPlacement.Pane);
        var tab = new EditorTabViewModel("t", Buffer);
        var set = ResultContextTestsAccess.Editable();
        tab.SetRunResults([set]);
        h.Select(tab);
        var before = tab.Results;
        var fresh = InlineResultsTests.OneSet();

        await h.Coordinator.RunAsync(SecondStatement(), async (_, confirm) =>
        {
            Assert.True(await confirm());
            set.SetCell(set.Rows[0], 1, "typed meanwhile");
            tab.SetRunResults([fresh]);
        });

        Assert.Same(before, tab.Results);
        Assert.True(set.HasPendingChanges);
        Assert.True(tab.CanGoBack);
        Assert.Contains(h.Statuses, line => line.Contains("one Back below in the pane"));
        tab.GoBack();
        Assert.Same(fresh, Assert.Single(tab.Results));
    });

    /// <summary>A set given up at the question and then edited again before the results landed: the later edit
    /// was never agreed to, so the zone is kept and nothing in it reverted.</summary>
    [Fact]
    public Task A_given_up_set_edited_again_during_the_run_is_kept() => _ui.Run(async () =>
    {
        var asked = new System.Collections.Generic.List<string>();
        var h = Build(Asking(asked, answer: true));
        var tab = new EditorTabViewModel("t", Buffer);
        h.Select(tab);
        var set = DirtyZoneOnSecond(tab);
        var zone = Assert.Single(tab.InlineZones);

        await h.Coordinator.RunAsync(SecondStatement(), async (_, confirm) =>
        {
            Assert.True(await confirm());                        // Discard and run
            set.SetCell(set.Rows[1], 1, "typed after the question");
            tab.SetRunResults([InlineResultsTests.OneSet()]);
        });

        Assert.Single(asked);
        Assert.Same(zone, Assert.Single(tab.InlineZones));
        Assert.True(set.HasPendingChanges);
        Assert.Equal(2, set.PendingCount);
    });

    /// <summary>F0 → F1, run, Back to F0 while it runs, edit F0, results land: F0 was never asked about and
    /// isn't the frame the question saw, yet its edit is kept — back on top, the results one Back below.</summary>
    [Fact]
    public Task An_edit_in_a_frame_Back_reached_during_the_run_is_kept() => _ui.Run(async () =>
    {
        var h = Build(Asking([], answer: true), ResultsPlacement.Pane);
        var tab = new EditorTabViewModel("t", Buffer);
        var f0 = ResultContextTestsAccess.Editable();
        tab.SetRunResults([f0]);
        var f0Frame = tab.Results;
        tab.PushResults([InlineResultsTests.OneSet()]);   // F1
        h.Select(tab);
        var fresh = InlineResultsTests.OneSet();

        await h.Coordinator.RunAsync(SecondStatement(), async (_, confirm) =>
        {
            Assert.True(await confirm());                  // nothing pending: nothing asked
            tab.GoBack();                                   // back to F0 while it runs
            f0.SetCell(f0.Rows[0], 1, "typed meanwhile");
            tab.SetRunResults([fresh]);
        });

        Assert.Same(f0Frame, tab.Results);
        Assert.True(f0.HasPendingChanges);
        tab.GoBack();
        Assert.Same(fresh, Assert.Single(tab.Results));
        Assert.Contains(h.Statuses, line => line.Contains("one Back below in the pane"));
    });

    /// <summary>A second Run on a tab whose first run gave up edits (refused: the tab is busy) leaves the first
    /// run's hold on Back in place — only the call that set it clears it.</summary>
    [Fact]
    public Task A_second_run_on_a_busy_tab_keeps_the_first_runs_hold() => _ui.Run(async () =>
    {
        var h = Build(Asking([], answer: true));
        var tab = new EditorTabViewModel("t", Buffer);
        h.Select(tab);
        DirtyZoneOnSecond(tab);
        var release = new TaskCompletionSource();
        var first = h.Coordinator.RunAsync(SecondStatement(), async (_, confirm) =>
        {
            Assert.True(await confirm());   // gives the edits up: Back now waits
            tab.BeginRun();
            await release.Task;
            tab.EndRun();
        });
        Assert.NotNull(tab.BusyReason("going back"));

        await h.Coordinator.RunAsync(SecondStatement(), (_, _) => Task.CompletedTask);   // refused: busy

        Assert.NotNull(tab.BusyReason("going back"));
        release.SetResult();
        await first;
        Assert.Null(tab.BusyReason("going back"));
    });
}
