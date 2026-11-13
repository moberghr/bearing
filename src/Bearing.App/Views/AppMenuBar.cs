using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Bearing.App.Input;
using Bearing.App.ViewModels;

namespace Bearing.App.Views;

/// <summary>
/// The macOS menu bar: the application menu (About, Settings, Quit) and File / Edit / View / Query /
/// Connection / Tabs / Help. Without it the system shows Avalonia's stock menu.
/// <para>
/// Built from the <see cref="CommandRegistry"/>, like the editor's context menu: labels name the commands,
/// gestures come from the live keymap (<see cref="SyncGestures"/>, on open and after a rebind).
/// </para>
/// <para>
/// <b>A key equivalent is not a click.</b> AppKit gives a keystroke that matches an item to the menu instead
/// of the window (<see cref="MacKeyEquivalent"/>), so if the item ran its command the menu would decide what
/// every listed key does wherever focus is — Cmd+S saving the script over a grid's pending edits, F2
/// renaming the tab instead of editing the cell, editor-only keys acting from the tree. So when an item fires
/// from its key, the key is replayed into the focused control of the active window as the
/// <c>KeyDown</c> it would have been, and the existing pipeline (scopes, text boxes, the grid, AvaloniaEdit,
/// a dialog) decides exactly as it did before the bar existed. Only a click runs the item's own command.
/// </para>
/// <para>
/// A click on a command item runs only while this window is the active one — a modal dialog in front owns
/// the app — and only if the command can run, before anything moves focus. Edit's clipboard items act on
/// whatever has focus in the active window, a dialog's text box included.
/// </para>
/// <para>
/// Desktop macOS only (<see cref="Enabled"/>). Elsewhere, and in the headless tests, the in-window bar
/// (<c>MainMenu</c>) that an Alt tap reveals is the menu.
/// </para>
/// </summary>
internal sealed class AppMenuBar
{
    /// <summary>Set by the desktop entry point on macOS (<see cref="AppBuilderFactory.BuildAvaloniaApp"/>).
    /// Off in the headless tests, which build the same window and must keep its in-window bar.</summary>
    public static bool Enabled { get; set; }

    private readonly Window _window;
    private readonly TextEditor _editor;
    private readonly CommandRegistry _commands;
    private readonly Func<Keymap> _keymap;
    private readonly Func<UpdateViewModel?> _updates;
    private readonly List<(NativeMenuItem Item, KeyCommand Command)> _wired = new();

    private AppMenuBar(Window window, TextEditor editor, CommandRegistry commands, Func<Keymap> keymap,
        Func<UpdateViewModel?> updates)
    {
        _window = window;
        _editor = editor;
        _commands = commands;
        _keymap = keymap;
        _updates = updates;
    }

    /// <summary>Attach the bar to <paramref name="window"/> and the application menu to the app; null (and
    /// nothing attached) when <see cref="Enabled"/> is off.</summary>
    /// <param name="keymap">Read live: the shortcuts editor can replace the map at runtime.</param>
    public static AppMenuBar? Install(Window window, TextEditor editor, CommandRegistry commands, Func<Keymap> keymap,
        Func<UpdateViewModel?> updates, Action about)
    {
        if (!Enabled)
        {
            return null;
        }
        var bar = new AppMenuBar(window, editor, commands, keymap, updates);
        if (Application.Current is { } app)
        {
            NativeMenu.SetMenu(app, bar.BuildApplicationMenu(about));
        }
        NativeMenu.SetMenu(window, bar.BuildWindowMenu());
        bar.SyncGestures();
        return bar;
    }

    /// <summary>Re-read every item's gesture from the live keymap. Called when a menu opens and after the
    /// shortcuts editor saves, so neither the labels nor the key equivalents outlive a rebind.</summary>
    public void SyncGestures()
    {
        var map = _keymap();
        foreach (var (item, command) in _wired)
        {
            item.Gesture = MenuGestures.For(map, command.Id, commandKey: true);
        }
    }

    // ---- the application menu ---------------------------------------------------------------------

    /// <summary>The bold "Bearing" menu. Avalonia appends Hide / Quit after these.</summary>
    private NativeMenu BuildApplicationMenu(Action about)
    {
        var menu = new NativeMenu();
        var aboutItem = Action("About Bearing", about);
        var check = Action("Check for Updates…", () => _updates()?.CheckNowCommand.Execute(null));
        menu.Add(aboutItem);
        menu.Add(check);
        menu.Add(new NativeMenuItemSeparator());
        Add(menu, CommandIds.SettingsOpen, "Settings…");
        Add(menu, CommandIds.SettingsKeybindings, "Keyboard Shortcuts…");
        WireOpening(menu, () =>
        {
            aboutItem.IsEnabled = _window.IsActive;
            check.IsEnabled = _window.IsActive && _updates() is not null;
        });
        return menu;
    }

    // ---- the window's menus -----------------------------------------------------------------------

    private NativeMenu BuildWindowMenu()
    {
        var bar = new NativeMenu();

        bar.Add(Submenu("File", m =>
        {
            Add(m, CommandIds.TabNew, "New Query");
            Add(m, CommandIds.ConnectionNew, "New Connection…");
            Add(m, CommandIds.FileOpen, "Open…");
            m.Add(new NativeMenuItemSeparator());
            Add(m, CommandIds.FileSave, "Save");
            Add(m, CommandIds.FileSaveAs, "Save As…");
            m.Add(new NativeMenuItemSeparator());
            Add(m, CommandIds.ConnectionImportDBeaver, "Import Connections from DBeaver…");
            Add(m, CommandIds.HistoryExport, "Export Query History…");
            m.Add(new NativeMenuItemSeparator());
            Add(m, CommandIds.TabClose, "Close Tab");
        }));

        // "Edit" is the header macOS recognises: it adds its own text items (dictation, emoji) under it.
        bar.Add(Submenu("Edit", m =>
        {
            m.Add(Clip("Undo", Key.Z, KeyModifiers.Meta, ClipAction.Undo));
            m.Add(Clip("Redo", Key.Z, KeyModifiers.Meta | KeyModifiers.Shift, ClipAction.Redo));
            m.Add(new NativeMenuItemSeparator());
            m.Add(Clip("Cut", Key.X, KeyModifiers.Meta, ClipAction.Cut));
            m.Add(Clip("Copy", Key.C, KeyModifiers.Meta, ClipAction.Copy));
            m.Add(Clip("Paste", Key.V, KeyModifiers.Meta, ClipAction.Paste));
            m.Add(Clip("Select All", Key.A, KeyModifiers.Meta, ClipAction.SelectAll));
            m.Add(new NativeMenuItemSeparator());
            Add(m, CommandIds.EditorFormat, "Format SQL");
            Add(m, CommandIds.EditorToggleComment, "Toggle Comment");
            Add(m, CommandIds.EditorSelectStatement, "Select Statement");
            m.Add(new NativeMenuItemSeparator());
            Add(m, CommandIds.EditorFoldAll, "Fold All");
            Add(m, CommandIds.EditorUnfoldAll, "Unfold All");
        }));

        bar.Add(Submenu("View", m =>
        {
            Add(m, CommandIds.PaletteOpen, "Command Palette…");
            m.Add(new NativeMenuItemSeparator());
            Add(m, CommandIds.PanelConnections, "Connections");
            Add(m, CommandIds.PanelScripts, "Scripts");
            Add(m, CommandIds.PanelHistory, "History");
            Add(m, CommandIds.PanelActivity, "Server Activity");
            m.Add(new NativeMenuItemSeparator());
            Add(m, CommandIds.ViewToggleSidePane, "Toggle Side Pane");
            Add(m, CommandIds.ViewToggleResults, "Toggle Results");
            Add(m, CommandIds.ViewToggleInlineResults, "Toggle Inline Results");
            m.Add(new NativeMenuItemSeparator());
            Add(m, CommandIds.EditorZoomIn, "Zoom In");
            Add(m, CommandIds.EditorZoomOut, "Zoom Out");
            Add(m, CommandIds.EditorZoomReset, "Actual Size");
            m.Add(new NativeMenuItemSeparator());
            Add(m, CommandIds.ViewToggleColorScheme, "Toggle Light/Dark Theme");
        }));

        bar.Add(Submenu("Query", m =>
        {
            Add(m, CommandIds.Run, "Run");
            Add(m, CommandIds.QueryRunAll, "Run Entire Script");
            Add(m, CommandIds.QueryCancel, "Cancel Running Query");
            m.Add(new NativeMenuItemSeparator());
            Add(m, CommandIds.QueryExplain, "Explain Statement");
            Add(m, CommandIds.QueryExplainAnalyze, "Explain and Analyse (Runs the Statement)");
            m.Add(new NativeMenuItemSeparator());
            Add(m, CommandIds.TransactionCommit, "Commit Transaction");
            Add(m, CommandIds.TransactionRollback, "Roll Back Transaction");
            Add(m, CommandIds.TransactionToggleMode, "Switch Auto / Manual Commit");
            m.Add(new NativeMenuItemSeparator());
            Add(m, CommandIds.QueryExportRun, "Export Run to Excel…");
        }));

        bar.Add(Submenu("Connection", m =>
        {
            Add(m, CommandIds.SelectProject, "Select Project…");
            Add(m, CommandIds.SelectConnection, "Select Connection…");
            Add(m, CommandIds.SelectDatabase, "Select Database…");
            m.Add(new NativeMenuItemSeparator());
            Add(m, CommandIds.SchemaGotoTable, "Go to Table…");
            Add(m, CommandIds.EditorGotoDefinition, "Go to Definition");
        }));

        bar.Add(Submenu("Tabs", m =>
        {
            Add(m, CommandIds.TabNext, "Next Tab");
            Add(m, CommandIds.TabPrev, "Previous Tab");
            Add(m, CommandIds.TabPick, "Go to Tab…");
            m.Add(new NativeMenuItemSeparator());
            Add(m, CommandIds.TabRename, "Rename Tab…");
            Add(m, CommandIds.TabTogglePin, "Pin / Unpin Tab");
            Add(m, CommandIds.TabMoveToProject, "Move Tab to Project…");
            Add(m, CommandIds.TabCopyToProject, "Copy Script into Project");
        }));

        bar.Add(Submenu("Help", m =>
        {
            var notes = Action("What's New", () => _updates()?.WhatsNewCommand.Execute(null));
            m.Add(notes);
            Add(m, CommandIds.SettingsKeybindings, "Keyboard Shortcuts…");
            WireOpening(m, () => notes.IsEnabled = _window.IsActive && _updates()?.CanShowNotes == true);
        }));

        return bar;
    }

    private NativeMenuItem Submenu(string header, Action<NativeMenu> fill)
    {
        var menu = new NativeMenu();
        fill(menu);
        WireOpening(menu, null);
        return new NativeMenuItem(header) { Menu = menu };
    }

    // ---- command items ----------------------------------------------------------------------------

    private void Add(NativeMenu menu, string commandId, string header)
    {
        if (_commands.Get(commandId) is not { } command)
        {
            return;
        }
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => Fire(item, () => Click(command, _window.IsActive, () => _editor.TextArea.Focus()));
        _wired.Add((item, command));
        menu.Add(item);
    }

    /// <summary>A click on a command item: only with this window in front (a modal dialog owns the app),
    /// only if the command can run, and only then — for an editor-scoped command, which acts on the caret —
    /// focus the editor. Nothing moves when nothing runs.</summary>
    internal static bool Click(KeyCommand command, bool windowActive, Action focusEditor)
    {
        if (!windowActive || !command.CanRun())
        {
            return false;
        }
        if (command.Scope == KeyScope.Editor)
        {
            focusEditor();
        }
        command.Invoke();
        return true;
    }

    private NativeMenuItem Action(string header, Action run)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => Fire(item, run);
        return item;
    }

    /// <summary>The one entry point for every item: a key equivalent is replayed as the keystroke it was,
    /// anything else — a click, or the item chosen by keyboard without being its key equivalent (Help ▸
    /// Search + Return, Full Keyboard Access, VoiceOver) — runs <paramref name="click"/>.</summary>
    private void Fire(NativeMenuItem item, Action click)
    {
        if (item.Gesture is { } gesture && MacKeyEquivalent.IsFiring(gesture))
        {
            KeyReplay.Raise(ActiveWindow(), gesture);
            return;
        }
        click();
    }

    private Window ActiveWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
            .FirstOrDefault(w => w.IsActive) ?? _window;

    /// <summary>Gestures and guards are re-read whenever a menu opens. Command items are greyed only for
    /// the length of that look: on close they are re-enabled, so a stale guard never makes AppKit swallow
    /// a key equivalent the replay would have delivered.</summary>
    private void WireOpening(NativeMenu menu, Action? extra)
    {
        menu.Opening += (_, _) =>
        {
            SyncGestures();
            foreach (var (item, command) in _wired)
            {
                if (menu.Items.Contains(item))
                {
                    item.IsEnabled = _window.IsActive && command.CanRun();
                }
            }
            extra?.Invoke();
        };
        menu.Closed += (_, _) =>
        {
            foreach (var item in menu.Items.OfType<NativeMenuItem>())
            {
                item.IsEnabled = true;
            }
        };
    }

    // ---- Edit's clipboard items -------------------------------------------------------------------

    internal enum ClipAction { Undo, Redo, Cut, Copy, Paste, SelectAll }

    private NativeMenuItem Clip(string header, Key key, KeyModifiers mods, ClipAction action)
    {
        var item = new NativeMenuItem(header) { Gesture = new KeyGesture(key, mods) };
        item.Click += (_, _) => Fire(item, () => Apply(action));
        return item;
    }

    /// <summary>A clicked clipboard item, on whatever has focus in the active window — a dialog's included.
    /// A grid's cell editor is a text box, so that comes first; a result grid is checked before the editor
    /// because inline results live inside it.</summary>
    internal void Apply(ClipAction action)
    {
        var focused = ActiveWindow().FocusManager?.GetFocusedElement() as Visual;
        switch (focused)
        {
            case TextBox box:
                Apply(action, box);
                return;
            case not null when focused.FindAncestorOfType<DataGrid>(includeSelf: true) is not null:
                ApplyToGrid(action);
                return;
            case not null when focused.FindAncestorOfType<TextEditor>(includeSelf: true) is { } editor:
                Apply(action, editor);
                return;
        }
    }

    private static void Apply(ClipAction action, TextBox box)
    {
        switch (action)
        {
            case ClipAction.Undo: box.Undo(); break;
            case ClipAction.Redo: box.Redo(); break;
            case ClipAction.Cut: box.Cut(); break;
            case ClipAction.Copy: box.Copy(); break;
            case ClipAction.Paste: box.Paste(); break;
            case ClipAction.SelectAll: box.SelectAll(); break;
        }
    }

    private static void Apply(ClipAction action, TextEditor editor)
    {
        switch (action)
        {
            case ClipAction.Undo: editor.Undo(); break;
            case ClipAction.Redo: editor.Redo(); break;
            case ClipAction.Cut: editor.Cut(); break;
            case ClipAction.Copy: editor.Copy(); break;
            case ClipAction.Paste: editor.Paste(); break;
            case ClipAction.SelectAll: editor.SelectAll(); break;
        }
    }

    /// <summary>A grid has no undo and no cut; copy, paste and select-all are its own commands.</summary>
    private void ApplyToGrid(ClipAction action)
    {
        var id = action switch
        {
            ClipAction.Copy => CommandIds.GridCopy,
            ClipAction.Paste => CommandIds.GridPaste,
            ClipAction.SelectAll => CommandIds.GridSelectAll,
            _ => null,
        };
        if (id is not null)
        {
            _commands.Get(id)?.TryInvoke();
        }
    }
}
