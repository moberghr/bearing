using System;
using System.Linq;
using Bearing.App.Formatting;
using Bearing.App.Results;
using Bearing.Core.Workspace;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Timestamps and zones (#77). A timestamp cell rendered <c>2026-08-26 12:15:00.372958</c> with nothing
/// saying what zone that was, and there was no way to see results in a chosen one.
/// <para>
/// The mappings this relies on were confirmed against Npgsql 10 rather than assumed: <c>timestamptz</c>
/// arrives as a <c>DateTime</c> with <c>Kind = Utc</c>, <c>timestamp</c> as <c>Kind = Unspecified</c>, and
/// <c>timetz</c> as a <c>DateTimeOffset</c>. <c>Kind</c> is therefore the discriminator, and the instant is
/// always UTC on arrival — which is what makes converting for display safe.
/// </para>
/// </summary>
public class TimeZoneDisplayTests
{
    /// <summary>A fixed +03:00 zone, so the test does not depend on the machine's own.</summary>
    private static readonly TimeZoneInfo Plus3 =
        TimeZoneInfo.CreateCustomTimeZone("test/+3", TimeSpan.FromHours(3), "test +3", "test +3");

    private static readonly DateTime Utc3Pm =
        new(2026, 8, 26, 15, 0, 0, DateTimeKind.Utc);

    // ---- ask (1): always show the zone -----------------------------------------------------------

    [Fact]
    public void A_timestamptz_now_shows_its_offset()
    {
        // The reported problem: the value was unambiguous in the database and ambiguous on screen.
        Assert.Equal("2026-08-26 15:00:00+00:00", CellFormat.Display(Utc3Pm, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Sub_second_precision_survives_the_offset()
    {
        // Postgres stores microseconds; truncating them would mean copying a value that is not in the row.
        var value = new DateTime(2026, 8, 26, 12, 15, 0, DateTimeKind.Utc).AddTicks(3_729_580);
        Assert.Equal("2026-08-26 12:15:00.372958+00:00", CellFormat.Display(value, TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_timestamp_without_zone_gets_no_offset()
    {
        // It genuinely has none — Postgres stores none. Printing +00:00 beside a column of local wall times
        // would invent information, and it is the arm most likely to mislead.
        var wall = new DateTime(2026, 8, 26, 15, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal("2026-08-26 15:00:00", CellFormat.Display(wall, Plus3));
    }

    [Fact]
    public void A_timetz_keeps_the_offset_it_arrived_with()
    {
        // Npgsql maps timetz to DateTimeOffset, so the existing offset arm was never dead for this type —
        // only for timestamptz, which is what the issue reported.
        var value = new DateTimeOffset(2026, 8, 26, 15, 0, 0, TimeSpan.FromHours(3));

        Assert.EndsWith("+03:00", CellFormat.Display(value, Plus3));
    }

    // ---- ask (2): a display zone -----------------------------------------------------------------

    [Fact]
    public void A_display_zone_shifts_the_wall_time_and_says_so()
    {
        // The issue's own example: 3 PM UTC in a +03:00 display zone reads as 18:00+03:00.
        Assert.Equal("2026-08-26 18:00:00+03:00", CellFormat.Display(Utc3Pm, Plus3));
    }

    [Fact]
    public void The_display_zone_does_not_touch_a_zone_less_timestamp()
    {
        // Nothing to convert *from*: a wall time with no zone has no instant to re-express.
        var wall = new DateTime(2026, 8, 26, 15, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(CellFormat.Display(wall, TimeZoneInfo.Utc), CellFormat.Display(wall, Plus3));
    }

    [Fact]
    public void The_default_zone_keeps_every_existing_display_identical()
    {
        // Except for gaining the offset, which is ask (1). The numbers do not move on upgrade.
        Assert.Equal(TimeZoneInfo.Utc, CellFormat.Zone);
        Assert.StartsWith("2026-08-26 15:00:00", CellFormat.Display(Utc3Pm));
    }

    // ---- the round trip: this is the one that must not lose an hour ------------------------------

    [Fact]
    public void An_edit_of_a_converted_value_writes_back_the_original_instant()
    {
        // A UTC 15:00 shown as 18:00+03:00 and handed back has to be 15:00 UTC — not 18:00 UTC, and not
        // 18:00 with the offset dropped. The same class of bug as "9,5 read as 95", with a three-hour blast
        // radius instead of a tenfold one.
        var shown = CellFormat.Display(Utc3Pm, Plus3);

        Assert.True(CellFormat.TryParseDate(shown, typeof(DateTime), out var parsed, utcColumn: true, Plus3));

        var back = Assert.IsType<DateTime>(parsed);
        Assert.Equal(Utc3Pm, back);
        Assert.Equal(DateTimeKind.Utc, back.Kind);
    }

    [Fact]
    public void Typing_a_wall_time_with_no_offset_is_read_in_the_zone_the_user_is_looking_at()
    {
        // The lenient path the issue warns about: read as UTC, "18:00" typed into a +03:00 display would
        // silently move the row three hours. The user edits what they see.
        Assert.True(CellFormat.TryParseDate(
            "2026-08-26 18:00:00", typeof(DateTime), out var parsed, utcColumn: true, Plus3));

        Assert.Equal(Utc3Pm, parsed);
    }

    [Fact]
    public void An_explicit_offset_in_the_text_wins_over_the_display_zone()
    {
        // Someone pasting a value from elsewhere has stated the instant outright.
        Assert.True(CellFormat.TryParseDate(
            "2026-08-26 15:00:00+00:00", typeof(DateTime), out var parsed, utcColumn: true, Plus3));

        Assert.Equal(Utc3Pm, parsed);
    }

    [Fact]
    public void A_zone_less_column_is_parsed_exactly_as_typed()
    {
        // No conversion in either direction: a timestamp column's text is its value.
        Assert.True(CellFormat.TryParseDate(
            "2026-08-26 18:00:00", typeof(DateTime), out var parsed, utcColumn: false, Plus3));

        Assert.Equal(new DateTime(2026, 8, 26, 18, 0, 0), parsed);
    }

    [Fact]
    public void A_bare_date_is_not_mistaken_for_a_stated_offset()
    {
        // "2026-08-26" carries hyphens of its own; reading one as an offset sign would put the value in a
        // zone nobody chose.
        Assert.True(CellFormat.TryParseDate("2026-08-26", typeof(DateTime), out var parsed, utcColumn: true, Plus3));

        // Midnight in +03:00 is 21:00 the previous day in UTC — the wall-time reading, which is what a user
        // typing a bare date in a +03:00 display means.
        Assert.Equal(new DateTime(2026, 8, 25, 21, 0, 0, DateTimeKind.Utc), parsed);
    }

    [Fact]
    public void Every_displayed_instant_round_trips_through_every_offset()
    {
        // The property that matters, over the whole range of offsets rather than one example.
        foreach (var hours in new[] { -11, -5, 0, 3, 5, 13 })
        {
            var zone = TimeZoneInfo.CreateCustomTimeZone($"t{hours}", TimeSpan.FromHours(hours), "t", "t");
            var shown = CellFormat.Display(Utc3Pm, zone);

            Assert.True(CellFormat.TryParseDate(shown, typeof(DateTime), out var parsed, utcColumn: true, zone),
                $"'{shown}' did not parse back");
            Assert.Equal(Utc3Pm, parsed);
        }
    }

    // ---- exports ---------------------------------------------------------------------------------

    [Fact]
    public void The_clipboard_and_csv_carry_the_same_text_the_grid_showed()
    {
        // TableFormats goes through CellFormat.Display, so the display zone reaches Copy and CSV for free —
        // and it should: an export that disagreed with the app that produced it is the worse surprise, and
        // the offset is in the text, so a consumer reading it is not left guessing.
        var previous = CellFormat.Zone;
        try
        {
            CellFormat.Zone = Plus3;
            Assert.Equal("2026-08-26 18:00:00+03:00", CellFormat.Display(Utc3Pm));
        }
        finally { CellFormat.Zone = previous; }
    }

    [Fact]
    public void An_xlsx_timestamp_is_the_wall_time_the_user_saw()
    {
        // Excel has no offset type, so a zone-aware value has to become *some* wall time. The one on screen
        // is the only defensible choice — exporting 15:00 while the grid said 18:00 would make the file
        // disagree with the app.
        var previous = CellFormat.Zone;
        try
        {
            CellFormat.Zone = Plus3;
            var block = TableBlock.ForResult(Ui.ResultsHarness.SingleColumn(
                "at", "timestamptz", typeof(DateTime), primaryKey: false, Utc3Pm));
            using var stream = new System.IO.MemoryStream();
            XlsxWriter.Write(stream, block, "t");

            // The serial for 2026-08-26 18:00 local, not 15:00: a difference of an eighth of a day.
            var expected = (new DateTime(2026, 8, 26, 18, 0, 0) - new DateTime(1899, 12, 30)).TotalDays;
            Assert.Contains(expected.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                Zip(stream));
        }
        finally { CellFormat.Zone = previous; }
    }

    private static string Zip(System.IO.MemoryStream stream)
    {
        stream.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        using var reader = new System.IO.StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        return reader.ReadToEnd();
    }

    // ---- marking the zone-less column ------------------------------------------------------------

    [Theory]
    [InlineData("timestamp without time zone", true)]
    [InlineData("timestamp", true)]
    [InlineData("timestamp with time zone", false)]
    [InlineData("timestamptz", false)]
    [InlineData("date", false)]
    [InlineData("time without time zone", false)]
    public void A_column_knows_whether_it_carries_a_zone(string dataType, bool zoneLess)
    {
        // The absence of an offset is too weak a signal on its own: a bare 2026-08-26 12:15:00 reads as "the
        // zone got truncated", not as "this value has no zone and never did".
        Assert.Equal(zoneLess, ColumnKinds.IsTimestampWithoutZone(dataType));
        if (dataType.StartsWith("timestamp", StringComparison.Ordinal))
            Assert.Equal(!zoneLess, ColumnKinds.IsTimestampWithZone(dataType));
    }

    // ---- the zone resolver -----------------------------------------------------------------------

    [Fact]
    public void The_default_and_the_system_zone_both_resolve()
    {
        Assert.Equal(TimeZoneInfo.Utc, DisplayTimeZone.Resolve("UTC"));
        Assert.Equal(TimeZoneInfo.Utc, DisplayTimeZone.Resolve(""));
        Assert.Equal(TimeZoneInfo.Utc, DisplayTimeZone.Resolve(null));
        Assert.Equal(TimeZoneInfo.Local, DisplayTimeZone.Resolve("system"));
    }

    [Fact]
    public void An_unknown_zone_falls_back_to_utc_rather_than_guessing()
    {
        // A typo in a settings file must not shift every timestamp by an unpredictable amount, and UTC is the
        // one answer that is never wrong about the instant.
        Assert.Equal(TimeZoneInfo.Utc, DisplayTimeZone.Resolve("Mars/Olympus_Mons"));
        Assert.False(DisplayTimeZone.IsKnown("Mars/Olympus_Mons"));
    }

    [Fact]
    public void A_real_zone_id_resolves_and_is_offered()
    {
        // .NET accepts IANA ids on Windows too, so a settings file written on one platform resolves on the
        // other.
        var zone = DisplayTimeZone.Resolve("Europe/Zagreb");

        Assert.NotEqual(TimeZoneInfo.Utc, zone);
        Assert.True(DisplayTimeZone.IsKnown("Europe/Zagreb"));
        Assert.Contains("system", DisplayTimeZone.Available());
        Assert.Contains("UTC", DisplayTimeZone.Available());
    }

    [Fact]
    public void A_zone_reads_with_the_offset_it_is_on_now()
    {
        // "system" alone does not say which zone that is, or what it costs.
        var described = DisplayTimeZone.Describe("UTC");

        Assert.Contains("UTC", described);
        Assert.Contains("+00:00", described);
    }

    // ---- the setting -----------------------------------------------------------------------------

    [Fact]
    public void The_zone_is_a_described_setting()
    {
        var setting = Assert.IsType<StringSetting>(SettingsCatalog.Find("results.displayTimeZone"));

        Assert.Equal("UTC", setting.Get(new AppSettings()));
        Assert.Contains("timezone", setting.Keywords);
    }

    [Fact]
    public void An_unresolvable_zone_is_refused_rather_than_stored()
    {
        // A stored value that cannot resolve is a setting that silently does nothing.
        SettingsCatalog.TimeZoneValidator = DisplayTimeZone.IsKnown;
        try
        {
            var setting = (StringSetting)SettingsCatalog.Find("results.displayTimeZone")!;

            var kept = setting.Write(new AppSettings(), "Mars/Olympus_Mons");
            var taken = setting.Write(new AppSettings(), "system");

            Assert.Equal("UTC", kept.DisplayTimeZone);
            Assert.Equal("system", taken.DisplayTimeZone);
        }
        finally
        {
            SettingsCatalog.TimeZoneValidator = null;
        }
    }

    [Fact]
    public void With_no_validator_wired_any_value_is_taken()
    {
        // Core has no zone database of its own (§2.1), so an un-wired catalog must not reject everything.
        SettingsCatalog.TimeZoneValidator = null;
        var setting = (StringSetting)SettingsCatalog.Find("results.displayTimeZone")!;

        Assert.Equal("Europe/Zagreb", setting.Write(new AppSettings(), "Europe/Zagreb").DisplayTimeZone);
    }
}
