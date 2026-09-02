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
