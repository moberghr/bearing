namespace Bearing.Core.Workspace;

/// <summary>
/// App-global, user-editable preferences (persisted as <c>settings.json</c> in the per-platform config
/// directory — see <c>Bearing.Persistence.BearingPaths.ConfigDir</c>).
/// Distinct from per-project session state and from the shareable project manifest.
/// <para>
/// <b>Adding a setting is two edits, both in this folder:</b> add an init-only property here (with a
/// default that matches today's hard-coded behaviour), then add a matching descriptor to
/// <see cref="SettingsCatalog"/>. The settings window renders itself from the catalog, so a new entry
/// gets a row, a section, and search coverage with no UI work. A test asserts every property here is
/// either described or explicitly listed as hidden state, so the second edit can't be forgotten.
/// </para>
/// </summary>
public sealed record AppSettings
{
    /// <summary>The as-shipped values. Descriptors read their default off this, so a default lives in
    /// exactly one place — the property initialiser below.</summary>
    public static readonly AppSettings Defaults = new();

    /// <summary>
    /// How many days of query history to keep. Entries older than this are pruned on startup.
    /// Zero or negative means keep everything forever.
    /// </summary>
    public int QueryLogRetentionDays { get; init; } = 180;

    /// <summary>When editor buffers are written to disk without an explicit Save. See <see cref="Workspace.AutosaveMode"/>.</summary>
    public AutosaveMode AutosaveMode { get; init; } = AutosaveMode.OnEdit;

    /// <summary>Base point size of the SQL editor text.</summary>
    public int EditorFontSize { get; init; } = 14;

    /// <summary>Whether closing a tab that holds unsaved work asks first. Off means a close discards it
    /// silently — which is why the default is on, autosave or not.</summary>
    public bool ConfirmTabClose { get; init; } = true;

    /// <summary>Minutes an unused connection is kept open before the idle sweep closes it. A connection
    /// serving a running query holds a lease and is never swept, whatever this says.</summary>
    public int ConnectionIdleTimeoutMinutes { get; init; } = 30;

    /// <summary>Rows fetched per page, both for a query's first page and for each Load more.</summary>
    public int ResultPageSize { get; init; } = 100;

    /// <summary>
    /// Where "Fetch all rows" stops. It streams the result to the end, so without a ceiling a mistyped query
    /// against a billion-row table would read until the app runs out of memory — the ceiling also bounds what
    /// the server is asked to produce. Hitting it is reported, never silent, and the rows already streamed
    /// stay loaded.
    /// </summary>
    public int ResultFetchAllMaxRows { get; init; } = 200_000;

    /// <summary>Whether the main window reopens at the size it was last closed at.</summary>
    public bool RestoreWindowSize { get; init; } = true;

    /// <summary>
    /// Whether the menu bar stays on screen. Off, it is Alt-tap-to-reveal only — invisible to anyone who
    /// doesn't know the gesture. Pinned, it is furniture: nothing auto-hides it, and it no longer counts as
    /// a modal-ish surface that swallows global shortcuts.
    /// </summary>
    public bool ShowMenuBar { get; init; }

    /// <summary>
    /// Whether the app checks the release feed on startup and downloads a newer version in the background.
    /// Installing is never automatic — a downloaded update waits for the user to restart, so a running query
    /// or an unsaved buffer is never interrupted by one.
    /// </summary>
    public bool AutoUpdate { get; init; } = true;

    // ---- persisted state, not user-facing preferences (no catalog entry; see SettingsCatalogTests) ----

    /// <summary>Last main-window width, written on close. Null until a window has been closed once.</summary>
    public double? WindowWidth { get; init; }

    /// <summary>Last main-window height, written on close. Null until a window has been closed once.</summary>
    public double? WindowHeight { get; init; }
}

/// <summary>
/// When a tab's buffer is written without an explicit Save.
/// <para>
/// This governs <b>named scripts</b>. A scratch buffer is always written at the checkpoints that would
/// otherwise lose it (tab close, project switch, shutdown) in every mode, including <see cref="Off"/> —
/// its file in the scratch folder is the buffer's only home, not a convenience.
/// </para>
/// </summary>
public enum AutosaveMode
{
    /// <summary>Write shortly after typing stops (debounced). The default.</summary>
    OnEdit,

    /// <summary>Write when the tab's SQL is executed, and not while typing.</summary>
    OnExecute,

    /// <summary>Never write automatically. Named scripts go dirty and are guarded by the close prompt.</summary>
    Off,
}
