using System;
using System.Runtime.InteropServices;

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

    /// <summary>True when the event AppKit is dispatching is a key-down. False on a click, off macOS, and if
    /// the runtime can't be asked — a click is the safe reading, since it runs the item's own command.</summary>
    public static bool IsFiring()
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
            return current != IntPtr.Zero && SendULong(current, sel_registerName("type")) == NSEventTypeKeyDown;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
