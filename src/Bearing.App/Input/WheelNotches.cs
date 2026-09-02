using System;

namespace Bearing.App.Input;

/// <summary>
/// Turns wheel deltas into whole notches. A mouse notch arrives as ±1 and spends immediately; a
/// precision trackpad sends a stream of fractions, and treating each one as a notch (the
/// <c>Math.Sign(delta)</c> shortcut) runs a swipe through fifty steps — so fractions are banked and only
/// whole notches are spent. Reversing direction drops what was banked the other way, otherwise a flick
/// back has to pay off the previous swipe before anything moves.
/// <para>
/// Shared by the two wheel gestures that step something discrete: Ctrl+wheel's editor zoom (#51) and the
/// tab strip's switch-tab-on-scroll. It lives here rather than beside either because the arithmetic is a
/// property of the wheel, not of what the notches are spent on.
/// </para>
/// <para>Stateful but UI-free, so a gesture's feel is testable without a pointer (§2.5, §4.3).</para>
/// </summary>
public sealed class WheelNotches
{
    private double _banked;

    /// <summary>The notches <paramref name="delta"/> releases — signed, and 0 while a swipe is still short
    /// of a whole notch.</summary>
    public int Add(double delta)
    {
        if (delta == 0) return 0;
        if (Math.Sign(delta) != Math.Sign(_banked)) _banked = 0;
        _banked += delta;
        var steps = (int)_banked;   // truncates toward zero, so the remainder stays banked
        _banked -= steps;
        return steps;
    }
}
