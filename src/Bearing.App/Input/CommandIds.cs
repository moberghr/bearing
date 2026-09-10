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
    public const string TabPick = "tab.pick";          // a modal list of every tab, fuzzy-filtered (Ctrl+E)

    /// <summary>
    /// Expand the tab strip so every tab is on screen at once, wrapped onto as many rows as it takes — what
    /// the strip's own end button does. Unbound by default: the button is the affordance, and this exists so
    /// the palette can reach it and <c>keybindings.json</c> can bind it.
    /// </summary>
    public const string TabExpandStrip = "tab.expandStrip";
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
    public const string QueryCancel = "query.cancel";
    public const string QueryExplain = "query.explain";
    public const string QueryExplainAnalyze = "query.explainAnalyze";
    public const string PanelConnections = "panel.connections";
    public const string PanelScripts = "panel.scripts";
    public const string PanelHistory = "panel.history";

    /// <summary>Export the query history as an audit report (#113). No default binding — it is a deliberate,
    /// occasional action, reachable from the palette and the History panel's own button.</summary>
    public const string HistoryExport = "history.export";
    public const string ConnectionNew = "connection.new";
    public const string ConnectionImportDBeaver = "connection.import.dbeaver";
    /// <summary>Fuzzy-pick a relation from the loaded schema and reveal it in the tree (#117).</summary>
    public const string SchemaGotoTable = "schema.gotoTable";

    public const string SettingsKeybindings = "settings.keybindings";
    public const string SettingsOpen = "settings.open";

    // Editor
    /// <summary>Lay out the selection, or the whole buffer when there is none (#102). Layout only — see
    /// <c>Bearing.Sql.SqlFormat</c>, which refuses rather than guessing at SQL it could not parse.</summary>
    public const string EditorFormat = "editor.format";
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
    /// <summary>F12 on the identifier under the caret: reveal what it refers to in the schema tree (#117).
    /// Editor scope, because it is about the caret.</summary>
    public const string EditorGotoDefinition = "editor.gotoDefinition";

    public const string EditorZoomReset = "editor.zoomReset";

    // Grid
    public const string GridCopy = "grid.copy";
    public const string GridPaste = "grid.paste";
    public const string GridFetchAll = "grid.fetchAll";

    /// <summary>Copy the selection in one of the alternative formats: <c>grid.copyAs.csv</c>,
    /// <c>.markdown</c>, <c>.json</c>, <c>.html</c>, <c>.htmlWithQuery</c>, <c>.sqlInsert</c>. All ship
    /// unbound (Ctrl+C is TSV) and are reachable from the palette, the grid's context menu, or a user
    /// binding.</summary>
    public static string GridCopyAs(CopyFormat format) => "grid.copyAs." + Lower(format.ToString());

    /// <summary>Export the whole result set: <c>grid.export.csv</c> / <c>grid.export.xlsx</c>.</summary>
    public static string GridExport(ExportFormat format) => "grid.export." + Lower(format.ToString());

    /// <summary>
    /// Export every result of the run as one workbook (#12). Global rather than grid-scoped: a run belongs to
    /// the tab, not to whichever grid the caret happens to be in, and it is reachable with no grid focused.
    /// </summary>
    public const string QueryExportRun = "query.export.run";

    /// <summary>Enum name → id segment (<c>SqlInsert</c> → <c>sqlInsert</c>), so ids stay camelCase like
    /// every other one here.</summary>
    private static string Lower(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    public const string GridSelectAll = "grid.selectAll";
    public const string GridDelete = "grid.delete";
    public const string GridBeginEdit = "grid.beginEdit";
    public const string GridSetNull = "grid.setNull";
    public const string GridAddRow = "grid.addRow";
    public const string GridSave = "grid.save";
    public const string GridShowSql = "grid.showSql";
    public const string GridDiscard = "grid.discard";
    public const string GridClearSelection = "grid.clearSelection";
    public const string GridInspect = "grid.inspectValue";
    public const string GridFollowFk = "grid.followFk";
    public const string GridBack = "grid.back";
}
