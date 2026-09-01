using Bearing.App.Results;

namespace Bearing.App.Input;

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

    /// <summary>Pin or unpin the selected tab (#67). One command rather than two, because the menu item and
    /// the keystroke both mean "flip this" and a separate Unpin would need its own disabled state.</summary>
    public const string TabTogglePin = "tab.togglePin";
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

    /// <summary>Remove a project from the recent list (and optionally delete its folder). Palette-only —
    /// deliberately unbound, like <see cref="SelectProject"/>.</summary>
    public const string ProjectRemove = "project.remove";
    public const string SelectConnection = "select.connection";
    public const string SelectDatabase = "select.database";

    /// <summary>Command id for jumping to tab <paramref name="n"/> (1-based); n=9 is "last tab" (browser convention).</summary>
    public static string TabGoto(int n) => $"tab.goto{n}";
    public const string QueryRunAll = "query.runAll";
    public const string PanelConnections = "panel.connections";
    public const string PanelScripts = "panel.scripts";
    public const string PanelHistory = "panel.history";
    public const string ConnectionNew = "connection.new";
    public const string ConnectionImportDBeaver = "connection.import.dbeaver";
    public const string SettingsKeybindings = "settings.keybindings";
    public const string SettingsOpen = "settings.open";

    // Editor
    public const string EditorOpenLineBelow = "editor.openLineBelow";
    public const string EditorOpenLineAbove = "editor.openLineAbove";
    public const string EditorToggleComment = "editor.toggleComment";
    public const string EditorSelectStatement = "editor.selectStatement";
    public const string EditorFoldCurrent = "editor.foldCurrent";
    public const string EditorUnfoldCurrent = "editor.unfoldCurrent";
    public const string EditorFoldAll = "editor.foldAll";
    public const string EditorUnfoldAll = "editor.unfoldAll";
    public const string EditorDeleteToLineStart = "editor.deleteToLineStart";
    public const string EditorDeleteWordBack = "editor.deleteWordBack";
    public const string EditorZoomIn = "editor.zoomIn";
    public const string EditorZoomOut = "editor.zoomOut";
    public const string EditorZoomReset = "editor.zoomReset";

    // Grid
    public const string GridCopy = "grid.copy";
    public const string GridPaste = "grid.paste";
    public const string GridFetchAll = "grid.fetchAll";

    /// <summary>Copy the selection in one of the alternative formats: <c>grid.copyAs.csv</c>,
    /// <c>.markdown</c>, <c>.json</c>, <c>.html</c>, <c>.sqlInsert</c>. All ship unbound (Ctrl+C is TSV) and
    /// are reachable from the palette, the grid's context menu, or a user binding.</summary>
    public static string GridCopyAs(CopyFormat format) => "grid.copyAs." + Lower(format.ToString());

    /// <summary>Export the whole result set: <c>grid.export.csv</c> / <c>grid.export.xlsx</c>.</summary>
    public static string GridExport(ExportFormat format) => "grid.export." + Lower(format.ToString());

    /// <summary>Enum name → id segment (<c>SqlInsert</c> → <c>sqlInsert</c>), so ids stay camelCase like
    /// every other one here.</summary>
    private static string Lower(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    public const string GridSelectAll = "grid.selectAll";
    public const string GridDelete = "grid.delete";
    public const string GridBeginEdit = "grid.beginEdit";
    public const string GridSetNull = "grid.setNull";
    public const string GridAddRow = "grid.addRow";
    public const string GridSave = "grid.save";
    public const string GridDiscard = "grid.discard";
    public const string GridClearSelection = "grid.clearSelection";
    public const string GridInspect = "grid.inspectValue";
    public const string GridFollowFk = "grid.followFk";
    public const string GridBack = "grid.back";
}
