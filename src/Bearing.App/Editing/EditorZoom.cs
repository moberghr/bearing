using System;

namespace Bearing.App.Editing;

/// <summary>
/// The arithmetic behind per-tab editor zoom: a signed step count layered over the configured base font
/// size (Settings ▸ Editor ▸ font size). Steps, not an absolute size, so changing the base in settings
/// still moves a zoomed tab with it — and so "reset" is just zero.
/// <para>Pure and stateless, which is the only way this is testable (§2.5, §4.3).</para>
/// </summary>
public static class EditorZoom
{
    /// <summary>Point size per step. One point per press is the browser/VS Code feel.</summary>
    public const double StepSize = 1;

    public const double MinSize = 6;
    public const double MaxSize = 48;

    /// <summary>The effective size for <paramref name="steps"/> over <paramref name="baseSize"/>, clamped.</summary>
    public static double SizeFor(double baseSize, int steps)
        => Math.Clamp(baseSize + steps * StepSize, MinSize, MaxSize);

    /// <summary>
    /// The step count after nudging by <paramref name="delta"/>, refusing the move that would leave the
    /// legible range. Refusing (rather than clamping the size) matters: clamping would silently bank
    /// presses at the limit, so zooming back out would do nothing for a while.
    /// </summary>
    public static int Nudge(double baseSize, int steps, int delta)
    {
        var next = steps + delta;
        var size = baseSize + next * StepSize;
        return size < MinSize || size > MaxSize ? steps : next;
    }
}

/// <summary>
/// Turns wheel deltas into whole zoom steps. A mouse notch arrives as ±1 and spends immediately; a
/// precision trackpad sends a stream of fractions, and treating each one as a notch (the
/// <c>Math.Sign(delta)</c> shortcut) runs the font from 14 to 48 in a single swipe — so fractions are
/// banked and only whole notches are spent. Reversing direction drops what was banked the other way,
/// otherwise a flick back has to pay off the previous swipe before anything moves.
/// <para>Stateful but UI-free, so the gesture's feel is testable without a pointer (§2.5, §4.3).</para>
/// </summary>
public sealed class WheelZoomAccumulator
{
    private double _banked;

    /// <summary>The steps <paramref name="delta"/> releases — signed, and 0 while a swipe is still short of
    /// a whole notch.</summary>
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
