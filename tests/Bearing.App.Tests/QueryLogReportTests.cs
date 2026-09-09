using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Results;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The audit report's shaping (#113). Pure, so all of it is testable without a file or a window — and the
/// half that matters most is what the report <em>says about itself</em>: a report that implied it knew more
/// than the log recorded would be the one kind of wrong this feature cannot afford.
/// </summary>
public class QueryLogReportTests
{
    private static readonly Guid ProdId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StagingId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ConnectionInfo Conn(Guid id, string name, string? environment) => new()
    {
        Id = id,
        Name = name,
        ProviderId = "postgres",
        Environment = environment,
    };

    private static readonly ConnectionInfo[] Connections =
    [
        Conn(ProdId, "prod-eu", "Production"),
        Conn(StagingId, "staging", "Staging"),
    ];

    private static QueryLogEntry Entry(
        string sql,
        string connection = "prod-eu",
        Guid? id = null,
        string? environment = "Production",
        DateTimeOffset? at = null,
        bool ok = true) => new()
        {
            ExecutedAt = at ?? new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero),
            ProviderId = "postgres",
            ConnectionName = connection,
            ConnectionId = id,
            Environment = environment,
            Database = "pagila",
            SqlText = sql,
            Duration = TimeSpan.FromMilliseconds(42),
            RowCount = ok ? 3 : 0,
            Success = ok,
            ErrorMessage = ok ? null : "relation \"nope\" does not exist",
        };

    private static AuditReport Build(
        IReadOnlyList<QueryLogEntry> entries,
        QueryLogReportFilter? filter = null,
        bool redactionOn = false)
        => QueryLogReport.Build(entries, filter ?? new QueryLogReportFilter(), Connections, redactionOn);

    private static string Notes(AuditReport report) => string.Join("\n", report.Notes);

    private static IEnumerable<string> Column(AuditReport report, string name)
    {
        var index = report.Table.Columns.Select((c, i) => (c.Name, i)).First(x => x.Name == name).i;
        return report.Table.Rows.Select(r => r[index]?.ToString() ?? "");
    }

    // ---- the table ------------------------------------------------------------------------------------

    [Fact]
    public void Every_execution_becomes_a_row_with_the_columns_an_audit_asks_for()
    {
        var report = Build([Entry("select 1"), Entry("delete from rental where rental_id = 1", ok: false)]);

        Assert.Equal(
            ["executed_at", "connection", "environment", "database", "statement",
             "duration_ms", "rows", "outcome", "error", "script"],
            report.Table.Columns.Select(c => c.Name));
        Assert.Equal(2, report.Table.Rows.Count);
        Assert.Equal(["ok", "error"], Column(report, "outcome"));
        Assert.Equal(["", "relation \"nope\" does not exist"], Column(report, "error"));
    }

    [Fact]
    public void The_timestamp_keeps_its_offset()
    {
        // "What ran at 3am" is a question about a moment, and a stamp with no offset cannot answer it for
        // anyone reading the report in another zone.
        var report = Build([Entry("select 1", at: new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.FromHours(2)))]);

        Assert.Equal("2026-09-03 10:00:00 +02:00", Column(report, "executed_at").Single());
    }

    // ---- environment resolution ----------------------------------------------------------------------

    [Fact]
    public void The_recorded_environment_wins_over_the_connection_s_current_one()
    {
        // The connection has since been re-pointed at staging. Last week's statement still ran against
        // production, and a report that said otherwise would rewrite history.
        var report = Build([Entry("select 1", connection: "staging", id: StagingId, environment: "Production")]);

        Assert.Equal("Production", Column(report, "environment").Single());
    }

    [Fact]
    public void A_row_recorded_before_the_column_existed_falls_back_to_the_connection_by_id()
    {
        var report = Build([Entry("select 1", connection: "renamed-since", id: ProdId, environment: null)]);

        Assert.Equal("Production", Column(report, "environment").Single());
    }

    [Fact]
    public void A_row_with_neither_an_environment_nor_a_known_id_reports_no_environment()
    {
        // Not "Production" guessed from a name that happens to match — §1.1's rule against asserting what
        // nobody checked. Blank, and the notes say how many such rows there are.
        var report = Build([Entry("select 1", connection: "prod-eu", id: null, environment: null)]);

        Assert.Equal("", Column(report, "environment").Single());
        Assert.Contains("recorded before the log stored a connection id", Notes(report));
    }

    [Fact]
    public void A_deleted_connection_still_reports_the_environment_it_ran_under()
    {
        var gone = Guid.NewGuid();
        var report = Build([Entry("select 1", connection: "deleted", id: gone, environment: "Production")]);

        Assert.Equal("deleted", Column(report, "connection").Single());
        Assert.Equal("Production", Column(report, "environment").Single());
    }

    // ---- filters --------------------------------------------------------------------------------------

    [Fact]
    public void Filtering_by_connection_keeps_only_that_connection()
    {
        var report = Build(
            [Entry("select 1", connection: "prod-eu"), Entry("select 2", connection: "staging")],
            new QueryLogReportFilter(ConnectionNames: ["staging"]));

        Assert.Equal(["select 2"], Column(report, "statement"));
    }

    /// <summary>
    /// Rows logged under a connection's <em>previous</em> name still count as that connection's. The dialog
    /// offers current names; matching by recorded name alone silently dropped everything a connection ran
    /// before it was renamed — for "what ran against production last week", the wrong answer with no sign
    /// that it is wrong.
    /// </summary>
    [Fact]
    public void Filtering_by_connection_finds_rows_logged_under_its_old_name()
    {
        // ProdId is now called "prod-eu"; this row was logged while it was called "prod".
        var report = Build(
            [Entry("select 1", connection: "prod", id: ProdId), Entry("select 2", connection: "staging", id: StagingId)],
            new QueryLogReportFilter(ConnectionNames: ["prod-eu"]));

        Assert.Equal(["select 1"], Column(report, "statement"));
    }

    [Fact]
    public void A_reused_name_does_not_pull_in_another_connection_s_old_rows_by_id()
    {
        // The name match is still there for rows with no id at all (pre-v2), and it is by *recorded* name —
        // so a row another connection logged under a name that is now ticked still matches by name, which
        // is the honest reading of "the log says prod-eu". What must not happen is an id pulling in a
        // connection that is not ticked.
        var report = Build(
            [Entry("select 1", connection: "old", id: StagingId)],
            new QueryLogReportFilter(ConnectionNames: ["prod-eu"]));

        Assert.Empty(report.Table.Rows);
    }

    [Fact]
    public void Filtering_by_environment_matches_case_insensitively()
    {
        var report = Build(
            [Entry("select 1", environment: "Production"), Entry("select 2", environment: "Staging")],
            new QueryLogReportFilter(Environments: ["production"]));

        Assert.Equal(["select 1"], Column(report, "statement"));
    }

    [Fact]
    public void An_empty_filter_list_means_everything_rather_than_nothing()
    {
        var report = Build(
            [Entry("select 1", connection: "prod-eu"), Entry("select 2", connection: "staging")],
            new QueryLogReportFilter(ConnectionNames: [], Environments: []));

        Assert.Equal(2, report.Table.Rows.Count);
    }

    /// <summary>
    /// The writes-only filter is a re-read of each statement rather than a stored verdict — which is what
    /// makes it work over history recorded before the filter existed.
    /// </summary>
    [Fact]
    public void Writes_only_keeps_what_the_write_guard_flags()
    {
        var report = Build(
            [
                Entry("select * from rental"),
                Entry("delete from rental where rental_id = 1"),
                Entry("with gone as (delete from rental returning *) select count(*) from gone"),
                Entry("update payment set amount = 1 where payment_id = 2"),
            ],
            new QueryLogReportFilter(WritesOnly: true));

        Assert.Equal(3, report.Table.Rows.Count);
        Assert.DoesNotContain("select * from rental", Column(report, "statement"));
        // The data-modifying CTE is a write, and the whole reason the classification is lexer-based.
        Assert.Contains(Column(report, "statement"), s => s.Contains("with gone as"));
        Assert.Contains("write guard", Notes(report));
    }

    [Fact]
    public void The_date_range_is_applied_even_though_the_store_already_narrowed_it()
    {
        // Belt and braces on purpose: the filter travels with the report, and a caller that read a wider
        // window than it asked for must not silently widen the report too.
        var day = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var report = Build(
            [Entry("inside", at: day), Entry("before", at: day.AddDays(-3)), Entry("after", at: day.AddDays(3))],
            new QueryLogReportFilter(From: day.AddHours(-1), To: day.AddHours(1)));

        Assert.Equal(["inside"], Column(report, "statement"));
    }

    // ---- the notes ------------------------------------------------------------------------------------

    [Fact]
    public void The_notes_say_there_is_only_one_user_and_why_there_is_no_user_column()
    {
        var report = Build([Entry("select 1")]);

        Assert.DoesNotContain("user", report.Table.Columns.Select(c => c.Name));
        Assert.Contains("run by the person using this Bearing installation", Notes(report));
    }

    [Fact]
    public void The_notes_report_the_redaction_setting_in_both_directions()
    {
        // §1.3. Neither state may be stated as a property of the rows: the setting is a property of when a
        // row was written, and the report cannot tell which rows were written under which.
        var on = Notes(Build([Entry("select 1")], redactionOn: true));
        Assert.Contains("currently ON", on);
        Assert.Contains("cannot be recovered", on);
        Assert.Contains("recorded while the setting was off", on);

        var off = Notes(Build([Entry("select 1")], redactionOn: false));
        Assert.Contains("currently OFF", off);
        Assert.Contains("remain redacted", off);
    }

    [Fact]
    public void The_notes_report_the_requested_period_even_when_nothing_ran_in_it()
    {
        // "Nothing ran against production that week" is itself the answer to an audit question, so the
        // period has to survive an empty result rather than collapsing to "no executions".
        var from = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 9, 7, 23, 59, 59, TimeSpan.Zero);
        var report = Build([], new QueryLogReportFilter(From: from, To: to));

        Assert.Empty(report.Table.Rows);
        Assert.Contains("2026-09-01 00:00:00 +00:00 to 2026-09-07 23:59:59 +00:00", Notes(report));
        Assert.Contains("Executions: 0", Notes(report));
    }

    [Fact]
    public void An_open_bound_is_named_rather_than_left_blank()
    {
        var report = Build([Entry("select 1")],
            new QueryLogReportFilter(To: new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)));

        Assert.Contains("the beginning of the log to 2026-09-07", Notes(report));
    }

    [Fact]
    public void With_no_range_the_notes_report_what_the_log_actually_spans()
    {
        var report = Build(
        [
            Entry("select 1", at: new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero)),
            Entry("select 2", at: new DateTimeOffset(2026, 8, 30, 8, 0, 0, TimeSpan.Zero)),
        ]);

        Assert.Contains("2026-08-30 08:00:00 +00:00 to 2026-09-03 10:00:00 +00:00 (whole log)", Notes(report));
    }

    [Fact]
    public void The_note_about_missing_ids_counts_only_the_rows_that_survived_the_filter()
    {
        // Counted after filtering, or the report would warn about rows it does not contain.
        var report = Build(
            [
                Entry("select 1", connection: "prod-eu", id: null, environment: null),
                Entry("select 2", connection: "staging", id: StagingId, environment: "Staging"),
            ],
            new QueryLogReportFilter(ConnectionNames: ["staging"]));

        Assert.DoesNotContain("recorded before the log stored a connection id", Notes(report));
    }

    // ---- period boundaries ----------------------------------------------------------------------------

    /// <summary>
    /// The end bound covers the whole of its day. A range of "the 1st to the 7th" whose upper bound was the
    /// 7th at midnight would silently exclude everything run on the 7th.
    /// </summary>
    [Fact]
    public void The_end_of_a_day_is_the_end_of_that_day()
    {
        var day = new DateTimeOffset(2026, 9, 7, 15, 42, 0, TimeSpan.FromHours(2));
        var offset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 7));

        Assert.Equal(new DateTimeOffset(2026, 9, 7, 0, 0, 0, offset), ReportPeriod.StartOfDay(day));
        var end = ReportPeriod.EndOfDay(day)!.Value;
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 23, 59, 59, offset).AddTicks(9_999_999), end);
        Assert.Equal(7, end.Day);

        Assert.Null(ReportPeriod.StartOfDay(null));
        Assert.Null(ReportPeriod.EndOfDay(null));
    }

    /// <summary>
    /// The bound uses the local zone's offset <em>for the picked day</em>, not the offset the picker value
    /// arrived with. Avalonia's date picker keeps the seed value's offset while the user scrolls to another
    /// month, so a January day picked in September carried a summer offset — and the report's edges sat an
    /// hour off local midnight. Asserted only where the local zone observes DST, since that is the only place
    /// the two offsets differ.
    /// </summary>
    [SkippableFact]
    public void A_day_picked_across_a_dst_boundary_is_bounded_at_its_own_midnight()
    {
        var winter = new DateTime(2026, 1, 5);
        var summer = new DateTime(2026, 7, 5);
        var winterOffset = TimeZoneInfo.Local.GetUtcOffset(winter);
        var summerOffset = TimeZoneInfo.Local.GetUtcOffset(summer);
        Skip.If(winterOffset == summerOffset, "the local zone has no DST, so the two readings agree");

        // A January day carrying July's offset — what the picker hands over when seeded in summer.
        var picked = new DateTimeOffset(winter, summerOffset);

        var start = ReportPeriod.StartOfDay(picked)!.Value;
        Assert.Equal(winterOffset, start.Offset);
        Assert.Equal(new DateTime(2026, 1, 5, 0, 0, 0), start.DateTime);
    }

    [Fact]
    public void An_inverted_range_is_put_in_order_rather_than_matching_nothing()
    {
        // "From the 8th to the 1st" means the week. A range that matched nothing was reported as "nothing
        // ran in that period" — a false negative handed to an auditor.
        var a = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var b = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal((a, b), ReportPeriod.Ordered(b, a));
        Assert.Equal((a, b), ReportPeriod.Ordered(a, b));
        // An open bound has nothing to be inverted against.
        Assert.Equal((null, b), ReportPeriod.Ordered(null, b));
        Assert.Equal((a, null), ReportPeriod.Ordered(a, null));
    }

    [Fact]
    public void A_statement_run_late_on_the_last_day_is_inside_the_range()
    {
        // In the local zone, because that is what "the 7th" means to the person picking it — a day bound is
        // local midnight, and a statement run at 23:58 local on the 7th is inside it.
        var local = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 7));
        var lastDay = new DateTimeOffset(2026, 9, 7, 0, 0, 0, local);
        var lateThatNight = new DateTimeOffset(2026, 9, 7, 23, 58, 0, local);

        var report = Build([Entry("select 'late'", at: lateThatNight)],
            new QueryLogReportFilter(
                From: ReportPeriod.StartOfDay(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)),
                To: ReportPeriod.EndOfDay(lastDay)));

        Assert.Equal(["select 'late'"], Column(report, "statement"));
    }

    [Fact]
    public void The_suggested_file_name_is_dated_so_successive_exports_do_not_overwrite()
    {
        var name = QueryLogReport.SuggestedName(new DateTime(2026, 9, 8, 14, 5, 3), ExportFormat.Xlsx);
        Assert.Equal("bearing-audit-20260908-140503.xlsx", name);
        Assert.EndsWith(".csv", QueryLogReport.SuggestedName(DateTime.Now, ExportFormat.Csv));
    }
}
