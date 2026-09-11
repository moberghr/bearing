using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Bearing.App.Controls;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The activity panel as a realized control in the running shell (#101).
/// <para>
/// The claims here are the ones no pure test can hold: that the panel is actually in the visual tree and
/// shown for its own rail position, and that its polling is tied to being on screen through the real
/// <c>ShellViewModel</c> rather than through a property a test set by hand. §0.2 — none of this says it looks
/// right; the two-line rows and the splitter need eyeball QA.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class ActivityPanelUiTests
{
    private readonly UiTestSession _ui;

    public ActivityPanelUiTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task Showing_the_panel_reveals_it_and_starts_it_polling() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync("activity-panel");
        var panel = Panel(shell);

        // Another panel is showing to begin with, so this one is present but hidden and idle.
        Assert.False(panel.IsVisible);
        Assert.False(shell.Vm.Activity.IsPolling);

        shell.Vm.ShowPanel(SidePanel.Activity);
        shell.Pump();

        Assert.True(panel.IsVisible);
        Assert.True(shell.Vm.Activity.IsPolling);

        // And its list is realized, which is what F6 has to be able to reach.
        Assert.NotNull(panel.FindControl<ListBox>("BackendList"));
    });

    [Fact]
    public Task Leaving_the_panel_stops_it_polling() => _ui.Run(async () =>
    {
        // The only thing in the app that queries a server on a timer, so it must not keep doing it for a
        // panel nobody is looking at.
        using var shell = await ShellHarness.ShowAsync("activity-stop");

        shell.Vm.ShowPanel(SidePanel.Activity);
        shell.Pump();
        Assert.True(shell.Vm.Activity.IsPolling);

        shell.Vm.ShowPanel(SidePanel.History);
        shell.Pump();
        Assert.False(shell.Vm.Activity.IsPolling);
    });

    [Fact]
    public Task Collapsing_the_pane_from_the_activity_tile_and_reopening_it_resumes_polling() => _ui.Run(async () =>
    {
        // The trap this panel is built around. [ObservableProperty]'s setter short-circuits on an unchanged
        // value, so toggling the pane from the Activity tile never changes ActivePanel — and polling hung off
        // OnActivePanelChanged alone would stop on the collapse and never start again, leaving the panel
        // visible and frozen.
        using var shell = await ShellHarness.ShowAsync("activity-toggle");

        shell.Vm.ActivateOrTogglePanel(SidePanel.Activity);
        shell.Pump();
        Assert.True(shell.Vm.SidePaneOpen);
        Assert.True(shell.Vm.Activity.IsPolling);

        shell.Vm.ActivateOrTogglePanel(SidePanel.Activity);   // same tile again: collapses the pane
        shell.Pump();
        Assert.False(shell.Vm.SidePaneOpen);
        Assert.Equal(SidePanel.Activity, shell.Vm.ActivePanel);   // …and ActivePanel did not change
        Assert.False(shell.Vm.Activity.IsPolling);

        shell.Vm.ActivateOrTogglePanel(SidePanel.Activity);
        shell.Pump();
        Assert.True(shell.Vm.SidePaneOpen);
        Assert.True(shell.Vm.Activity.IsPolling);
    });

    [Fact]
    public Task The_rail_carries_a_tile_for_it() => _ui.Run(async () =>
    {
        // The rail's click handler is generic over the enum, so the tile is the whole registration — and a
        // panel with no tile is only reachable from the palette.
        using var shell = await ShellHarness.ShowAsync("activity-rail");

        var tile = shell.Window.GetVisualDescendants().OfType<RadioButton>()
            .FirstOrDefault(r => r.Tag as string == nameof(SidePanel.Activity));

        Assert.NotNull(tile);
    });

    [Fact]
    public Task A_poll_that_finds_the_same_statement_leaves_the_sql_pane_alone() => _ui.Run(async () =>
    {
        // The panel rebuilds every row on each read, so `Selected` is a *new* object every 2.5 seconds even
        // when nothing about the backend changed — and re-assigning the viewer's Text resets its caret, its
        // selection and its scroll. A user reading a long statement, or part-way through selecting a clause
        // out of it to paste, lost it twice every three seconds.
        using var shell = await ShellHarness.ShowAsync("activity-sql-pane");
        shell.Vm.ShowPanel(SidePanel.Activity);
        // The timer off: this test drives the rows itself, and a real tick on a shell with no live session
        // empties the list and clears the selection — which is correct behaviour and would be the only thing
        // this ever measured once the test ran long enough to reach one.
        shell.Vm.Activity.SetPolling(false);
        shell.Pump();

        var panel = Panel(shell);
        var editor = panel.FindControl<TextEditor>("BackendSql");
        Assert.NotNull(editor);

        var vm = shell.Vm.Activity;
        const string sql = "select store_id, sum(amount) from payment group by store_id";
        var row = new BackendRowViewModel(Backend(sql, seconds: 12));
        vm.Backends.Add(row);
        vm.Selected = row;

        // Settled first: the pane puts the raw statement up immediately and the formatter — which parses,
        // off the UI thread — writes over it a frame or two later. That second write is the panel doing its
        // job once, and this test is about the writes that come after it.
        await SettleAsync(shell, editor!);
        Assert.Contains("store_id", editor!.Text);

        // Counted rather than inferred from the caret alone: a write is the thing being claimed not to
        // happen, and the caret is only one of the several pieces of state a write costs.
        var writes = 0;
        editor.Document.Changed += (_, _) => writes++;
        // Where the user has got to: a caret in the middle of the statement, which is the state a
        // re-assignment silently throws away, along with the selection and the scroll offset.
        editor.CaretOffset = 9;

        // One poll: the same session two and a half seconds on, which is what Apply hands the row it kept.
        // The header has to move — the statement has been running longer — and the pane must not.
        row.Update(Backend(sql, seconds: 15));
        await SettleAsync(shell, editor);

        Assert.Equal(0, writes);
        Assert.Equal(9, editor.CaretOffset);
        Assert.Contains("15 s", row.Header);

        // And the pane is not frozen: a backend that has moved on to another statement still refreshes.
        row.Update(Backend("vacuum analyze payment", seconds: 1));
        await SettleAsync(shell, editor);

        Assert.Contains("vacuum", editor.Text, StringComparison.OrdinalIgnoreCase);
    });

    /// <summary>
    /// Pump until the viewer's text stops changing. The SQL pane's second write is asynchronous (the
    /// formatter parses), so a single pump catches it half done and the caret assertion above would be
    /// measuring that rather than the poll.
    /// </summary>
    private static async Task SettleAsync(ShellHarness shell, TextEditor editor)
    {
        var last = editor.Text;
        var still = 0;
        // Eight quiet rounds, not one: the formatter's write lands a good deal later than the raw one, and a
        // settle that stopped at the first pair of equal reads was measuring the gap between them.
        for (var attempt = 0; attempt < 120 && still < 8; attempt++)
        {
            await Task.Delay(20);
            shell.Pump();
            still = editor.Text == last ? still + 1 : 0;
            last = editor.Text;
        }
    }

    /// <summary>One backend, fixed identity, with whatever statement and elapsed time the test wants.</summary>
    private static BackendActivity Backend(string query, double seconds) => new(
        Pid: 4242,
        BackendStart: new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
        User: "app",
        Database: "app",
        Application: "psql",
        State: "active",
        WaitEvent: null,
        RunningFor: TimeSpan.FromSeconds(seconds),
        StateFor: TimeSpan.FromSeconds(seconds),
        Query: query,
        IsOurs: false);

    private static ActivityPanelView Panel(ShellHarness shell)
        => shell.Window.GetVisualDescendants().OfType<ActivityPanelView>().Single();
}
