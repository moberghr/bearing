using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Bearing.Sql;

namespace Bearing.App.Results;

/// <summary>
/// What the audit export covers (#113). Every field narrows; all of them null/empty means "everything in the
/// log".
/// </summary>
/// <param name="From">Earliest execution to include, inclusive.</param>
/// <param name="To">Latest execution to include, inclusive.</param>
/// <param name="ConnectionNames">
/// Connections to include, by their <em>current</em> name — what the dialog offers. A row matches when its
/// recorded name is one of these <b>or</b> its recorded id belongs to a connection now called one of these.
/// Both are needed: rows written before <c>connection_id</c> existed have only a name, and rows written
/// before a rename have the old name but the right id. Matching by name alone silently dropped everything a
/// connection ran under its previous name, which for "what ran against production last week" is the wrong
/// answer with no sign that it is wrong.
/// </param>
/// <param name="Environments">Environment labels to include (matched case-insensitively).</param>
/// <param name="WritesOnly">Keep only statements the write guard flags as modifying data or schema.</param>
public sealed record QueryLogReportFilter(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    IReadOnlyList<string>? ConnectionNames = null,
    IReadOnlyList<string>? Environments = null,
    bool WritesOnly = false);

/// <summary>
/// What the user asked an audit export for: the filter, and the format to write it in. Lives here rather
/// than beside the dialog so the service boundary that carries it does not have to reach into
/// <c>Views</c> (§2.2).
/// </summary>
public sealed record AuditExportRequest(QueryLogReportFilter Filter, ExportFormat Format);

/// <summary>The report: the table to write, and the notes that have to travel with it.</summary>
/// <param name="Table">One row per execution, newest first.</param>
/// <param name="Notes">
/// What the table cannot say about itself — the period covered, whose activity it is, and whether the SQL in
/// it was stored redacted. Written alongside the rows rather than dropped, because each of them changes how
/// the report should be read.
/// </param>
public sealed record AuditReport(TableBlock Table, IReadOnlyList<string> Notes);

/// <summary>
/// The query log as an audit report — <i>what ran against which connection, when</i> (#113).
/// <para>
/// Pure, so the shaping is testable without a file or a window (§2.5): the store answers the date range and
/// the export writes the file, and everything in between — filtering, environment resolution, the honesty
/// notes — happens here.
/// </para>
/// </summary>
public static class QueryLogReport
{
    private static readonly ColumnDescriptor[] ReportColumns =
    [
        new("executed_at", "timestamptz", typeof(string)),
        new("connection", "text", typeof(string)),
        new("environment", "text", typeof(string)),
        new("database", "text", typeof(string)),
        new("statement", "text", typeof(string)),
        new("duration_ms", "bigint", typeof(long)),
        new("rows", "bigint", typeof(long)),
        new("outcome", "text", typeof(string)),
        new("error", "text", typeof(string)),
        new("script", "text", typeof(string)),
    ];

    /// <summary>
    /// Build the report over <paramref name="entries"/> (already narrowed by date at the store, since that
    /// is the one filter SQL can do without losing rows it cannot classify).
    /// </summary>
    /// <param name="connections">
    /// The connections as they are now, for resolving an environment the log did not record. Matched by id
    /// first and by name only as a fallback — see <see cref="ResolveEnvironment"/>.
    /// </param>
    /// <param name="redactionOn">
    /// Whether literal stripping is currently on (§1.3). Reported as the <em>current setting</em> and not as
    /// a property of the rows, because it is exactly that: a row written while it was off holds verbatim SQL
    /// forever, and one written while it was on cannot be un-redacted. The note says so in both directions.
    /// </param>
    public static AuditReport Build(
        IReadOnlyList<QueryLogEntry> entries,
        QueryLogReportFilter filter,
        IReadOnlyList<ConnectionInfo> connections,
        bool redactionOn)
    {
        var byId = connections
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.First());

        var rows = new List<object?[]>();
        var kept = new List<QueryLogEntry>();
        foreach (var entry in entries)
        {
            if (!Matches(entry, filter, byId)) continue;
            kept.Add(entry);
            rows.Add(
            [
                entry.ExecutedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                entry.ConnectionName,
                ResolveEnvironment(entry, byId) ?? "",
                entry.Database,
                entry.SqlText,
                (long)entry.Duration.TotalMilliseconds,
                entry.RowCount,
                entry.Success ? "ok" : "error",
                entry.ErrorMessage ?? "",
                entry.ScriptPath ?? "",
            ]);
        }

        return new AuditReport(new TableBlock(ReportColumns, rows), Notes(kept, filter, redactionOn));
    }

    private static bool Matches(
        QueryLogEntry entry, QueryLogReportFilter filter, IReadOnlyDictionary<Guid, ConnectionInfo> byId_)
    {
        if (filter.From is { } from && entry.ExecutedAt < from) return false;
        if (filter.To is { } to && entry.ExecutedAt > to) return false;

        if (filter.ConnectionNames is { Count: > 0 } names)
        {
            var byName = names.Contains(entry.ConnectionName, StringComparer.Ordinal);
            // The ids of the connections *currently* called one of the ticked names, so a row logged under
            // an earlier name still counts as that connection's.
            var byId = entry.ConnectionId is { } id
                && byId_.TryGetValue(id, out var connection)
                && names.Contains(connection.Name, StringComparer.Ordinal);
            if (!byName && !byId) return false;
        }

        if (filter.Environments is { Count: > 0 } environments)
        {
            var environment = ResolveEnvironment(entry, byId_);
            if (environment is null
                || !environments.Contains(environment, StringComparer.OrdinalIgnoreCase)) return false;
        }

        // Re-lexed rather than read from a stored verdict, because there is no stored verdict — and re-lexing
        // is what makes the filter work over history written before this feature existed. It is the same
        // scan the guard itself runs (§1.2), in the App layer because `Persistence` may not reference `Sql`
        // (§2.2).
        if (filter.WritesOnly && !WriteGuard.HasRisk(entry.SqlText)) return false;

        return true;
    }

    /// <summary>
    /// The environment for one row: what the log recorded, else the connection's current label.
    /// <para>
    /// Recorded first, and that ordering is the point — a connection re-pointed from production to staging
    /// must not rewrite what last week's statements ran against. The fallback exists only for rows written
    /// before the log carried the field, and it prefers the id over the name: a renamed connection still
    /// resolves, and a name reused by a different connection does not silently resolve to the wrong one.
    /// </para>
    /// </summary>
    public static string? ResolveEnvironment(
        QueryLogEntry entry, IReadOnlyDictionary<Guid, ConnectionInfo> byId)
    {
        if (!string.IsNullOrWhiteSpace(entry.Environment)) return entry.Environment;
        if (entry.ConnectionId is { } id && byId.TryGetValue(id, out var connection))
            return string.IsNullOrWhiteSpace(connection.Environment) ? null : connection.Environment;
        return null;
    }

    /// <summary>
    /// The notes. Each one is here because leaving it out would let the report be read as saying more than
    /// it does.
    /// </summary>
    private static List<string> Notes(
        IReadOnlyList<QueryLogEntry> kept, QueryLogReportFilter filter, bool redactionOn)
    {
        var notes = new List<string>
        {
            "Bearing query log — audit report",
            $"Generated {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}",
            $"Executions: {kept.Count.ToString("N0", CultureInfo.InvariantCulture)}",
            $"Period: {Period(kept, filter)}",
            // There is no other user, and a column of identical names would look like the start of one.
            "Every execution here was run by the person using this Bearing installation. The log records no "
            + "other user, and this report does not have a user column because there is nothing to put in it.",
        };

        if (filter.WritesOnly)
            notes.Add("Filtered to statements the write guard flags as modifying data or schema. The "
                    + "classification is made when the report is built, by re-reading each statement, so it "
                    + "covers history recorded before this filter existed.");

        // §1.3. The setting is a property of *when a row was written*, so neither state can be reported as a
        // property of the rows — and the redacted case must never imply the original is recoverable.
        notes.Add(redactionOn
            ? "Literal redaction is currently ON: statements recorded while it was on have their string and "
            + "numeric literals replaced with placeholders. What was replaced was never stored, so it cannot "
            + "be recovered — here or anywhere else. Rows recorded while the setting was off hold the "
            + "statement as written."
            : "Literal redaction is currently OFF: statements are recorded as written, values included. Rows "
            + "recorded while the setting was on remain redacted, and cannot be un-redacted.");

        var unidentified = kept.Count(e => e.ConnectionId is null);
        if (unidentified > 0)
            notes.Add($"{unidentified.ToString("N0", CultureInfo.InvariantCulture)} of these executions were "
                    + "recorded before the log stored a connection id. They are identified by the connection "
                    + "name they were run under, which a later rename would not have updated.");

        return notes;
    }

    private static string Period(IReadOnlyList<QueryLogEntry> kept, QueryLogReportFilter filter)
    {
        // The requested window when there is one, because "no executions between these dates" is itself the
        // answer to an audit question — and reporting the empty period as "n/a" would lose it.
        if (filter.From is { } from || filter.To is { } to)
            return $"{Bound(filter.From, "the beginning of the log")} to {Bound(filter.To, "now")}";
        if (kept.Count == 0) return "no executions recorded";
        return $"{Stamp(kept.Min(e => e.ExecutedAt))} to {Stamp(kept.Max(e => e.ExecutedAt))} (whole log)";
    }

    private static string Bound(DateTimeOffset? value, string open) => value is { } v ? Stamp(v) : open;

    private static string Stamp(DateTimeOffset value)
        => value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    /// <summary>A suggested file name for the report: dated, so successive exports do not overwrite.</summary>
    public static string SuggestedName(DateTime now, ExportFormat format)
        => $"bearing-audit-{now:yyyyMMdd-HHmmss}.{ResultExport.Extension(format)}";
}

/// <summary>
/// A day picked in a dialog turned into the instant range a report should cover (#113).
/// <para>
/// Pure because the end bound is the part that is easy to get wrong: the <em>end</em> of the chosen day, not
/// its midnight. A range of "the 1st to the 7th" whose upper bound was the 7th at 00:00 would silently
/// exclude everything run on the 7th — a report about a period nobody asked for, which is the one failure an
/// audit report must not have.
/// </para>
/// </summary>
public static class ReportPeriod
{
    /// <summary>
    /// Midnight at the start of <paramref name="day"/>, in the <b>local zone's offset for that day</b>.
    /// <para>
    /// Not the picker value's own offset. Avalonia's date picker keeps the offset of the value it was seeded
    /// with (today's) while the user scrolls to another month, so a January day picked in September carried
    /// +02:00 into a +01:00 date — and the report's bounds sat an hour off local midnight either side, taking
    /// in a statement run at 23:30 the night before and leaving out one run at 23:30 on the last day. The
    /// zone's offset <em>for that date</em> is what "midnight on the 5th" means.
    /// </para>
    /// </summary>
    /// <param name="zone">
    /// The zone "midnight" is measured in; the machine's own when omitted. A parameter only so this is
    /// testable: both CI runners are UTC, so a test that read <c>TimeZoneInfo.Local</c> skipped itself on
    /// the one machine that matters and the DST bug it guards had no cover where it was found.
    /// </param>
    public static DateTimeOffset? StartOfDay(DateTimeOffset? day, TimeZoneInfo? zone = null)
        => day is { } d ? Midnight(d.Date, zone) : null;

    /// <summary>The last instant of <paramref name="day"/>, so an inclusive upper bound covers all of it.</summary>
    /// <inheritdoc cref="StartOfDay" path="/param"/>
    public static DateTimeOffset? EndOfDay(DateTimeOffset? day, TimeZoneInfo? zone = null)
        => day is { } d ? Midnight(d.Date.AddDays(1), zone).AddTicks(-1) : null;

    private static DateTimeOffset Midnight(DateTime date, TimeZoneInfo? zone)
    {
        var wall = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, (zone ?? TimeZoneInfo.Local).GetUtcOffset(wall));
    }

    /// <summary>
    /// The two bounds in order. A user who sets "from the 8th to the 1st" meant the week either way, and a
    /// range that matches nothing would otherwise be reported as "nothing ran in that period" — a false
    /// negative handed to an auditor.
    /// </summary>
    public static (DateTimeOffset? From, DateTimeOffset? To) Ordered(DateTimeOffset? from, DateTimeOffset? to)
        => from is { } f && to is { } t && f > t ? (to, from) : (from, to);
}
