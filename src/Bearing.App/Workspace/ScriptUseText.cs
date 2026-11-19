using System.Globalization;
using Bearing.Core.Logging;

namespace Bearing.App.Workspace;

/// <summary>
/// The wording of a script row's dim trailer in the Scripts panel and of the server menu's recent
/// scripts: which database a script was last run against, and when. Pure, so it is tested without a
/// window (§2.5).
/// <para>
/// "Used" means a recorded run. A script with none shows the file's edit date instead, and says so in
/// the tooltip — an edit date is never presented as a use, and a missing record is never called "never
/// run": runs made before the log linked them to scripts, or older than its retention, are not in it.
/// </para>
/// </summary>
public static class ScriptUseText
{
    /// <summary>The row's trailer: <c>database · day</c> for a recorded run, the edit day alone without
    /// one, or empty when neither is known.</summary>
    public static string Trailer(ScriptUse? use, DateTimeOffset? modified, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (use is not null)
        {
            var day = Day(use.ExecutedAt, now, zone);
            return string.IsNullOrWhiteSpace(use.Database) ? day : $"{use.Database} · {day}";
        }
        return modified is { } edited ? Day(edited, now, zone) : "";
    }

    /// <summary>The trailer's tooltip: the whole of what the trailer abbreviates.</summary>
    public static string Tip(ScriptUse? use, DateTimeOffset? modified, TimeZoneInfo zone)
    {
        if (use is not null)
        {
            var where = string.IsNullOrWhiteSpace(use.Database)
                ? use.ConnectionName
                : $"{use.ConnectionName} / {use.Database}";
            return $"Last run on {where}, {Exact(use.ExecutedAt, zone)}";
        }
        return modified is { } edited
            ? $"No run recorded for this script. Edited {Exact(edited, zone)}"
            : "";
    }

    /// <summary>One line of the server menu's recent scripts.</summary>
    public static string MenuLine(string name, ScriptUse use, DateTimeOffset now, TimeZoneInfo zone)
        => $"{name}  ·  {Trailer(use, null, now, zone)}";

    /// <summary>today / yesterday / <c>12 Nov</c> / <c>12 Nov 2025</c>, by the calendar of
    /// <paramref name="zone"/>. Fixed English month names, like the rest of the app's text.</summary>
    internal static string Day(DateTimeOffset when, DateTimeOffset now, TimeZoneInfo zone)
    {
        var date = TimeZoneInfo.ConvertTime(when, zone).Date;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        if (date == today) return "today";
        if (date == today.AddDays(-1)) return "yesterday";
        return date.ToString(date.Year == today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
    }

    private static string Exact(DateTimeOffset when, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTime(when, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
