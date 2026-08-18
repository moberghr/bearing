using Bearing.App.Editing;
using Bearing.App.ViewModels;
using Xunit;

namespace Bearing.App.Tests;

public class EditorZoomTests
{
    [Fact]
    public void No_zoom_is_the_configured_base_size()
    {
        Assert.Equal(14, EditorZoom.SizeFor(14, 0));
        Assert.Equal(11, EditorZoom.SizeFor(11, 0));
    }

    [Fact]
    public void Each_step_moves_the_size_by_one_point()
    {
        Assert.Equal(17, EditorZoom.SizeFor(14, 3));
        Assert.Equal(12, EditorZoom.SizeFor(14, -2));
    }

    [Fact]
    public void The_zoom_rides_on_top_of_a_changed_base_size()
    {
        // Settings ▸ Editor ▸ font size moves from 14 to 18 while a tab is two steps up.
        Assert.Equal(16, EditorZoom.SizeFor(14, 2));
        Assert.Equal(20, EditorZoom.SizeFor(18, 2));
    }

    [Fact]
    public void Zooming_stops_at_the_legible_range_without_banking_presses()
    {
        // At the ceiling the step is refused, so one press back out is immediately visible — a clamped
        // size would silently accumulate steps and make zoom-out look broken.
        var steps = 0;
        for (var i = 0; i < 100; i++) steps = EditorZoom.Nudge(14, steps, +1);
        Assert.Equal(EditorZoom.MaxSize, EditorZoom.SizeFor(14, steps));
        Assert.Equal(EditorZoom.MaxSize - 1, EditorZoom.SizeFor(14, EditorZoom.Nudge(14, steps, -1)));

        steps = 0;
        for (var i = 0; i < 100; i++) steps = EditorZoom.Nudge(14, steps, -1);
        Assert.Equal(EditorZoom.MinSize, EditorZoom.SizeFor(14, steps));
        Assert.Equal(EditorZoom.MinSize + 1, EditorZoom.SizeFor(14, EditorZoom.Nudge(14, steps, +1)));
    }

    [Fact]
    public void Zoom_is_per_tab_and_starts_from_the_base_size()
    {
        var a = new EditorTabViewModel("A");
        var b = new EditorTabViewModel("B");

        a.FontZoomSteps = EditorZoom.Nudge(14, a.FontZoomSteps, +2);

        Assert.Equal(16, EditorZoom.SizeFor(14, a.FontZoomSteps));
        Assert.Equal(0, b.FontZoomSteps);                                  // a fresh tab is unzoomed
        Assert.Equal(14, EditorZoom.SizeFor(14, b.FontZoomSteps));
    }

    [Fact]
    public void Reset_returns_the_tab_to_the_base_size()
    {
        var tab = new EditorTabViewModel("A") { FontZoomSteps = 5 };
        tab.FontZoomSteps = 0;
        Assert.Equal(14, EditorZoom.SizeFor(14, tab.FontZoomSteps));
    }
}
