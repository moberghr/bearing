using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Bearing.App.Controls;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Inline results in the real editor (docs/specs/2026-11-06-inline-results.md). These assert positions and
/// what is realized — that a zone occupies its own row under its statement, follows edits, survives a tab
/// switch and leaves with its text. Whether it <i>looks</i> right is eyeball QA (§4.3).
/// </summary>
[Collection(UiTestCollection.Name)]
public class InlineResultsTests
{
    private readonly UiTestSession _ui;

    public InlineResultsTests(UiTestSession ui) => _ui = ui;

    private const string Buffer = "select 1;\nselect 2;\n";

    [Fact]
    public Task A_zone_gets_its_own_row_under_its_statement_and_pushes_the_next_line_down() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_zone_gets_its_own_row_under_its_statement_and_pushes_the_next_line_down));
        var (editor, tab) = Setup(shell);
        var lineHeight = editor.TextArea.TextView.DefaultLineHeight;
        var before = editor.TextArea.TextView.GetVisualTopByDocumentLine(2);

        PlaceUnderFirstStatement(shell, tab);

        var view = Assert.Single(Zones(editor));
        Assert.True(view.Bounds.Height > lineHeight, $"zone is {view.Bounds.Height}px tall");
        var zoneTop = view.TranslatePoint(new Point(0, 0), editor.TextArea.TextView)!.Value.Y;
        // Below the text of line 1, not beside it…
        Assert.True(zoneTop >= lineHeight - 1, $"zone top {zoneTop} is inside line 1 (height {lineHeight})");
        // …and line 2 moved down by (at least) the zone's height.
        var after = editor.TextArea.TextView.GetVisualTopByDocumentLine(2);
        Assert.True(after - before >= view.Bounds.Height, $"line 2 moved {after - before}px for a {view.Bounds.Height}px zone");
    });

    [Fact]
    public Task Close_removes_the_zone_and_gives_the_row_back() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Close_removes_the_zone_and_gives_the_row_back));
        var (editor, tab) = Setup(shell);
        var before = editor.TextArea.TextView.GetVisualTopByDocumentLine(2);
        PlaceUnderFirstStatement(shell, tab);

        Assert.Single(Zones(editor)).CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        shell.Pump();

        Assert.Empty(tab.InlineZones);
        Assert.Empty(Zones(editor));
        Assert.Equal(before, editor.TextArea.TextView.GetVisualTopByDocumentLine(2));
    });

    [Fact]
    public Task In_pane_mode_zones_are_kept_but_not_shown() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(In_pane_mode_zones_are_kept_but_not_shown));
        var (editor, tab) = Setup(shell);
        var before = editor.TextArea.TextView.GetVisualTopByDocumentLine(2);
        PlaceUnderFirstStatement(shell, tab);

        shell.Vm.SettingsService.Update(s => s with { ResultsPlacement = ResultsPlacement.Pane });
        shell.Pump();

        Assert.Single(tab.InlineZones);
        Assert.Equal(before, editor.TextArea.TextView.GetVisualTopByDocumentLine(2));

        shell.Vm.SettingsService.Update(s => s with { ResultsPlacement = ResultsPlacement.Inline });
        shell.Pump();
        Assert.True(editor.TextArea.TextView.GetVisualTopByDocumentLine(2) > before);
    });

    [Fact]
    public Task Typing_above_moves_the_zone_with_its_statement() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Typing_above_moves_the_zone_with_its_statement));
        var (editor, tab) = Setup(shell);
        PlaceUnderFirstStatement(shell, tab);
        var offset = tab.InlineZones[0].Offset;

        editor.Document.Insert(0, "-- note\n");
        shell.Pump();

        Assert.Equal(offset + 8, tab.InlineZones[0].Offset);
        var view = Assert.Single(Zones(editor));
        var zoneTop = view.TranslatePoint(new Point(0, 0), editor.TextArea.TextView)!.Value.Y;
        var lineHeight = editor.TextArea.TextView.DefaultLineHeight;
        Assert.True(zoneTop >= 2 * lineHeight - 1, $"zone top {zoneTop} did not move below the inserted line");
    });

    [Fact]
    public Task Deleting_the_statement_takes_its_zone_with_it() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Deleting_the_statement_takes_its_zone_with_it));
        var (editor, tab) = Setup(shell);
        PlaceUnderFirstStatement(shell, tab);

        editor.Document.Remove(0, "select 1;\n".Length);
        shell.Pump();

        Assert.Empty(tab.InlineZones);
        Assert.Empty(Zones(editor));
    });

    [Fact]
    public Task A_tab_switch_keeps_the_zone_where_it_was() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_tab_switch_keeps_the_zone_where_it_was));
        var (editor, tab) = Setup(shell);
        PlaceUnderFirstStatement(shell, tab);
        var offset = tab.InlineZones[0].Offset;

        var other = shell.Vm.Workspace.NewTab("select 42;");
        shell.Pump();
        Assert.Same(other, shell.Vm.Workspace.SelectedTab);
        Assert.Empty(Zones(editor));

        shell.Vm.Workspace.SelectedTab = tab;
        shell.Pump();

        var zone = Assert.Single(tab.InlineZones);
        Assert.Equal(offset, zone.Offset);
        Assert.Single(Zones(editor));
    });

    [Fact]
    public Task Open_in_pane_shows_the_zones_sets_in_the_results_pane() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Open_in_pane_shows_the_zones_sets_in_the_results_pane));
        var (editor, tab) = Setup(shell);
        PlaceUnderFirstStatement(shell, tab);
        var results = shell.Window.GetVisualDescendants().OfType<ResultView>().Single();
        Assert.False(results.IsVisible);

        Assert.Single(Zones(editor)).OpenInPaneButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        shell.Pump();

        Assert.Same(tab.InlineZones[0].Sets, tab.Results);
        Assert.True(results.IsVisible);
    });

    /// <summary>Nothing in a zone may take the keyboard: focus inside the text view would send typing and
    /// the editor's bindings to a control the user isn't looking at.</summary>
    [Fact]
    public Task Nothing_in_a_zone_can_take_focus() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Nothing_in_a_zone_can_take_focus));
        var (editor, tab) = Setup(shell);
        PlaceUnderFirstStatement(shell, tab);

        var view = Assert.Single(Zones(editor));
        Assert.All(view.GetVisualDescendants().OfType<InputElement>().Append(view), e => Assert.False(e.Focusable, e.GetType().Name));
        Assert.True(editor.TextArea.IsFocused);
    });

    /// <summary>The zone's row has no caret stops: Down from the statement's line lands on the next text
    /// line, and Up comes back.</summary>
    [Fact]
    public Task The_caret_steps_over_the_zone_row() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_caret_steps_over_the_zone_row));
        var (editor, tab) = Setup(shell);
        editor.CaretOffset = 3;
        shell.Pump();
        Press(shell, Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal(2, editor.TextArea.Caret.Line);   // control: Down works without a zone
        PlaceUnderFirstStatement(shell, tab);
        editor.CaretOffset = 3;
        shell.Pump();

        Press(shell, Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal(2, editor.TextArea.Caret.Line);

        Assert.Equal(4, editor.TextArea.Caret.Column);   // kept its column

        Press(shell, Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal(1, editor.TextArea.Caret.Line);
        Assert.Equal(4, editor.TextArea.Caret.Column);

        // End stays on the text row of the statement's line.
        Press(shell, Key.End, PhysicalKey.End);
        Assert.Equal(1, editor.TextArea.Caret.Line);
        Assert.Equal("select 1;".Length, editor.CaretOffset);
    });

    /// <summary>Arriving from above at a statement line shorter than the caret's x lands at its end — a real
    /// text position, not the zone row — so the caret stays there instead of skipping the line.</summary>
    [Fact]
    public Task Down_from_above_onto_a_short_statement_line_stops_on_it() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Down_from_above_onto_a_short_statement_line_stops_on_it));
        var (editor, tab) = Setup(shell, "select 1234567890;\nselect 2;\nselect 3;\n");
        var start = "select 1234567890;\n".Length;
        var scope = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 2;", start, isWholeBuffer: false);
        tab.ApplyInlineRun(scope, InlineResultPlacement.Assign(scope, [OneSet()]));
        shell.Pump();
        editor.CaretOffset = 14;
        shell.Pump();

        Press(shell, Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal(2, editor.TextArea.Caret.Line);

        Press(shell, Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal(3, editor.TextArea.Caret.Line);
    });

    /// <summary>Another script loaded into the same tab (File ▸ Open, a reload from disk): the old results
    /// describe text that is gone, so they go with it.</summary>
    [Fact]
    public Task Different_text_in_the_same_tab_drops_its_zones() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Different_text_in_the_same_tab_drops_its_zones));
        var (editor, tab) = Setup(shell);
        PlaceUnderFirstStatement(shell, tab);

        var other = System.IO.Path.Combine(shell.Vm.ScriptsDirectory!, "other.sql");
        await System.IO.File.WriteAllTextAsync(other, "select 'another script';\n");
        // File ▸ Open's two steps: load into the selected tab, then show it.
        await shell.Vm.Workspace.LoadScriptIntoSelectedAsync(other);
        shell.Vm.Workspace.SelectedTab = shell.Vm.Workspace.NewTab();
        shell.Vm.Workspace.SelectedTab = tab;
        shell.Pump();

        Assert.Equal(tab.Text, editor.Text);
        Assert.Empty(tab.InlineZones);
        Assert.Empty(Zones(editor));
    });

    private static void Press(ShellHarness shell, Key key, PhysicalKey physical)
    {
        shell.Window.KeyPress(key, RawInputModifiers.None, physical, null);
        shell.Window.KeyRelease(key, RawInputModifiers.None, physical, null);
        shell.Pump();
    }

    /// <summary>⤢ while the tab runs is refused: the run's results replace the tab's when it finishes, and a
    /// swap in between would be taken for them.</summary>
    [Fact]
    public Task Open_in_pane_is_refused_while_the_tab_runs() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Open_in_pane_is_refused_while_the_tab_runs));
        var (editor, tab) = Setup(shell);
        PlaceUnderFirstStatement(shell, tab);
        var results = shell.Window.GetVisualDescendants().OfType<ResultView>().Single();
        var before = tab.Results;
        tab.BeginRun();

        Assert.Single(Zones(editor)).OpenInPaneButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        shell.Pump();

        Assert.Same(before, tab.Results);
        Assert.False(results.IsVisible);
        Assert.Contains("Wait for the running query", shell.Vm.StatusText);
        tab.EndRun();
    });

    /// <summary>The zone's placement toggle while the tab runs: refused like ⤢, and the setting stays put —
    /// flipping it anyway would hide every zone with the pane still closed.</summary>
    [Fact]
    public Task The_zones_toggle_while_the_tab_runs_changes_nothing() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_zones_toggle_while_the_tab_runs_changes_nothing));
        var (editor, tab) = Setup(shell);
        PlaceUnderFirstStatement(shell, tab);
        var results = shell.Window.GetVisualDescendants().OfType<ResultView>().Single();
        tab.BeginRun();

        Assert.Single(Zones(editor)).PlacementButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        shell.Pump();

        Assert.Equal(ResultsPlacement.Inline, shell.Vm.SettingsService.Current.ResultsPlacement);
        Assert.False(results.IsVisible);
        Assert.Single(Zones(editor));
        Assert.Contains("Wait for the running query", shell.Vm.StatusText);
        tab.EndRun();
    });

    /// <summary>A press on a zone never reaches the text area: the caret stays and no selection starts.</summary>
    [Fact]
    public Task Clicking_a_zone_leaves_the_caret_alone() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Clicking_a_zone_leaves_the_caret_alone));
        var (editor, tab) = Setup(shell);
        PlaceUnderFirstStatement(shell, tab);
        editor.CaretOffset = 3;
        shell.Pump();
        var view = Assert.Single(Zones(editor));
        var point = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height - 4), shell.Window)!.Value;

        shell.Window.MouseDown(point, MouseButton.Left);
        shell.Window.MouseUp(point, MouseButton.Left);
        shell.Pump();

        Assert.Equal(3, editor.CaretOffset);
        Assert.True(editor.TextArea.Selection.IsEmpty);
    });

    /// <summary>A zone's placement toggle: back to the pane, with that zone's results in it.</summary>
    [Fact]
    public Task The_zones_toggle_goes_back_to_the_pane_with_its_results() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_zones_toggle_goes_back_to_the_pane_with_its_results));
        var (editor, tab) = Setup(shell);
        PlaceUnderFirstStatement(shell, tab);
        var results = shell.Window.GetVisualDescendants().OfType<ResultView>().Single();

        Assert.Single(Zones(editor)).PlacementButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        shell.Pump();

        Assert.Equal(ResultsPlacement.Pane, shell.Vm.SettingsService.Current.ResultsPlacement);
        Assert.Same(tab.InlineZones[0].Sets, tab.Results);
        Assert.True(results.IsVisible);
        Assert.Empty(Zones(editor));
    });

    /// <summary>Dragging the bottom grip resizes the table, the lines below follow, and the drag survives the
    /// editor re-measuring the line mid-drag (the pointer stays captured).</summary>
    [Fact]
    public Task Dragging_the_grip_resizes_the_table_and_the_text_below_follows() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(Dragging_the_grip_resizes_the_table_and_the_text_below_follows));
        var (editor, tab) = Setup(shell);
        var scope = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 1;", 0, isWholeBuffer: false);
        tab.ApplyInlineRun(scope, InlineResultPlacement.Assign(scope, [ManyRows(40)]));
        shell.Pump();
        var view = Assert.Single(Zones(editor));
        var lineTwo = editor.TextArea.TextView.GetVisualTopByDocumentLine(2);
        var grip = view.ResizeGrip;
        var start = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), shell.Window)!.Value;

        shell.Window.MouseDown(start, MouseButton.Left);
        for (var dy = 20; dy <= 80; dy += 20)
        {
            shell.Window.MouseMove(start + new Point(0, dy), RawInputModifiers.LeftMouseButton);
            shell.Pump();
        }
        shell.Window.MouseUp(start + new Point(0, 80), MouseButton.Left);
        shell.Pump();

        var row = ResultGridChrome.RowMinHeight;
        Assert.Equal(row * InlineResultView.VisibleRows + 80, tab.InlineZones[0].TableHeight!.Value, 1);
        Assert.Equal(lineTwo + 80, editor.TextArea.TextView.GetVisualTopByDocumentLine(2), 1);
        Assert.Equal(0, editor.CaretOffset);   // the drag never reached the text
    });

    internal static ResultSetViewModel ManyRows(int count) => new(
        new QueryResult([new ColumnDescriptor("n", "int4", typeof(int))],
            Enumerable.Range(0, count).Select(i => new object?[] { i }).ToList(), count, TimeSpan.Zero, null, null, false),
        "select n;", pageable: false);

    // ---- helpers ---------------------------------------------------------------------------

    private static (TextEditor Editor, EditorTabViewModel Tab) Setup(ShellHarness shell, string buffer = Buffer)
    {
        shell.Vm.SettingsService.Update(s => s with { ResultsPlacement = ResultsPlacement.Inline });
        var editor = shell.Window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "Editor");
        editor.TextArea.Focus();
        editor.Text = buffer;
        shell.Pump();
        var tab = shell.Vm.Workspace.SelectedTab!;
        Assert.Equal(buffer, tab.Text);
        return (editor, tab);
    }

    private static void PlaceUnderFirstStatement(ShellHarness shell, EditorTabViewModel tab)
    {
        var scope = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 1;", 0, isWholeBuffer: false);
        tab.ApplyInlineRun(scope, InlineResultPlacement.Assign(scope, [OneSet()]));
        shell.Pump();
    }

    internal static ResultSetViewModel OneSet() => new(
        new QueryResult([new ColumnDescriptor("n", "int4", typeof(int))], [[1], [2], [3]], 3,
            TimeSpan.FromMilliseconds(4), null, null, false),
        "select 1;", pageable: false);

    private static InlineResultView[] Zones(TextEditor editor)
        => editor.TextArea.TextView.GetVisualDescendants().OfType<InlineResultView>()
            .Where(v => v.IsEffectivelyVisible).ToArray();
}
