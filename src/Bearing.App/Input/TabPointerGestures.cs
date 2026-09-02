using Avalonia.Input;

namespace Bearing.App.Input;

/// <summary>
/// Which mouse button means what on a tab header. Pointer gestures don't go through the keymap (§9.2 covers
/// the keyboard), so this is where the rule is written down instead of being implied by two code-behind
/// handlers — and, being a function of the update kind alone, it is the part that can be tested without a
/// window.
/// <para>
/// The button matters because the handlers previously ignored it: the ✕ closed a tab on <i>any</i> press,
/// so a right-click aimed at the context menu closed the tab out from under the menu it was opening (#66).
/// </para>
/// </summary>
internal static class TabPointerGestures
{
    /// <summary>A press on the tab header that closes it. Middle-click is the convention every tabbed app
    /// shares — browsers, VS Code, the IDEs — and the fastest way to clear a pile of scratch buffers.</summary>
    public static bool ClosesTab(PointerUpdateKind kind) => kind == PointerUpdateKind.MiddleButtonPressed;

    /// <summary>A press on the ✕ that actually means "close". Only the left button: a right-click there is
    /// opening the context menu, and a middle-click is already handled by the header beneath it, so letting
    /// either through would close the tab twice or against the user's intent.</summary>
    public static bool ActivatesCloseButton(PointerUpdateKind kind) => kind == PointerUpdateKind.LeftButtonPressed;
}
