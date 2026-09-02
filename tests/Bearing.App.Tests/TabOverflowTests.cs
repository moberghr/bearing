using Bearing.App.Input;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Counting the tabs that are off the edge of the strip (#65). Pure arithmetic, so the edge cases are
/// cheap — and the edge cases are where a chevron reads a number that contradicts what is on screen.
/// </summary>
public class TabOverflowTests
{
    /// <summary>Four 100px tabs laid out end to end.</summary>
    private static (double, double)[] FourTabs() => [(0, 100), (100, 100), (200, 100), (300, 100)];

    [Fact]
    public void Nothing_is_hidden_when_the_strip_fits()
    {
        Assert.Equal(0, TabOverflow.HiddenCount(FourTabs(), offset: 0, viewport: 400));
        Assert.False(TabOverflow.Overflows(FourTabs(), offset: 0, viewport: 400));
    }

    [Fact]
    public void A_tab_past_the_right_edge_counts()
    {
        // 250px of viewport holds two whole tabs; the third straddles the edge and the fourth is gone.
        Assert.Equal(2, TabOverflow.HiddenCount(FourTabs(), offset: 0, viewport: 250));
    }

    [Fact]
    public void A_partly_visible_tab_counts_as_hidden()
    {
        // The rule that matters for the count being believable: a tab sliced down the middle is one the user
        // cannot read, so it belongs in the list. Counting it visible is how a chevron says "0" beside a tab
        // that is plainly cut in half.
        Assert.Equal(1, TabOverflow.HiddenCount([(0, 100), (100, 100)], offset: 0, viewport: 150));
    }

    [Fact]
    public void Scrolling_moves_which_tabs_are_hidden_not_how_many()
    {
        // Scrolled to the far end, the same two tabs' worth is visible — but it is the other two that are not.
        // The count is what the chevron shows, so it is the count that has to stay right through a scroll.
        var spans = FourTabs();
        Assert.Equal(2, TabOverflow.HiddenCount(spans, offset: 0, viewport: 200));
        Assert.Equal(2, TabOverflow.HiddenCount(spans, offset: 200, viewport: 200));
    }

    [Fact]
    public void A_tab_scrolled_off_the_left_counts_too()
    {
        // Off the near edge is just as unreachable as off the far one, and the first fix for #65 only ever
        // scrolled rightwards — so this is the direction it is easy to forget.
        Assert.Equal(1, TabOverflow.HiddenCount([(0, 100), (100, 100)], offset: 100, viewport: 200));
    }

    [Fact]
    public void Sub_pixel_layout_does_not_invent_an_overflow()
    {
        // Layout arithmetic lands a hair over an edge constantly. A chevron that flickers "1" on every resize
        // of a strip that fits is worse than no chevron, so a fraction of a pixel is slack, not overflow.
        Assert.Equal(0, TabOverflow.HiddenCount([(0, 100), (100, 100.0001)], offset: 0, viewport: 200));
        // …but a real pixel is a real overflow.
        Assert.Equal(1, TabOverflow.HiddenCount([(0, 100), (100, 102)], offset: 0, viewport: 200));
    }

    [Fact]
    public void An_unlaid_out_strip_reports_nothing_hidden()
    {
        // Viewport 0 means "not arranged yet", not "everything is hidden". Reporting the tab count here would
        // light the chevron during startup and on every restore from minimised — moments when nothing has
        // overflowed and the user is being told four tabs are missing.
        Assert.Equal(0, TabOverflow.HiddenCount(FourTabs(), offset: 0, viewport: 0));
        Assert.Equal(0, TabOverflow.HiddenCount([], offset: 0, viewport: 400));
    }

    // ---- which edge dissolves --------------------------------------------------------------------

    [Fact]
    public void A_strip_that_fits_fades_neither_edge()
    {
        // Nothing is cut, so nothing may be dimmed. A permanent fade would shade the first and last tab of a
        // strip that is simply short, which is a lie in the other direction from the one being fixed.
        Assert.Equal((false, false), TabOverflow.FadeEdges(offset: 0, extent: 300, viewport: 400));
        Assert.Equal((false, false), TabOverflow.FadeEdges(offset: 0, extent: 400, viewport: 400));
    }

    [Fact]
    public void At_the_start_only_the_far_edge_fades()
    {
        // Sitting at offset 0 there is nothing behind you, so a left fade would dim the first tab for no
        // reason — which is the case that makes this a pair of booleans rather than one.
        Assert.Equal((false, true), TabOverflow.FadeEdges(offset: 0, extent: 900, viewport: 400));
    }

    [Fact]
    public void At_the_end_only_the_near_edge_fades()
    {
        Assert.Equal((true, false), TabOverflow.FadeEdges(offset: 500, extent: 900, viewport: 400));
    }

    [Fact]
    public void In_the_middle_both_edges_fade()
    {
        Assert.Equal((true, true), TabOverflow.FadeEdges(offset: 250, extent: 900, viewport: 400));
    }

    [Fact]
    public void Sub_pixel_offsets_do_not_fade_an_edge_that_is_flush()
    {
        // Scroll offsets land a hair off zero constantly. A fade flickering onto the first tab every time the
        // strip settles is worse than the hard edge this replaced.
        Assert.Equal((false, true), TabOverflow.FadeEdges(offset: 0.0001, extent: 900, viewport: 400));
        Assert.Equal((true, false), TabOverflow.FadeEdges(offset: 499.9999, extent: 900, viewport: 400));
    }

    [Fact]
    public void An_unlaid_out_strip_fades_nothing()
    {
        Assert.Equal((false, false), TabOverflow.FadeEdges(offset: 0, extent: 900, viewport: 0));
    }

    [Fact]
    public void A_strip_narrower_than_one_tab_hides_that_tab()
    {
        // Not a curiosity: it is a very narrow window, and the answer has to be "1 hidden" rather than 0,
        // because the tab genuinely cannot be read.
        Assert.Equal(1, TabOverflow.HiddenCount([(0, 200)], offset: 0, viewport: 80));
    }
}
