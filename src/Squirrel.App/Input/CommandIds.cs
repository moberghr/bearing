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
}
