using System.Collections.Generic;

namespace Squirrel.App.Input;

/// <summary>
/// The built-in keymap — every shortcut Squirrel ships with, in one table. A user
/// <c>keybindings.json</c> will layer over this in Phase 2; today this is the whole map.
/// Gestures are written in the same text form the config and menu use, so this doubles as documentation.
/// </summary>
public static class KeymapDefaults
{
    public static Keymap Build() => new(Bindings());

    private static IEnumerable<KeyBinding> Bindings()
    {
        // ---- Global (resolved on the window bubble path) ----
        yield return G(CommandIds.Run, "Ctrl+Enter"); // primary (shown in menu); F5 is the alias
        yield return G(CommandIds.Run, "F5");
        yield return G(CommandIds.CompletionTrigger, "Ctrl+Space");
        yield return G(CommandIds.FileSave, "Ctrl+S");
        yield return G(CommandIds.FileSaveAs, "Ctrl+Shift+S");
        yield return G(CommandIds.FileOpen, "Ctrl+O");
        yield return G(CommandIds.TabNew, "Ctrl+N"); // primary (shown in menu); Ctrl+T is the alias
        yield return G(CommandIds.TabNew, "Ctrl+T");
        yield return G(CommandIds.TabClose, "Ctrl+W");
        yield return G(CommandIds.TabRename, "F2");
        yield return G(CommandIds.ViewToggleSidePane, "Ctrl+B");
        yield return G(CommandIds.ViewToggleResults, "Ctrl+R");
        yield return G(CommandIds.StatementPrev, "Alt+Up");
        yield return G(CommandIds.StatementNext, "Alt+Down");
        yield return G(CommandIds.AppEscape, "Escape");
        yield return G(CommandIds.PaletteOpen, "Ctrl+Shift+P");
        yield return G(CommandIds.TabNext, "Ctrl+Tab");
        yield return G(CommandIds.TabNext, "Ctrl+PageDown");
        yield return G(CommandIds.TabPrev, "Ctrl+Shift+Tab");
        yield return G(CommandIds.TabPrev, "Ctrl+PageUp");
        yield return G(CommandIds.FocusCycle, "F6");
        // panel.*, connection.new, query.runAll ship unbound — reachable via the command palette
        // (and the rail for panels); users can bind them in keybindings.json.

        // ---- Editor (resolved in the editor's tunnel handler) ----
        yield return E(CommandIds.EditorOpenLineBelow, "Shift+Enter");
        yield return E(CommandIds.EditorOpenLineAbove, "Ctrl+Shift+Enter");
        yield return E(CommandIds.EditorToggleComment, "Ctrl+/");
        yield return E(CommandIds.EditorToggleComment, "Ctrl+-");   // HR layout: that physical key reports OemMinus
        yield return E(CommandIds.EditorSelectStatement, "Ctrl+Shift+A");
        yield return E(CommandIds.EditorFoldCurrent, "Ctrl+Shift+PhysBracketLeft");
        yield return E(CommandIds.EditorUnfoldCurrent, "Ctrl+Shift+PhysBracketRight");
        yield return E(CommandIds.EditorFoldAll, "Ctrl+Shift+-");
        yield return E(CommandIds.EditorUnfoldAll, "Ctrl+Shift+=");

        // ---- Grid (resolved in the results grid's tunnel handler) ----
        yield return R(CommandIds.GridCopy, "Ctrl+C");
        yield return R(CommandIds.GridCopy, "Ctrl+Insert");
        yield return R(CommandIds.GridSelectAll, "Ctrl+A");
        yield return R(CommandIds.GridDelete, "Delete");
        yield return R(CommandIds.GridBeginEdit, "Enter");
        yield return R(CommandIds.GridBeginEdit, "F2");
        yield return R(CommandIds.GridClearSelection, "Escape");
        yield return R(CommandIds.GridFollowFk, "Alt+Right"); // drill into the FK the active cell points to
        yield return R(CommandIds.GridBack, "Alt+Left");      // return to the pre-navigation result
    }

    private static KeyBinding G(string id, string gesture) => new(KeyScope.Global, GestureParser.Parse(gesture), id);
    private static KeyBinding E(string id, string gesture) => new(KeyScope.Editor, GestureParser.Parse(gesture), id);
    private static KeyBinding R(string id, string gesture) => new(KeyScope.Grid, GestureParser.Parse(gesture), id);
}
