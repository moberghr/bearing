namespace Bearing.Core.Workspace;

/// <summary>
/// App-global, user-editable preferences (persisted at <c>$XDG_CONFIG_HOME/bearing/settings.json</c>).
/// Distinct from per-project session state and from the shareable project manifest.
/// </summary>
public sealed record AppSettings
{
    /// <summary>
    /// How many days of query history to keep. Entries older than this are pruned on startup.
    /// Zero or negative means keep everything forever.
    /// </summary>
    public int QueryLogRetentionDays { get; init; } = 180;

    /// <summary>When editor buffers are written to disk without an explicit Save. See <see cref="Workspace.AutosaveMode"/>.</summary>
    public AutosaveMode AutosaveMode { get; init; } = AutosaveMode.OnEdit;
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
