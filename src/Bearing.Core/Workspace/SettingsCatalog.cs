using System;
using System.Collections.Generic;
using System.Linq;

namespace Bearing.Core.Workspace;

/// <summary>
/// Every user-facing preference, in display order. This is the whole contract between
/// <see cref="AppSettings"/> and the settings window: the window renders sections, rows, search and
/// reset straight off this list and knows nothing about individual settings.
/// <para>
/// <b>To add a setting:</b> add the property to <see cref="AppSettings"/>, then add a descriptor below in
/// the section it belongs to. Nothing else needs touching for it to appear, be searchable, persist, and
/// reset. If it can't take effect until restart, say so in <see cref="SettingDescriptor.AppliesNote"/> —
/// the window applies edits immediately, so an unmarked row promises immediacy.
/// </para>
/// </summary>
public static class SettingsCatalog
{
    // Category ids — consts so a descriptor can't drift from a section by a typo.
    public const string General = "general";
    public const string Editor = "editor";
    public const string Results = "results";
    public const string Connections = "connections";
    public const string History = "history";

    /// <summary>Sections, in the order the window lists them. A section with no descriptors is not shown.</summary>
    public static IReadOnlyList<SettingsCategory> Categories { get; } =
    [
        new(General, "General", "Window and application behaviour."),
        new(Editor, "Editor", "The SQL editor and how buffers are saved."),
        new(Results, "Results", "The result grid and paging."),
        new(Connections, "Connections", "Connection lifetime and pooling."),
        new(History, "History", "The local query log."),
    ];

    /// <summary>Every described setting. Order within a section is the order declared here.</summary>
    public static IReadOnlyList<SettingDescriptor> All { get; } =
    [
        // ---- General ---------------------------------------------------------------------------
        new BoolSetting
        {
            Key = "general.restoreWindowSize",
            CategoryId = General,
            Title = "Restore window size on startup",
            Description = "Reopen the main window at the size it was last closed at. Position is left to the "
                        + "window manager.",
            Keywords = "window geometry dimensions",
            Get = s => s.RestoreWindowSize,
            Set = (s, v) => s with { RestoreWindowSize = v },
        },
        new BoolSetting
        {
            Key = "general.showMenuBar",
            CategoryId = General,
            Title = "Always show the menu bar",
            Description = "Keep the File/Edit/View/Query/Help bar on screen. Off, tap Alt to reveal it and "
                        + "Esc or a click elsewhere to hide it again.",
            Keywords = "menu menubar bar alt hidden pinned toolbar",
            Get = s => s.ShowMenuBar,
            Set = (s, v) => s with { ShowMenuBar = v },
        },

        new BoolSetting
        {
            Key = "general.autoUpdate",
            CategoryId = General,
            Title = "Download updates automatically",
            Description = "Check for a newer Bearing on startup and download it in the background. Installing "
                        + "always waits for you to restart, so an update never interrupts a query or loses an "
                        + "unsaved buffer.",
            Keywords = "update updates upgrade version release download automatic",
            Get = s => s.AutoUpdate,
            Set = (s, v) => s with { AutoUpdate = v },
        },

        // ---- Editor ----------------------------------------------------------------------------
        new EnumSetting
        {
            Key = "editor.autosaveMode",
            CategoryId = Editor,
            Title = "Autosave",
            Description = "When a named script is written to disk without an explicit Save. Scratch buffers "
                        + "are always written at tab close, project switch and shutdown, whatever this says.",
            Keywords = "save autosave automatic write dirty scratch",
            Options =
            [
                new(nameof(AutosaveMode.OnEdit), "As you type",
                    "Writes shortly after typing stops, so a saved script never shows as modified. Git is the undo."),
                new(nameof(AutosaveMode.OnExecute), "When the query runs",
                    "Writes at the moment a run starts, and never while typing."),
                new(nameof(AutosaveMode.Off), "Never",
                    "Named scripts go dirty and are guarded by the close prompt."),
            ],
            Get = s => s.AutosaveMode.ToString(),
            Set = (s, v) => s with { AutosaveMode = Enum.Parse<AutosaveMode>(v) },
        },
        new IntSetting
        {
            Key = "editor.fontSize",
            CategoryId = Editor,
            Title = "Font size",
            Description = "Point size of the SQL editor text.",
            Keywords = "text zoom bigger smaller point",
            Min = 8,
            Max = 32,
            Unit = "pt",
            Get = s => s.EditorFontSize,
            Set = (s, v) => s with { EditorFontSize = v },
        },
        new BoolSetting
        {
            Key = "editor.confirmTabClose",
            CategoryId = Editor,
            Title = "Confirm before closing a tab with unsaved work",
            Description = "Turning this off discards unsaved changes silently when a tab is closed.",
            Keywords = "prompt ask discard lose close tab dirty modified",
            Get = s => s.ConfirmTabClose,
            Set = (s, v) => s with { ConfirmTabClose = v },
        },

        // ---- Results ---------------------------------------------------------------------------
        new IntSetting
        {
            Key = "results.pageSize",
            CategoryId = Results,
            Title = "Rows per page",
            Description = "How many rows a query fetches at a time, both for the first page and for each "
                        + "Load more.",
            Keywords = "paging fetch limit batch",
            Min = 10,
            Max = 10_000,
            Unit = "rows",
            AppliesNote = "Applies to the next query you run.",
            Get = s => s.ResultPageSize,
            Set = (s, v) => s with { ResultPageSize = v },
        },
        new IntSetting
        {
            Key = "results.inspectorFontSize",
            CategoryId = Results,
            Title = "Cell inspector font size",
            Description = "Point size of the value text in the cell inspector pane. Ctrl+wheel over the "
                        + "pane changes it too, and lands back here.",
            Keywords = "json preview zoom bigger smaller point text",
            Min = 8,
            Max = 32,
            Unit = "pt",
            Get = s => s.InspectorFontSize,
            Set = (s, v) => s with { InspectorFontSize = v },
        },
        new IntSetting
        {
            Key = "results.fetchAllMaxRows",
            CategoryId = Results,
            Title = "Stop “Fetch all rows” at",
            Description = "Fetch all rows keeps paging until the query is exhausted; this is where it gives "
                        + "up instead. Reaching the limit is reported and the rows already fetched stay loaded.",
            Keywords = "fetch all limit cap maximum rows memory export",
            Min = 1_000,
            Max = 5_000_000,
            Unit = "rows",
            Get = s => s.ResultFetchAllMaxRows,
            Set = (s, v) => s with { ResultFetchAllMaxRows = v },
        },

        // ---- Connections -----------------------------------------------------------------------
        new IntSetting
        {
            Key = "connections.idleTimeoutMinutes",
            CategoryId = Connections,
            Title = "Close idle connections after",
            Description = "An unused connection is closed once it has been idle this long. A connection "
                        + "serving a running query is never closed, however long it takes.",
            Keywords = "disconnect timeout pool sweep evict",
            Min = 1,
            Max = 1440,
            Unit = "minutes",
            Get = s => s.ConnectionIdleTimeoutMinutes,
            Set = (s, v) => s with { ConnectionIdleTimeoutMinutes = v },
        },

        // ---- History ---------------------------------------------------------------------------
        new IntSetting
        {
            Key = "history.retentionDays",
            CategoryId = History,
            Title = "Keep query history for",
            Description = "Older entries are pruned on startup. Zero keeps everything forever. The log holds "
                        + "the SQL you ran, verbatim and unredacted.",
            Keywords = "query log prune retention delete privacy",
            Min = 0,
            Max = 3650,
            Unit = "days",
            AppliesNote = "Pruning runs at the next startup.",
            Get = s => s.QueryLogRetentionDays,
            Set = (s, v) => s with { QueryLogRetentionDays = v },
        },
    ];

    /// <summary>The descriptors in a section, in declaration order.</summary>
    public static IReadOnlyList<SettingDescriptor> InCategory(string categoryId)
        => All.Where(d => d.CategoryId == categoryId).ToList();

    /// <summary>The descriptor with this key, or null.</summary>
    public static SettingDescriptor? Find(string key)
        => All.FirstOrDefault(d => d.Key == key);
}
