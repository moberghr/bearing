namespace Bearing.App.Input;

/// <summary>
/// Which tabs are off the edge of a strip, as arithmetic (#65).
/// <para>
/// Split out from the view for the usual reason (§2.5): the interesting part is a rule about spans and a
/// viewport, and it is the part that has edge cases — a tab straddling the edge, a strip that fits exactly, a
/// strip narrower than one tab. None of that needs a window to check.
/// </para>
/// <para>
/// The rule is <b>fully</b> visible, not partly: a tab whose label is half cut off is one the user cannot
/// read, so it belongs in the overflow list. Counting it as visible is how a chevron reads "0" beside a tab
/// that is plainly sliced in two.
/// </para>
/// </summary>
internal static class TabOverflow
{
    /// <summary>Sub-pixel slack. Layout arithmetic lands a hair over an edge constantly — a tab arranged at
    /// 419.9997 inside a 420 viewport is not overflowing, and a chevron that flickers "1" on every resize is
    /// worse than no chevron.</summary>
    private const double Slack = 0.5;

    /// <summary>
    /// How many of <paramref name="spans"/> are not fully inside the viewport.
    /// </summary>
    /// <param name="spans">Each tab's (start, width) along the strip, in the strip's own coordinates —
    /// i.e. unscrolled. Order does not matter.</param>
    /// <param name="offset">How far the strip is scrolled.</param>
    /// <param name="viewport">The visible width.</param>
    public static int HiddenCount(IReadOnlyList<(double Start, double Width)> spans, double offset, double viewport)
    {
        // A viewport of zero means "not laid out yet", not "everything is hidden". Reporting the tab count
        // there would light the chevron up during startup and every time the window is restored from
        // minimised, both of which are moments when nothing has overflowed.
        if (viewport <= 0) return 0;

        var count = 0;
        foreach (var (start, width) in spans)
        {
            var left = start - offset;
            if (left < -Slack || left + width > viewport + Slack) count++;
        }
        return count;
    }

    /// <summary>
    /// Which edges of the strip have more content beyond them, and so should be faded out.
    /// <para>
    /// A tab at the viewport boundary is cut mid-glyph — <c>store-health</c> ends as <c>store-he</c> — which
    /// reads as a broken label rather than as a tab that continues off-screen. Fading the last few pixels is
    /// the usual answer (browsers do it to overflowing text) and, unlike hiding partial tabs, it is purely a
    /// paint: an opacity mask changes no measurement, so it cannot feed back into the layout the way the
    /// chevron's own width did.
    /// </para>
    /// <para>
    /// Only an edge with something past it fades. A permanent left fade would dim the first tab of a strip
    /// that is simply sitting at its start, which is a lie in the other direction.
    /// </para>
    /// </summary>
    /// <param name="offset">How far the strip is scrolled.</param>
    /// <param name="extent">The strip's full width.</param>
    /// <param name="viewport">The visible width.</param>
    public static (bool Left, bool Right) FadeEdges(double offset, double extent, double viewport)
    {
        // Not laid out, or it all fits: nothing is cut, so nothing fades.
        if (viewport <= 0 || extent <= viewport + Slack) return (false, false);
        return (offset > Slack, offset < extent - viewport - Slack);
    }

    /// <summary>Whether a chevron should be shown at all. Same rule as <see cref="HiddenCount"/> being
    /// positive, named so the call site reads as intent.</summary>
    public static bool Overflows(IReadOnlyList<(double Start, double Width)> spans, double offset, double viewport)
        => HiddenCount(spans, offset, viewport) > 0;
}
