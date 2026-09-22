using System;

namespace Bearing.Results;

/// <summary>
/// Turns the display-timezone setting's text into the <see cref="TimeZoneInfo"/> that
/// <see cref="CellFormat.Zone"/> holds (#77).
/// <para>
/// Here rather than in the app because <b>every host has to set that zone</b> (§2.7), and a host that
/// cannot reach the resolver either copies it or skips it. The CLI did skip it, so an exported
/// <c>timestamptz</c> came out in UTC while the same rows exported from the app carried the user's display
/// zone — two files that disagree, with nothing on either to say which zone it is in. The picker's own
/// members (the suggestion list, the validator, the description) stay in the app, which is the only place
/// that has a picker.
/// </para>
/// </summary>
public static class DisplayZone
{
    /// <summary>The setting value meaning "whatever this machine is set to".</summary>
    public const string SystemId = "system";

    /// <summary>The setting value meaning UTC, and what an empty or unreadable setting falls back to.</summary>
    public const string UtcId = "UTC";

    /// <summary>
    /// The zone a setting's text names.
    /// <para>
    /// An unknown id falls back to UTC rather than throwing or silently using the machine's zone: a typo in a
    /// settings file must not shift every timestamp by an unpredictable amount, and UTC is the one answer
    /// that is never wrong about the instant.
    /// </para>
    /// </summary>
    public static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        if (id.Equals(SystemId, StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Local;
        if (id.Equals(UtcId, StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;

        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception)
        {
            // TimeZoneNotFoundException, InvalidTimeZoneException, or a platform without the zone database.
            return TimeZoneInfo.Utc;
        }
    }
}
