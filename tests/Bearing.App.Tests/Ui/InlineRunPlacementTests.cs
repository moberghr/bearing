using System.Threading.Tasks;
using Avalonia.Controls;
using AvaloniaEdit;
using Bearing.App.Controls;
using Bearing.App.Editing;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Placing a finished run's results (<see cref="InlineResultsController.PlaceRun"/>): the buffer stays editable
/// while a query runs, so the offsets captured at the start have to be carried through the edits made since —
/// and when they can't be (the buffer was swapped), the answer is the pane, not a guess. Over a bare editor:
/// what is under test is the controller and AvaloniaEdit's version history, not the window's wiring.
/// </summary>
[Collection(UiTestCollection.Name)]
public class InlineRunPlacementTests
{
    private readonly UiTestSession _ui;

    public InlineRunPlacementTests(UiTestSession ui) => _ui = ui;

    private const string Buffer = "select 1;\nselect 2;\n";

    private static (TextEditor Editor, InlineResultsController Controller, EditorTabViewModel Tab) Build()
    {
        var editor = new TextEditor { Text = Buffer };
        var window = new Window { Content = editor, Width = 600, Height = 400 };
        window.Show();
        var controller = new InlineResultsController(editor, () => true,
            (_, zone) => new InlineZoneView(zone, new ResultView { IsInline = true }, _ => false, () => { }));
        var tab = new EditorTabViewModel("t", Buffer);
        controller.Bind(tab);
        return (editor, controller, tab);
    }

    private static RunScope SecondStatement()
        => InlineResultPlacement.Scope(PostgresDialect.Instance, "select 2;", "select 1;\n".Length, isWholeBuffer: false);

    [Fact]
    public Task Edits_made_during_the_run_move_the_results_with_their_statement() => _ui.Run(() =>
    {
        var (editor, controller, tab) = Build();
        var scope = SecondStatement();
        var start = controller.BeginRun(tab);

        editor.Document.Insert(0, "-- typed while it ran\n");
        tab.Text = editor.Text;   // what the window's buffer sync does on every keystroke
        tab.SetRunResults([InlineResultsTests.OneSet()]);

        Assert.Equal(InlineResultsController.Placement.Inline, controller.PlaceRun(scope, start));
        var zone = Assert.Single(tab.InlineZones);
        Assert.Equal(editor.Text.IndexOf("select 2;") + "select 2;".Length, zone.Offset);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_run_that_spanned_a_buffer_swap_goes_to_the_pane() => _ui.Run(() =>
    {
        var (editor, controller, tab) = Build();
        var start = controller.BeginRun(tab);

        controller.Unbind();
        editor.Text = "select 'other tab';";
        editor.Text = Buffer + "-- edited\n";
        tab.Text = editor.Text;
        controller.Bind(tab);
        tab.SetRunResults([InlineResultsTests.OneSet()]);

        Assert.Equal(InlineResultsController.Placement.Untraceable, controller.PlaceRun(SecondStatement(), start));
        Assert.Empty(tab.InlineZones);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_run_with_no_new_results_places_nothing() => _ui.Run(() =>
    {
        var (_, controller, tab) = Build();
        tab.SetFreshResults([InlineResultsTests.OneSet()]);
        var start = controller.BeginRun(tab);

        Assert.Equal(InlineResultsController.Placement.Nothing, controller.PlaceRun(SecondStatement(), start));
        Assert.Empty(tab.InlineZones);
        return Task.CompletedTask;
    });

    /// <summary>FK navigation or ⤢ during the write prompt swaps the tab's results; a declined write then
    /// sets nothing. Those swapped-in results are not this run's and must not land under its statement.</summary>
    [Fact]
    public Task Results_swapped_in_by_something_else_during_the_run_are_not_placed() => _ui.Run(() =>
    {
        var (_, controller, tab) = Build();
        tab.SetRunResults([InlineResultsTests.OneSet()]);
        var start = controller.BeginRun(tab);

        tab.PushResults([InlineResultsTests.OneSet()]);      // FK lookup while the row count runs
        tab.SetFreshResults([InlineResultsTests.OneSet()]);  // ⤢ on another zone

        Assert.Equal(InlineResultsController.Placement.Nothing, controller.PlaceRun(SecondStatement(), start));
        Assert.Empty(tab.InlineZones);
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_runs_own_results_are_placed_even_after_a_later_swap() => _ui.Run(() =>
    {
        var (_, controller, tab) = Build();
        var start = controller.BeginRun(tab);
        var own = InlineResultsTests.OneSet();
        tab.SetRunResults([own]);
        tab.PushResults([InlineResultsTests.OneSet()]);

        Assert.Equal(InlineResultsController.Placement.Inline, controller.PlaceRun(SecondStatement(), start));
        Assert.Same(own, Assert.Single(Assert.Single(tab.InlineZones).Sets));
        return Task.CompletedTask;
    });
}
