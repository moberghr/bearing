namespace Squirrel.App.Input;

/// <summary>Stable command id constants, shared by the defaults table and the registration sites so a
/// typo can't silently unbind a command.</summary>
public static class CommandIds
{
    // Global
    public const string Run = "run";
    public const string CompletionTrigger = "completion.trigger";
    public const string FileSave = "file.save";
    public const string FileSaveAs = "file.saveAs";
    public const string FileOpen = "file.open";
    public const string TabNew = "tab.new";
    public const string TabClose = "tab.close";
    public const string TabRename = "tab.rename";
    public const string ViewToggleSidePane = "view.toggleSidePane";
    public const string ViewToggleResults = "view.toggleResults";
    public const string StatementPrev = "statement.prev";
    public const string StatementNext = "statement.next";
    public const string AppEscape = "app.escape";
    public const string PaletteOpen = "palette.open";
    public const string TabNext = "tab.next";          // visual order (tab strip)
    public const string TabPrev = "tab.prev";
    public const string TabMruNext = "tab.mruNext";    // most-recently-used order (Ctrl+Tab)
    public const string TabMruPrev = "tab.mruPrev";
    public const string FocusCycle = "focus.cycle";
    public const string FocusEditor = "focus.editor";
    public const string FocusResults = "focus.results";
    public const string SelectProject = "select.project";
    public const string SelectConnection = "select.connection";
    public const string SelectDatabase = "select.database";

    /// <summary>Command id for jumping to tab <paramref name="n"/> (1-based); n=9 is "last tab" (browser convention).</summary>
    public static string TabGoto(int n) => $"tab.goto{n}";
    public const string QueryRunAll = "query.runAll";
    public const string PanelConnections = "panel.connections";
    public const string PanelScripts = "panel.scripts";
    public const string PanelHistory = "panel.history";
    public const string ConnectionNew = "connection.new";
    public const string SettingsKeybindings = "settings.keybindings";

    // Editor
    public const string EditorOpenLineBelow = "editor.openLineBelow";
    public const string EditorOpenLineAbove = "editor.openLineAbove";
    public const string EditorToggleComment = "editor.toggleComment";
    public const string EditorSelectStatement = "editor.selectStatement";
    public const string EditorFoldCurrent = "editor.foldCurrent";
    public const string EditorUnfoldCurrent = "editor.unfoldCurrent";
    public const string EditorFoldAll = "editor.foldAll";
    public const string EditorUnfoldAll = "editor.unfoldAll";

    // Grid
    public const string GridCopy = "grid.copy";
    public const string GridSelectAll = "grid.selectAll";
    public const string GridDelete = "grid.delete";
    public const string GridBeginEdit = "grid.beginEdit";
    public const string GridClearSelection = "grid.clearSelection";
    public const string GridFollowFk = "grid.followFk";
    public const string GridBack = "grid.back";
}
