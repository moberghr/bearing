using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using Bearing.App.Input;

namespace Bearing.App.Editing;

/// <summary>
/// The SQL editor's right-click menu: run / explain, the clipboard three, format, the two statement-shaped
/// edits (select, comment) and fold-all. The editor had no context menu at all — every one of these
/// actions existed but was reachable only by a gesture you had to already know.
/// <para>
/// Items come from the <see cref="CommandRegistry"/> rather than being spelled out here: the label is the
/// command's own <see cref="KeyCommand.Title"/> and the shown gesture is resolved from the live keymap
/// <b>on open</b>, so a rebind in <c>keybindings.json</c> or the shortcuts editor can never leave the menu
/// advertising a dead key (the failure the menu bar's <see cref="Keymap.DisplayGesture"/> sync exists to
/// prevent). A command that isn't registered is simply not offered.
/// </para>
/// <para>
/// Its own class, not another slab of <c>MainWindow</c> (§9.1). Cut/Copy/Paste are the exception to the
/// command-table rule: they are AvaloniaEdit's own, not ours, so they are called directly and their
/// gestures are the platform's rather than the keymap's.
/// </para>
/// </summary>
internal static class EditorContextMenu
{
    /// <param name="keymap">Read on open, not captured: the shortcuts editor can replace the map at runtime.</param>
    public static void Install(TextEditor editor, CommandRegistry commands, Func<Keymap> keymap)
    {
        var flyout = new MenuFlyout();
        var wired = new List<(MenuItem Item, KeyCommand Command)>();

        void Add(string commandId)
        {
            if (commands.Get(commandId) is not { } command) return;
            var item = new MenuItem { Header = command.Title };
            // Through CrashReporter.Observe, like the keyboard and palette paths — a bare `async void`
            // handler would let a faulting command (Run against a connection that just dropped, Export)
            // throw unhandled on the UI thread, past the app's own reporting.
            item.Click += (_, _) => CrashReporter.Observe(command.Run(), $"command '{command.Id}'");
            flyout.Items.Add(item);
            wired.Add((item, command));
        }

        Add(CommandIds.Run);
        Add(CommandIds.QueryRunAll);
        Add(CommandIds.QueryExplain);
        flyout.Items.Add(new Separator());

        // AvaloniaEdit's own clipboard, with the platform gestures it already handles — not keymap commands,
        // so nothing here can be rebound and nothing here should claim it can be.
        var cut = ClipboardItem("Cut", "Ctrl+X", editor.Cut);
        var copy = ClipboardItem("Copy", "Ctrl+C", editor.Copy);
        var paste = ClipboardItem("Paste", "Ctrl+V", editor.Paste);
        flyout.Items.Add(cut);
        flyout.Items.Add(copy);
        flyout.Items.Add(paste);
        flyout.Items.Add(new Separator());

        Add(CommandIds.EditorFormat);
        Add(CommandIds.EditorSelectStatement);
        Add(CommandIds.EditorToggleComment);
        flyout.Items.Add(new Separator());
        Add(CommandIds.EditorFoldAll);
        Add(CommandIds.EditorUnfoldAll);

        flyout.Opening += (_, _) =>
        {
            var map = keymap();
            foreach (var (item, command) in wired)
            {
                item.InputGesture = Gesture(map.DisplayGesture(command.Id));
                item.IsEnabled = command.CanRun();
            }

            // Cut and Copy act on the selection; Paste writes at the caret, so it stays available with
            // nothing selected.
            var selected = editor.SelectionLength > 0;
            cut.IsEnabled = selected;
            copy.IsEnabled = selected;
        };

        // On the TextArea: that is what the pointer actually hits, and what raises ContextRequested.
        editor.TextArea.ContextFlyout = flyout;
        editor.TextArea.AddHandler(
            InputElement.PointerPressedEvent,
            (_, e) => PlaceCaretForRightClick(editor, e),
            RoutingStrategies.Tunnel);
    }

    private static MenuItem ClipboardItem(string header, string gesture, Action run)
    {
        var item = new MenuItem { Header = header, InputGesture = Gesture(gesture) };
        item.Click += (_, _) => run();
        return item;
    }

    /// <summary>Display-only, so a binding with no <see cref="KeyGesture"/> form (a physical-key gesture)
    /// shows nothing rather than throwing — the same tolerance the menu bar's sync applies.</summary>
    private static KeyGesture? Gesture(string? text)
    {
        if (text is null) return null;
        try { return KeyGesture.Parse(text); } catch { return null; }
    }

    /// <summary>
    /// Put the caret where the user right-clicked, unless they clicked inside the selection. Half this menu
    /// acts on the statement under the caret (Run, Explain, Toggle comment) — without this it would act on
    /// wherever the caret was last left, which is not where the user is pointing. Clicking inside a
    /// selection leaves it alone: that is the thing Copy and Toggle comment are about to be asked to act on.
    /// <para>Mirrors the results grid, whose cells collapse the selection onto themselves on a right-click
    /// outside it.</para>
    /// </summary>
    private static void PlaceCaretForRightClick(TextEditor editor, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(editor.TextArea).Properties.IsRightButtonPressed) return;
        if (editor.GetPositionFromPoint(e.GetPosition(editor)) is not { } position) return;

        var offset = editor.Document.GetOffset(position.Location);
        if (offset >= editor.SelectionStart && offset <= editor.SelectionStart + editor.SelectionLength) return;

        editor.TextArea.ClearSelection();
        editor.TextArea.Caret.Position = position;
    }
}
