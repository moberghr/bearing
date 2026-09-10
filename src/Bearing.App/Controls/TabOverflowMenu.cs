using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.ViewModels;

namespace Bearing.App.Controls;

/// <summary>
/// The tab strip's dropdown: every open tab, in strip order, click one to go to it — Visual Studio's
/// document-well dropdown, in the same place.
/// <para>
/// It is the third route to a tab, and each of the three answers a different question. This one answers
/// "which tabs do I have open" with a list you point at, right where the tabs are. The modal picker
/// (Ctrl+E) answers "where is the one called X" — it filters, and it carries each tab's folder, which is
/// what tells two same-named scripts apart. Expanding the strip answers "show me all of them at once" and
/// leaves them on screen to be dragged around. Both of the others hang off the bottom of this menu, so the
/// button is the one place you look.
/// </para>
/// <para>
/// Built per press like <see cref="TabContextMenu"/>, and for the same reason: which tab is selected, which
/// is dirty and how many are off the edge are then simply true at build time, with no stale-menu problem to
/// guard against. The caller supplies the actions and the gestures, so this knows nothing of the command
/// table or the keymap.
/// </para>
/// </summary>
internal static class TabOverflowMenu
{
    /// <summary>
    /// How long a tab's name may be in the menu before it is trimmed. A dropdown as wide as the longest
    /// filename someone has open is a dropdown that covers the results grid; the full name stays in the
    /// item's tooltip, as it does on the tab itself.
    /// </summary>
    private const int MaxLabel = 44;

    /// <summary>
    /// Refill <paramref name="menu"/> for the current tabs. Called every time the flyout opens rather than
    /// once, so the list cannot go stale behind the button.
    /// </summary>
    /// <param name="pinned">The pinned row, in its own order.</param>
    /// <param name="unpinned">The rest of the strip. Listed after the pinned tabs, behind a separator —
    /// the same two groups the strip draws as two rows (§9.1).</param>
    /// <param name="selected">The active tab, marked with a tick. Nothing else in a menu says "you are
    /// here", and without it the list reads as a set of destinations one of which is where you already are.</param>
    /// <param name="go">Go to a tab. The caller checks it is still open — a menu can outlive a tab that a
    /// background close took away.</param>
    /// <param name="expanded">Whether the strip is currently expanded, which decides what the last-but-one
    /// item offers.</param>
    public static void Fill(
        MenuFlyout menu,
        IReadOnlyList<EditorTabViewModel> pinned,
        IReadOnlyList<EditorTabViewModel> unpinned,
        EditorTabViewModel? selected,
        Action<EditorTabViewModel> go,
        bool expanded,
        Action toggleExpand,
        Action openPicker,
        KeyGesture? pickerGesture = null,
        KeyGesture? expandGesture = null)
    {
        menu.Items.Clear();

        foreach (var tab in pinned) menu.Items.Add(Item(tab, selected, go));
        if (pinned.Count > 0 && unpinned.Count > 0) menu.Items.Add(new Separator());
        foreach (var tab in unpinned) menu.Items.Add(Item(tab, selected, go));

        menu.Items.Add(new Separator());
        menu.Items.Add(Command(
            expanded ? "Collapse the tab strip" : "Show all tabs in the strip", expandGesture, toggleExpand));
        menu.Items.Add(Command("Go to tab…", pickerGesture, openPicker));
    }

    /// <summary>One tab's row. The dirty dot is the strip's own marker, so the two agree about which tabs
    /// have unsaved work.</summary>
    private static MenuItem Item(EditorTabViewModel tab, EditorTabViewModel? selected, Action<EditorTabViewModel> go)
    {
        var item = new MenuItem
        {
            Header = Label(tab),
            // The tick goes in the icon column, not into the text: it has to line up down the list, and a
            // marker inside the header would move with the name's length.
            Icon = ReferenceEquals(tab, selected) ? Tick() : null,
        };
        // The same tooltip the tab header carries — project-relative inside the project, absolute outside —
        // because a name trimmed to fit a menu is exactly when you need the path.
        ToolTip.SetTip(item, tab.HeaderTooltip);
        item.Click += (_, _) => go(tab);
        return item;
    }

    /// <summary>
    /// A tab's label: its name, trimmed, with the unsaved-work dot after it.
    /// <para>
    /// Pure and internal so the trimming is testable — the interesting part is that a name is trimmed and
    /// the dot is not, since a marker lost to the ellipsis is worse than no marker.
    /// </para>
    /// </summary>
    internal static string Label(EditorTabViewModel tab)
    {
        var name = tab.Header;
        if (name.Length > MaxLabel) name = name[..(MaxLabel - 1)] + "…";
        return tab.IsDirty ? $"{name} ●" : name;
    }

    private static Control Tick() => new TextBlock
    {
        Text = "✓",
        FontSize = 12,
        Foreground = Tokens.Res("Accent.Brand"),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static MenuItem Command(string header, KeyGesture? gesture, Action run)
    {
        var item = new MenuItem { Header = header, InputGesture = gesture };
        item.Click += (_, _) => run();
        return item;
    }
}
