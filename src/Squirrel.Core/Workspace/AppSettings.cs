namespace Squirrel.Core.Workspace;

/// <summary>
/// App-global, user-editable preferences (persisted at <c>$XDG_CONFIG_HOME/squirrel/settings.json</c>).
/// Distinct from per-project session state and from the shareable project manifest.
/// </summary>
public sealed record AppSettings
{
    /// <summary>
    /// How many days of query history to keep. Entries older than this are pruned on startup.
    /// Zero or negative means keep everything forever.
    /// </summary>
    public int QueryLogRetentionDays { get; init; } = 180;
}
