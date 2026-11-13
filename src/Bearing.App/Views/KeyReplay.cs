using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Bearing.App.Views;

/// <summary>
/// A keystroke the macOS menu bar took as a key equivalent, handed back to the window as the <c>KeyDown</c>
/// it would have been (see <see cref="AppMenuBar"/> and <see cref="MacKeyEquivalent"/>).
/// </summary>
internal static class KeyReplay
{
    /// <summary>
    /// Raise <paramref name="gesture"/> as a <c>KeyDown</c> on the focused element of
    /// <paramref name="window"/> (the window itself when nothing has focus) — tunnel and bubble, the route
    /// Avalonia's keyboard device gives a real key. Whatever would have handled the key handles it; when
    /// nothing does, nothing happens, as before the menu existed. True when something handled it.
    /// </summary>
    public static bool Raise(Window window, KeyGesture gesture)
    {
        var target = window.FocusManager?.GetFocusedElement() as Interactive ?? window;
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = gesture.Key,
            KeyModifiers = gesture.KeyModifiers,
            PhysicalKey = PhysicalFor(gesture.Key),
            Source = target,
        };
        target.RaiseEvent(args);
        return args.Handled;
    }

    private static Dictionary<Key, PhysicalKey>? _physical;

    /// <summary>The physical key a logical one sits on in a QWERTY layout — what the keymap's physical
    /// bindings (fold on <c>[</c>) match against. <see cref="PhysicalKey.None"/> when no key maps.</summary>
    internal static PhysicalKey PhysicalFor(Key key)
    {
        _physical ??= Enum.GetValues<PhysicalKey>()
            .Where(p => p != PhysicalKey.None)
            .GroupBy(p => p.ToQwertyKey())
            .ToDictionary(g => g.Key, g => g.First());
        return _physical.TryGetValue(key, out var physical) ? physical : PhysicalKey.None;
    }
}
