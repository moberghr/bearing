using Bearing.App.Controls;
using Xunit;

namespace Bearing.App.Tests;

public class SafeTriangleTests
{
    // Apex at the owner item (10,50); submenu's near edge is x=100, spanning y 0..200.
    private static bool In(double x, double y) => SafeTriangle.Contains(10, 50, 100, 0, 100, 200, x, y);

    [Fact]
    public void A_diagonal_path_to_the_submenu_is_inside() => Assert.True(In(60, 100));

    [Fact]
    public void The_far_corner_of_the_submenu_edge_is_inside() => Assert.True(In(99, 195));

    [Fact]
    public void Straight_back_past_the_apex_is_outside() => Assert.False(In(0, 50));

    [Fact]
    public void Above_the_upper_slope_is_outside() => Assert.False(In(60, 5));

    [Fact]
    public void Below_the_lower_slope_is_outside() => Assert.False(In(20, 190));

    [Fact]
    public void Winding_order_does_not_matter() =>
        Assert.True(SafeTriangle.Contains(10, 50, 100, 200, 100, 0, 60, 100));

    // Submenu box: x 100..300, y 0..200; pointer at (10, 50) heading right.
    [Fact]
    public void Heading_diagonally_at_the_submenu_aims() => Assert.True(SafeTriangle.Aims(10, 50, 5, 8, 100, 300, 0, 200));

    [Fact]
    public void Heading_straight_down_does_not_aim() => Assert.False(SafeTriangle.Aims(10, 50, 0, 8, 100, 300, 0, 200));

    [Fact]
    public void Heading_away_does_not_aim() => Assert.False(SafeTriangle.Aims(10, 50, -5, 1, 100, 300, 0, 200));

    [Fact]
    public void Heading_steeply_past_the_submenu_does_not_aim() => Assert.False(SafeTriangle.Aims(10, 50, 2, 20, 100, 300, 0, 200));
}
