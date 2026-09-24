using System;
using System.Collections.Generic;
using System.Linq;

namespace Bearing.Core.Data;

/// <summary>
/// Which zone a session computes in (#163): what <see cref="ConnectionInfo.SessionTimeZone"/> means, and the
/// zone id it resolves to. Pure, so the precedence and the Windows → IANA conversion are testable without a
/// server (§2.5). The provider decides how the answer reaches the server —
/// <c>PostgresConnectionString.Build</c> for Postgres — the split <see cref="SessionPolicy"/> makes.
/// <para>
/// <b>Not a display setting.</b> <c>results.displayTimeZone</c> (#77) decides how a <c>timestamptz</c> the
/// server already produced is <i>rendered</i>; it cannot change a value. This decides what the server
/// <i>computes</i>: <c>timestamptz::timestamp</c>, <c>::date</c>, <c>date_trunc('day', …)</c>, mixing
/// <c>timestamp</c> with <c>timestamptz</c>. Left unset, a session keeps the server's own zone — UTC on RDS —
/// while pgJDBC (and so DBeaver) sends the client's, so the same query returned numbers two hours apart in
/// the two tools, and the grid could show local time above SQL that ran in UTC.
/// </para>
/// </summary>
public static class SessionTimeZonePolicy
{
    /// <summary>The setting value for this machine's zone — and what an absent setting means.</summary>
    public const string Local = "local";

    /// <summary>The setting value for "send nothing": the session keeps the server's configured zone (or
    /// whatever <c>PGTZ</c> says, which the driver still honours — the same as libpq).</summary>
    public const string Server = "server";

    /// <summary>The Npgsql options-bag key that set this before it had a field — the only route there was.
    /// Read while the field is untouched, like <see cref="TlsPolicy.LegacyOptionKey"/>, so a connection that
    /// used it keeps its zone rather than being moved to this machine's on upgrade.</summary>
    public const string LegacyOptionKey = "timezone";

    /// <summary>The setting's text in force: the field, else a legacy bag entry, else <see cref="Local"/>.</summary>
    public static string Setting(ConnectionInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.SessionTimeZone)) return info.SessionTimeZone.Trim();
        return FromOptions(info.Options) ?? Local;
    }

    /// <summary>A legacy bag entry, or null when there is none.</summary>
    public static string? FromOptions(IReadOnlyDictionary<string, string> options)
    {
        var key = options.Keys.FirstOrDefault(k => string.Equals(k, LegacyOptionKey, StringComparison.OrdinalIgnoreCase));
        return key is not null && !string.IsNullOrWhiteSpace(options[key]) ? options[key].Trim() : null;
    }

    public static bool IsLocal(string setting) => setting.Equals(Local, StringComparison.OrdinalIgnoreCase);

    public static bool IsServer(string setting) => setting.Equals(Server, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The zone to ask the server for, or null to ask for nothing and keep its own.
    /// <para>
    /// Null for <see cref="Local"/> too when this machine's zone has no IANA name — a Linux box with no
    /// <c>/etc/localtime</c>, or no ICU to map a Windows id. Sending the Windows id would make the server
    /// refuse the connection outright (Postgres knows <c>Europe/Zagreb</c>, not
    /// <c>Central Europe Standard Time</c>), and inventing a fixed offset would be wrong half the year.
    /// <see cref="Describe"/> says so rather than letting it pass as the server's zone by choice.
    /// </para>
    /// </summary>
    public static string? ZoneFor(ConnectionInfo info) => ZoneFor(Setting(info));

    /// <inheritdoc cref="ZoneFor(ConnectionInfo)"/>
    public static string? ZoneFor(string setting)
    {
        if (IsServer(setting)) return null;
        if (IsLocal(setting)) return LocalIanaId();
        // An explicit zone. A Windows id — typed, or picked from this machine's list on Windows — is converted,
        // since that is the one spelling the server certainly cannot read. Anything else is passed through as
        // written: Postgres knows spellings .NET does not (POSIX `UTC+2`, abbreviations), so refusing what
        // TimeZoneInfo cannot resolve would refuse zones the server accepts. A genuine typo fails the connect
        // with the server's own "invalid value for parameter TimeZone", which is visible — silently computing
        // in some other zone instead would not be.
        // An IANA id is left alone even when it is also a Windows one: "UTC" is both, and converting it would
        // send "Etc/UTC" for a zone the user typed as UTC.
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(setting, out _)) return setting;
        return FromWindowsId(setting, CurrentRegion()) ?? setting;
    }

    /// <summary>This machine's zone as an IANA id, or null when it has none that can be named.</summary>
    public static string? LocalIanaId() => IanaIdOf(TimeZoneInfo.Local, CurrentRegion());

    /// <summary>The IANA id of a zone, or null. Separate from <see cref="LocalIanaId"/> so the conversion is
    /// testable on a zone the test names, rather than on whatever the machine running it is set to.</summary>
    public static string? IanaIdOf(TimeZoneInfo zone, string? region = null)
    {
        // "Local" is what .NET calls a zone it read from a TZ file it could not name; it is not an id.
        if (zone.Id.Equals("Local", StringComparison.OrdinalIgnoreCase)) return null;
        if (zone.HasIanaId) return zone.Id;
        return FromWindowsId(zone.Id, region);
    }

    /// <summary>
    /// A Windows zone id as IANA, preferring the name for <paramref name="region"/>.
    /// <para>
    /// The region is not a nicety. A Windows zone covers several IANA zones that agree <i>today</i> and not
    /// historically: "Central European Standard Time" is Warsaw by default and Zagreb for HR, and Poland kept
    /// summer time in the late 1970s when Yugoslavia did not. Sending Warsaw from a Zagreb machine moves
    /// <c>::date</c> on old rows off what DBeaver — which sends the JVM's own IANA id — computes, which is
    /// exactly #163's mismatch in a smaller place. A region that zone does not cover falls back to the
    /// default mapping.
    /// </para>
    /// </summary>
    public static string? FromWindowsId(string windowsId, string? region)
    {
        if (!string.IsNullOrEmpty(region)
            && TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, region, out var regional))
            return regional;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, out var iana) ? iana : null;
    }

    /// <summary>This machine's region as a two-letter code, or null when none can be read.</summary>
    private static string? CurrentRegion()
    {
        try { return System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// How the zone reads in a sentence — the status tip and the dialog's hint. Every mode says where the
    /// answer came from, because "UTC" as a choice and "UTC" because the server said so are different facts,
    /// and only one of them survives the connection being pointed at another server.
    /// </summary>
    public static string Describe(ConnectionInfo info) => Describe(Setting(info));

    /// <inheritdoc cref="Describe(ConnectionInfo)"/>
    public static string Describe(string setting)
    {
        if (IsServer(setting)) return "the server's own zone";
        if (IsLocal(setting))
            return LocalIanaId() is { } local
                ? $"{local} (this machine)"
                : "the server's own zone — this machine's zone has no name the server would accept";
        return ZoneFor(setting)!;
    }

    /// <summary>
    /// The dialog's hint under the setting: which zone the SQL will run in, and — for a zone typed by hand —
    /// what is actually sent and whether this machine recognises it. Never empty, unlike the safety note:
    /// every connection computes in <i>some</i> zone, and saying which is the point.
    /// </summary>
    public static string Advice(string setting)
    {
        var text = $"Queries compute in {Describe(setting)} — it decides what ::date, ::timestamp and "
                   + "date_trunc return. How the grid shows timestamps is a separate setting.";
        if (IsLocal(setting) || IsServer(setting)) return text;

        if (ZoneFor(setting) is { } sent && !sent.Equals(setting, StringComparison.Ordinal))
            text += $" “{setting}” is sent as {sent}, the name the server reads.";
        else if (!TimeZoneInfo.TryFindSystemTimeZoneById(setting, out _))
            text += " This machine doesn't recognise that zone. The server may; if it doesn't, connecting "
                    + "fails with the server's own error rather than running in another zone.";
        return text;
    }
}
