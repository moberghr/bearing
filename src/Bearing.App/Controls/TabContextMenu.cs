using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Bearing.App.ViewModels;

namespace Bearing.App.Controls;

/// <summary>
/// A tab's right-click menu: Rename, Save, Close, Reveal in Scripts, Open containing folder. Every one of
/// these already existed and was reachable another way (double-tap, Ctrl+F4, the File menu) — the menu is
/// the discoverability half, and the two reveal items are the answer to "which file is this tab?".
/// <para>
/// Its own class rather than another slab of the window (§9.1), and built per right-click rather than once
/// per tab: applicability (is there anything to save? does this tab have a file yet?) is then simply true at
/// build time, with no stale-menu problem to guard against. The caller supplies the actions and the gestures
/// so this control knows nothing of the command table or the keymap.
/// </para>
/// </summary>
internal static class TabContextMenu
{
    /// <param name="tab">The tab that was right-clicked — <em>not</em> the selected one. A menu on a
    /// background tab must act on that tab, which is what the ✕ button and the double-tap rename already do.</param>
    /// <param name="canSave">False when the buffer matches what's on disk, so Save has nothing to do.</param>
    /// <param name="reveal">Select this tab's file in the Scripts panel; null hides the item.</param>
    /// <param name="openFolder">Show this tab's file in the OS file manager; null hides the item.</param>
    public static MenuFlyout Build(
        EditorTabViewModel tab,
        Func<Task> rename,
        Func<Task> save,
        bool canSave,
        Func<Task> close,
        Action? reveal,
        Action? openFolder,
        KeyGesture? renameGesture = null,
        KeyGesture? saveGesture = null,
        KeyGesture? closeGesture = null)
    {
        var menu = new MenuFlyout();

        // Renaming a scratch tab promotes it out of the scratch folder; for a named script it's a file
        // rename. Same command either way, so say which one this tab will get.
        menu.Items.Add(Item(tab.IsScratch ? "Rename / promote…" : "Rename file…", renameGesture, rename));
        menu.Items.Add(Item("Save", saveGesture, save, canSave));
        menu.Items.Add(Item("Close", closeGesture, close));

        if (reveal is not null || openFolder is not null)
        {
            menu.Items.Add(new Separator());
            if (reveal is not null) menu.Items.Add(Sync("Reveal in Scripts", reveal));
            if (openFolder is not null) menu.Items.Add(Sync("Open containing folder", openFolder));
        }

        return menu;

        // No access-key underscores: the app only uses those on the top-level menu bar (File/Edit/…).
        static MenuItem Item(string header, KeyGesture? gesture, Func<Task> run, bool enabled = true)
        {
            var item = new MenuItem { Header = header, InputGesture = gesture, IsEnabled = enabled };
            item.Click += async (_, _) => await run();
            return item;
        }

        static MenuItem Sync(string header, Action run)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => run();
            return item;
        }
    }
}
