using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Input;

namespace Bearing.App.Views;

/// <summary>
/// Whether the native menu item firing right now was reached by its key equivalent rather than a click.
/// <para>
/// On macOS a keystroke matching a menu item's key equivalent goes to the menu <b>instead of</b> the window:
/// Avalonia raises <c>KeyDown</c> from <c>AvnView keyDown:</c>, which AppKit never calls for a matched key
/// equivalent. So a menu that claims Cmd+S would take it from the result grid (where it saves edits) and
/// hand it to File ▸ Save. The menu bar tells the two apart by asking AppKit for the event being handled —
/// <c>[[NSApplication sharedApplication] currentEvent]</c> — and replays a key equivalent through Avalonia's
/// own input path.
/// </para>
/// </summary>
internal static class MacKeyEquivalent
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const ulong NSEventTypeKeyDown = 10;

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern ulong SendULong(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPtrRet(IntPtr receiver, IntPtr selector);

    // NSEventModifierFlags — the four a key equivalent can carry. Function / numeric-pad / caps-lock bits are
    // ignored: an arrow or F-key reports NSEventModifierFlagFunction whether or not anyone pressed fn.
    private const ulong ShiftFlag = 1UL << 17;
    private const ulong ControlFlag = 1UL << 18;
    private const ulong OptionFlag = 1UL << 19;
    private const ulong CommandFlag = 1UL << 20;

    /// <summary>
    /// True when the event AppKit is dispatching is a key-down <b>that is <paramref name="gesture"/></b> — the
    /// item was reached by its key equivalent. A menu item can also be chosen by keyboard without being one
    /// (Help ▸ Search + Return, Full Keyboard Access, VoiceOver): then the current event is that Return, it does
    /// not match, and the item runs as a click. False on a mouse click, off macOS, and if the runtime can't be
    /// asked — a click is the safe reading, since it runs the item's own command.
    /// </summary>
    public static bool IsFiring(KeyGesture gesture)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }
        try
        {
            var app = SendPtr(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app == IntPtr.Zero)
            {
                return false;
            }
            var current = SendPtr(app, sel_registerName("currentEvent"));
            if (current == IntPtr.Zero || SendULong(current, sel_registerName("type")) != NSEventTypeKeyDown)
            {
                return false;
            }
            var flags = SendULong(current, sel_registerName("modifierFlags"));
            var chars = SendPtr(current, sel_registerName("charactersIgnoringModifiers"));
            var utf8 = chars == IntPtr.Zero ? IntPtr.Zero : SendPtrRet(chars, sel_registerName("UTF8String"));
            return Matches(gesture, flags, utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a key-down with these <c>modifierFlags</c> and <c>charactersIgnoringModifiers</c> is
    /// <paramref name="gesture"/>. ⌘ ⌃ ⌥ must be exactly the gesture's (Meta, Control, Alt); an extra Shift
    /// is allowed only when it was needed to type the gesture's own non-letter character (see below). <c>charactersIgnoringModifiers</c> still applies Shift, so a shifted key matches its
    /// US-layout shifted character too (⌘⇧/ arrives as "?"). A key with no known character never matches —
    /// the item then runs as a click, which is what it did before key equivalents were replayed.
    /// </summary>
    internal static bool Matches(KeyGesture gesture, ulong modifierFlags, string? characters)
    {
        if (string.IsNullOrEmpty(characters) || characters.Length != 1 || !CharFor.TryGetValue(gesture.Key, out var expected))
        {
            return false;
        }
        var mods = gesture.KeyModifiers;
        if (((modifierFlags & ControlFlag) != 0) != mods.HasFlag(KeyModifiers.Control)
            || ((modifierFlags & OptionFlag) != 0) != mods.HasFlag(KeyModifiers.Alt)
            || ((modifierFlags & CommandFlag) != 0) != mods.HasFlag(KeyModifiers.Meta))
        {
            return false;
        }
        var actual = char.ToLowerInvariant(characters[0]);
        var shiftDown = (modifierFlags & ShiftFlag) != 0;
        if (shiftDown == mods.HasFlag(KeyModifiers.Shift))
        {
            return actual == expected || (Shifted.TryGetValue(expected, out var shifted) && actual == shifted);
        }
        // Shift held that the gesture doesn't name: still this key when the layout needs Shift to type the
        // character itself — '/' is Shift+7 on a Croatian keyboard, so ⌘/ arrives as ⌘⇧ with "/". On US the
        // same Shift turns '/' into '?', which this does not accept.
        return shiftDown && actual == expected && !char.IsLetter(expected);
    }

    /// <summary>The character AppKit reports for a key, unshifted (letters lower case). Function keys are
    /// AppKit's private-use code points (<c>NSF1FunctionKey</c> = U+F704 …).</summary>
    private static readonly Dictionary<Key, char> CharFor = BuildCharFor();

    private static Dictionary<Key, char> BuildCharFor()
    {
        var map = new Dictionary<Key, char>
        {
            [Key.OemComma] = ',',
            [Key.OemPeriod] = '.',
            [Key.OemQuestion] = '/',
            [Key.OemMinus] = '-',
            [Key.OemPlus] = '=',
            [Key.OemOpenBrackets] = '[',
            [Key.OemCloseBrackets] = ']',
            [Key.OemSemicolon] = ';',
            [Key.OemQuotes] = '\'',
            [Key.OemPipe] = '\\',
            [Key.OemBackslash] = '\\',
            [Key.OemTilde] = '`',
            [Key.Space] = ' ',
            [Key.Enter] = '\r',
            [Key.Tab] = '\t',
            [Key.Escape] = '\u001b',
            [Key.Back] = '\u007f',
            [Key.Delete] = '\uF728',
            [Key.Insert] = '\uF727',
            [Key.Up] = '\uF700',
            [Key.Down] = '\uF701',
            [Key.Left] = '\uF702',
            [Key.Right] = '\uF703',
            [Key.Home] = '\uF729',
            [Key.End] = '\uF72B',
            [Key.PageUp] = '\uF72C',
            [Key.PageDown] = '\uF72D',
        };
        for (var k = Key.A; k <= Key.Z; k++)
        {
            map[k] = (char)('a' + (k - Key.A));
        }
        for (var k = Key.D0; k <= Key.D9; k++)
        {
            map[k] = (char)('0' + (k - Key.D0));
        }
        for (var k = Key.F1; k <= Key.F24; k++)
        {
            map[k] = (char)(0xF704 + (k - Key.F1));
        }
        return map;
    }

    /// <summary>US-layout Shift pairs, for <c>charactersIgnoringModifiers</c> on a shifted key.</summary>
    private static readonly Dictionary<char, char> Shifted = new()
    {
        ['/'] = '?',
        [','] = '<',
        ['.'] = '>',
        ['-'] = '_',
        ['='] = '+',
        ['['] = '{',
        [']'] = '}',
        [';'] = ':',
        ['\''] = '"',
        ['\\'] = '|',
        ['`'] = '~',
        ['1'] = '!',
        ['2'] = '@',
        ['3'] = '#',
        ['4'] = '$',
        ['5'] = '%',
        ['6'] = '^',
        ['7'] = '&',
        ['8'] = '*',
        ['9'] = '(',
        ['0'] = ')',
    };
}
