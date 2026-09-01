using System;
using System.Collections.Generic;
using System.Linq;

namespace Bearing.App.Formatting;

/// <summary>
/// Which zone timestamps are shown in (#77), and how a setting's text becomes one.
/// <para>
/// Postgres hands back a <c>timestamptz</c> as a <c>DateTime</c> with <c>Kind = Utc</c> — confirmed against
/// Npgsql 10 rather than assumed — so the instant is always UTC on arrival and the display zone is purely a
/// presentation choice. That is what makes it safe: converting for display never changes the stored value,
/// and the offset travels with the text so nothing downstream has to guess.
/// </para>
/// <para>
/// The default is UTC, which keeps every existing display identical — the same numbers, now with
/// <c>+00:00</c> after them, which is ask (1) of the issue on its own.
/// </para>
/// </summary>
public static class DisplayTimeZone
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

    /// <summary>Whether a setting's text names a zone this machine can resolve — for validating what the user
    /// typed, without waiting for a timestamp to render wrongly.</summary>
    public static bool IsKnown(string? id)
        => string.IsNullOrWhiteSpace(id)
           || id.Equals(SystemId, StringComparison.OrdinalIgnoreCase)
           || id.Equals(UtcId, StringComparison.OrdinalIgnoreCase)
           || TryFind(id);

    private static bool TryFind(string id)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Every zone this machine knows, for a picker: <c>system</c> and <c>UTC</c> first — the two answers most
    /// people want — then the rest by id.
    /// <para>
    /// The ids are the platform's own (IANA on Unix, Windows ids on Windows). .NET accepts IANA ids on
    /// Windows too, so a settings file written on one platform still resolves on the other; this list just
    /// offers what is local.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Available()
    {
        var zones = new List<string> { SystemId, UtcId };
        try
        {
            zones.AddRange(TimeZoneInfo.GetSystemTimeZones()
                .Select(z => z.Id)
                .Where(id => !id.Equals(UtcId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception) { /* no zone database: the two above still work */ }
        return zones;
    }

    /// <summary>How a zone reads in the settings row: its id, and the offset it is on right now.</summary>
    public static string Describe(string? id)
    {
        var zone = Resolve(id);
        var offset = zone.GetUtcOffset(DateTimeOffset.UtcNow);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"{zone.Id} (UTC{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00})";
    }
}
