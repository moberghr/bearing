using Avalonia.Input;

namespace Bearing.App.Input;

/// <summary>
/// When a held-modifier tab cycle (Ctrl+Tab style) ends and commits the tab it landed on as most-recent.
/// <para>
/// The modifier to watch for comes from the <em>keymap</em>, not from a hard-coded Ctrl: the release that ends
/// the cycle used to be `Key.LeftCtrl or Key.RightCtrl` literally, so rebinding <c>tab.mruNext</c> to, say,
/// Alt+Tab left the cycle flag stuck on forever — and with it the MRU order, which is only recorded when a
/// cycle ends.
/// </para>
/// Pure and unit-tested (§2.5); the view only supplies the released key.
/// </summary>
public static class MruCycle
{
    /// <summary>
    /// Modifiers held down by the MRU bindings. <see cref="KeyModifiers.Shift"/> is excluded on purpose: it
    /// only distinguishes next from previous (Ctrl+Tab vs Ctrl+Shift+Tab), so releasing it mid-cycle — which
    /// is exactly what reversing direction does — must not end the cycle. <see cref="KeyModifiers.None"/>
    /// means the binding holds nothing, so there is no release to wait for.
    /// </summary>
    public static KeyModifiers ModifiersOf(Keymap keymap)
    {
        var mods = KeyModifiers.None;
        foreach (var id in new[] { CommandIds.TabMruNext, CommandIds.TabMruPrev })
            foreach (var gesture in keymap.GesturesFor(id))
                mods |= gesture.Modifiers;
        return mods & ~KeyModifiers.Shift;
    }

    /// <summary>Whether releasing <paramref name="released"/> ends a cycle held by <paramref name="mods"/>.</summary>
    public static bool EndsCycle(KeyModifiers mods, Key released)
        => (mods.HasFlag(KeyModifiers.Control) && released is Key.LeftCtrl or Key.RightCtrl)
        || (mods.HasFlag(KeyModifiers.Alt) && released is Key.LeftAlt or Key.RightAlt)
        || (mods.HasFlag(KeyModifiers.Meta) && released is Key.LWin or Key.RWin);
}
