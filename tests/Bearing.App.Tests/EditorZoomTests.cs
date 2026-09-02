using Bearing.App.Editing;
using Bearing.App.Input;
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

    // ---- Ctrl+wheel (#51): a mouse notch is one point, a trackpad swipe is not fifty ----

    [Fact]
    public void A_mouse_notch_spends_immediately()
    {
        var wheel = new WheelNotches();
        Assert.Equal(1, wheel.Add(1));
        Assert.Equal(-1, wheel.Add(-1));
        Assert.Equal(0, wheel.Add(0));
    }

    [Fact]
    public void Trackpad_fractions_bank_until_they_make_a_whole_step()
    {
        // Ten 0.2 deltas — one swipe — must be two points, not ten.
        var wheel = new WheelNotches();
        var steps = 0;
        for (var i = 0; i < 10; i++) steps += wheel.Add(0.2);
        Assert.Equal(2, steps);
    }

    [Fact]
    public void A_fast_gesture_releases_every_notch_it_carried()
    {
        var wheel = new WheelNotches();
        Assert.Equal(3, wheel.Add(3.4));   // and 0.4 stays banked
        Assert.Equal(1, wheel.Add(0.7));
    }

    [Fact]
    public void Reversing_direction_drops_what_was_banked_the_other_way()
    {
        // Otherwise a flick back has to pay off the previous swipe's remainder before anything moves.
        var wheel = new WheelNotches();
        Assert.Equal(0, wheel.Add(0.9));
        Assert.Equal(-1, wheel.Add(-1));
    }

    [Fact]
    public void A_multi_notch_gesture_at_the_ceiling_still_travels_the_last_point()
    {
        // The controller spends a burst one step at a time for this reason: EditorZoom.Nudge refuses a move
        // that would leave the range, so asking for +3 from one below the ceiling would do nothing at all.
        var steps = (int)(EditorZoom.MaxSize - 14) - 1;                 // one point short of the ceiling
        Assert.Equal(steps, EditorZoom.Nudge(14, steps, +3));           // the whole burst is refused…
        for (var i = 0; i < 3; i++) steps = EditorZoom.Nudge(14, steps, +1);
        Assert.Equal(EditorZoom.MaxSize, EditorZoom.SizeFor(14, steps)); // …but step by step it arrives
    }
}
