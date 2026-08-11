using System.Linq;
using Bearing.App.Input;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The index arithmetic behind the shell's two navigation rings: tab switching
/// (<see cref="TabNavigator"/>) and focus cycling (<see cref="FocusRing"/>). Both were inline in
/// <c>MainWindow</c>, where an off-by-one could only be found by clicking around — the actual focusing and
/// tab selection still need eyeball QA (§4.3), but the wrap and clamp rules no longer do.
/// </summary>
public class ShellNavigationTests
{
    // ---- tab.next / tab.prev -----------------------------------------------------------------

    [Theory]
    [InlineData(0, +1, 1)]
    [InlineData(2, +1, 0)]   // wraps forward off the end
    [InlineData(0, -1, 2)]   // wraps backward off the start
    [InlineData(1, -1, 0)]
    public void Adjacent_tab_wraps_in_both_directions(int current, int dir, int expected)
        => Assert.Equal(expected, TabNavigator.AdjacentIndex(count: 3, current, dir));

    [Fact]
    public void Adjacent_tab_of_a_single_tab_stays_on_it()
        => Assert.Equal(0, TabNavigator.AdjacentIndex(count: 1, current: 0, dir: +1));

    // ---- tab.goto{n} -------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    public void Goto_n_is_one_based(int n, int expected)
        => Assert.Equal(expected, TabNavigator.GotoIndex(count: 5, n));

    [Fact]
    public void Goto_9_means_the_last_tab_however_many_there_are()
    {
        Assert.Equal(4, TabNavigator.GotoIndex(count: 5, n: 9));
        Assert.Equal(0, TabNavigator.GotoIndex(count: 1, n: 9));
        Assert.Equal(11, TabNavigator.GotoIndex(count: 12, n: 9)); // even past nine tabs
    }

    [Fact]
    public void Goto_past_the_end_clamps_to_the_last_tab()
        => Assert.Equal(1, TabNavigator.GotoIndex(count: 2, n: 8));

    // ---- focus.cycle -------------------------------------------------------------------------

    [Fact]
    public void Focus_order_starts_after_the_current_region_and_wraps_back_to_it()
    {
        // Ending on `current` matters: when every other region refuses focus, the caller must still land
        // somewhere rather than leaving focus nowhere.
        Assert.Equal([1, 2, 0], FocusRing.Order(count: 3, current: 0).ToArray());
        Assert.Equal([0, 1, 2], FocusRing.Order(count: 3, current: 2).ToArray());
    }

    [Fact]
    public void An_unrecognised_current_region_starts_from_the_first()
        // Nothing focused yet (or focus sits outside every region) — start the ring at index 0.
        => Assert.Equal([1, 2, 0], FocusRing.Order(count: 3, current: -1).ToArray());

    [Fact]
    public void A_single_region_ring_offers_only_itself()
        => Assert.Equal([0], FocusRing.Order(count: 1, current: 0).ToArray());

    [Fact]
    public void An_empty_ring_offers_nothing()
        => Assert.Empty(FocusRing.Order(count: 0, current: -1));
}
