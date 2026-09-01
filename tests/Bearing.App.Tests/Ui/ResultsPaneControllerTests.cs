using System.Threading.Tasks;
using Avalonia.Controls;
using Bearing.App.Controls;
using Bearing.App.Views;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Showing and collapsing the results pane — untested until a coverage audit noticed, and the type it holds
/// changed underneath it when the seam became a <see cref="PaneDivider"/>.
/// <para>
/// The interesting behaviour is the memory: collapsing has to remember the split the user dragged to, and
/// remember it <b>once</b>. Capturing it on every collapse would memorise the collapsed sizes on the second
/// one, so the pane would come back at zero height — which the comment in the controller warns about and
/// nothing checked.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class ResultsPaneControllerTests
{
    private readonly UiTestSession _ui;

    public ResultsPaneControllerTests(UiTestSession ui) => _ui = ui;

    /// <summary>The shell's own three-row shape: editor, seam, results.</summary>
    private static (ResultsPaneController Controller, Grid Grid, PaneDivider Seam, ResultView Results) Build()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("2*,Auto,3*") };
        var seam = new PaneDivider();
        var results = new ResultView();
        Grid.SetRow(seam, 1);
        Grid.SetRow(results, 2);
        grid.Children.Add(seam);
        grid.Children.Add(results);
        return (new ResultsPaneController(grid, seam, results), grid, seam, results);
    }

    [Fact]
    public Task Collapsing_hides_the_pane_and_its_seam() => _ui.Run(() =>
    {
        var (controller, grid, seam, results) = Build();

        controller.SetVisible(false);

        Assert.False(controller.IsVisible);
        Assert.False(results.IsVisible);
        // The seam goes too: a draggable divider with nothing below it to resize is a handle onto nothing.
        Assert.False(seam.IsVisible);
        Assert.Equal(0, grid.RowDefinitions[2].Height.Value);
        Assert.Equal(0, grid.RowDefinitions[1].Height.Value);
    });

    [Fact]
    public Task Showing_it_again_restores_the_split() => _ui.Run(() =>
    {
        var (controller, grid, seam, _) = Build();

        controller.SetVisible(false);
        controller.SetVisible(true);

        Assert.True(controller.IsVisible);
        Assert.True(seam.IsVisible);
        Assert.Equal(2, grid.RowDefinitions[0].Height.Value);
        Assert.Equal(3, grid.RowDefinitions[2].Height.Value);
        Assert.True(grid.RowDefinitions[1].Height.IsAuto);
    });

    [Fact]
    public Task It_remembers_where_the_divider_was_dragged_to() => _ui.Run(() =>
    {
        // The point of saving the rows at all: a user who gave the results two thirds of the window gets it
        // back after a collapse, not the 2:3 default.
        var (controller, grid, _, _) = Build();
        grid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        grid.RowDefinitions[2].Height = new GridLength(4, GridUnitType.Star);

        controller.SetVisible(false);
        controller.SetVisible(true);

        Assert.Equal(1, grid.RowDefinitions[0].Height.Value);
        Assert.Equal(4, grid.RowDefinitions[2].Height.Value);
    });

    [Fact]
    public Task Collapsing_twice_does_not_memorise_the_collapsed_size() => _ui.Run(() =>
    {
        // The bug the controller's own comment describes and nothing tested: capture on every collapse and the
        // second one saves zero, so the pane reopens at no height at all.
        var (controller, grid, _, _) = Build();
        grid.RowDefinitions[2].Height = new GridLength(5, GridUnitType.Star);

        controller.SetVisible(false);
        controller.SetVisible(false);
        controller.SetVisible(true);

        Assert.Equal(5, grid.RowDefinitions[2].Height.Value);
        Assert.True(grid.RowDefinitions[2].Height.Value > 0, "the pane reopened with no height");
    });

    [Fact]
    public Task Toggle_does_nothing_at_all_when_there_is_nothing_to_show() => _ui.Run(() =>
    {
        // Ctrl+R on an empty pane would otherwise open an empty box over the editor. The return value is
        // "did I just hide it", not "is it hidden" — so a no-op reports false, and the caller does not go
        // hunting for focus that never moved.
        var (controller, _, _, results) = Build();
        controller.SetVisible(false);

        var justHidden = controller.Toggle();

        Assert.False(justHidden);
        Assert.False(controller.IsVisible);
        Assert.Null(results.Results);
    });

    [Fact]
    public Task Toggle_closes_an_open_pane_and_says_it_did() => _ui.Run(() =>
    {
        // The return value is what lets the caller pull focus back out of the grid it just collapsed.
        var (controller, _, _, results) = Build();
        results.Results = [ResultsHarness.SingleColumn("id", "int4", typeof(int), primaryKey: false, 1)];
        controller.SetVisible(true);

        var justHidden = controller.Toggle();

        Assert.True(justHidden);
        Assert.False(controller.IsVisible);
    });

    [Fact]
    public Task Toggle_reopens_a_pane_that_has_results() => _ui.Run(() =>
    {
        var (controller, _, _, results) = Build();
        results.Results = [ResultsHarness.SingleColumn("id", "int4", typeof(int), primaryKey: false, 1)];
        controller.SetVisible(false);

        var justHidden = controller.Toggle();

        Assert.False(justHidden);
        Assert.True(controller.IsVisible);
    });
}
