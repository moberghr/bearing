using System;
using System.Linq;
using Avalonia.Input;

namespace Bearing.App.Input;

/// <summary>
/// A keymap <see cref="Gesture"/> as the <see cref="KeyGesture"/> a menu shows: the menu bar, the macOS
/// native bar, the editor's context menu and the tab-strip dropdown all go through here.
/// <para>
/// The keymap folds Cmd into Ctrl (<see cref="Gesture"/> normalizes Meta), so a binding written
/// <c>Ctrl+S</c> is Cmd+S on a Mac. A menu takes the modifier literally — handed Control it would draw ⌃S,
/// and a native item would claim the Control key — so on macOS Control goes back to Meta. A physical-key
/// binding has no <see cref="KeyGesture"/> form and gets no gesture rather than a wrong one.
/// </para>
/// </summary>
public static class MenuGestures
{
    /// <summary>True on macOS, where the keymap's Ctrl is the Command key.</summary>
    public static bool CommandKey => OperatingSystem.IsMacOS();

    public static KeyGesture? From(Gesture? gesture, bool commandKey)
    {
        if (gesture is not { Logical: { } key } g)
        {
            return null;
        }
        var mods = g.Modifiers;
        if (commandKey && mods.HasFlag(KeyModifiers.Control))
        {
            mods = (mods & ~KeyModifiers.Control) | KeyModifiers.Meta;
        }
        return new KeyGesture(key, mods);
    }

    /// <summary>The command's first gesture in <paramref name="keymap"/> — logical ones sort first
    /// (<see cref="Keymap.GesturesFor"/>), so a physical one is reached only when it is all there is.</summary>
    public static KeyGesture? For(Keymap keymap, string commandId, bool commandKey) =>
        From(keymap.GesturesFor(commandId).Cast<Gesture?>().FirstOrDefault(), commandKey);

    /// <summary><see cref="For(Keymap, string, bool)"/> for this platform.</summary>
    public static KeyGesture? For(Keymap keymap, string commandId) => For(keymap, commandId, CommandKey);

    /// <summary>A fixed gesture written in keymap text (<c>Ctrl+X</c>), for this platform.</summary>
    public static KeyGesture? Parse(string text) =>
        GestureParser.TryParse(text, out var gesture) ? From(gesture, CommandKey) : null;
}
