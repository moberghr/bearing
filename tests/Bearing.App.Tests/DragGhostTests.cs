using Avalonia;
using Bearing.App.Controls;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Where the drag box sits relative to the pointer. It exists because Avalonia gives no way to supply a drag
/// cursor — the platform drag source owns the cursor through an internal override — so the "you're dragging
/// this" affordance is drawn in the overlay layer instead. What it looks like is eyeball-QA (§4.3); the
/// placement is pure and pinned here, because the failure it guards against (a label clipped off the window
/// edge) shows up exactly where file names matter most: the rows furthest right.
/// </summary>
public class DragGhostTests
{
    private static readonly Size Ghost = new(120, 24);
    private static readonly Size Layer = new(1000, 800);

    [Fact]
    public void It_sits_below_and_right_of_the_pointer()
    {
        var at = DragGhost.Place(new Point(200, 300), Ghost, Layer);

        Assert.Equal(214, at.X);   // clear of the pointer hotspot
        Assert.Equal(314, at.Y);
    }

    [Fact]
    public void Near_the_right_edge_it_flips_to_the_left_of_the_pointer()
    {
        var at = DragGhost.Place(new Point(960, 300), Ghost, Layer);

        Assert.Equal(960 - 14 - 120, at.X);
        Assert.Equal(314, at.Y);
    }

    [Fact]
    public void Near_the_bottom_edge_it_flips_above_the_pointer()
    {
        var at = DragGhost.Place(new Point(200, 790), Ghost, Layer);

        Assert.Equal(214, at.X);
        Assert.Equal(790 - 14 - 24, at.Y);
    }

    [Fact]
    public void In_the_corner_it_flips_both_ways_at_once()
    {
        var at = DragGhost.Place(new Point(995, 795), Ghost, Layer);

        Assert.Equal(995 - 14 - 120, at.X);
        Assert.Equal(795 - 14 - 24, at.Y);
    }

    [Fact]
    public void A_box_wider_than_the_window_is_pinned_on_screen_rather_than_pushed_off_it()
    {
        // Flipping can't help when there isn't room either side; showing a truncated label beats showing none.
        var at = DragGhost.Place(new Point(10, 10), new Size(400, 24), new Size(200, 100));

        Assert.Equal(0, at.X);
        Assert.Equal(24, at.Y);
    }
}
