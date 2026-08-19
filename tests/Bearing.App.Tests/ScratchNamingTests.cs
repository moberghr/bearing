using System;
using System.IO;
using Bearing.App.Workspace;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>Dated scratch filenames and scratch-folder membership — the two pure pieces of Phase 2.</summary>
public class ScratchNamingTests
{
    private static readonly DateOnly Day = new(2026, 8, 6);

    [Fact]
    public void First_file_of_the_day_is_number_one()
        => Assert.Equal("2026-08-06-01.sql", ScratchNaming.NextFileName(Day, Array.Empty<string>()));

    [Fact]
    public void Numbering_continues_past_existing_files()
    {
        var next = ScratchNaming.NextFileName(Day, new[] { "2026-08-06-01.sql", "2026-08-06-02.sql" });
        Assert.Equal("2026-08-06-03.sql", next);
    }

    [Fact]
    public void Gaps_are_reused_so_deleting_a_file_frees_its_number()
    {
        var next = ScratchNaming.NextFileName(Day, new[] { "2026-08-06-01.sql", "2026-08-06-03.sql" });
        Assert.Equal("2026-08-06-02.sql", next);
    }

    [Fact]
    public void Another_days_files_dont_consume_todays_numbers()
    {
        var next = ScratchNaming.NextFileName(Day, new[] { "2026-08-05-01.sql", "2026-08-05-02.sql" });
        Assert.Equal("2026-08-06-01.sql", next);
    }

    [Fact]
    public void Existing_names_match_case_insensitively()
        => Assert.Equal("2026-08-06-02.sql", ScratchNaming.NextFileName(Day, new[] { "2026-08-06-01.SQL" }));

    [Fact]
    public void Names_sort_chronologically_as_plain_strings()
    {
        var earlier = ScratchNaming.NextFileName(new DateOnly(2026, 8, 5), Array.Empty<string>());
        var later = ScratchNaming.NextFileName(new DateOnly(2026, 12, 1), Array.Empty<string>());
        Assert.True(string.CompareOrdinal(earlier, later) < 0);  // the tree sorts by name; date order must fall out
    }

    // ---- folder membership ----

    [Theory]
    [InlineData("/p/scripts/scratch/a.sql", true)]
    [InlineData("/p/scripts/scratch/nested/a.sql", true)]
    [InlineData("/p/scripts/a.sql", false)]
    [InlineData("/p/scripts/scratchpad/a.sql", false)]   // prefix match must not count
    [InlineData("/p/scripts/scratch", false)]            // the folder itself is not a file under it
    public void Membership_is_by_folder_not_by_name(string path, bool expected)
        => Assert.Equal(expected, ScratchNaming.IsUnderScratch(Norm(path), Norm("/p/scripts/scratch")));

    [Fact]
    public void Null_path_or_directory_is_never_scratch()
    {
        Assert.False(ScratchNaming.IsUnderScratch(null, Norm("/p/scripts/scratch")));
        Assert.False(ScratchNaming.IsUnderScratch(Norm("/p/scripts/scratch/a.sql"), null));
    }

    [Fact]
    public void Trailing_separator_on_the_directory_is_tolerated()
        => Assert.True(ScratchNaming.IsUnderScratch(Norm("/p/scripts/scratch/a.sql"), Norm("/p/scripts/scratch") + Path.DirectorySeparatorChar));

    // ---- naming the file after a label the user typed ----

    [Fact]
    public void A_user_label_becomes_a_sql_file_name()
        => Assert.Equal("morning check.sql", ScratchNaming.NamedFileName("morning check", Array.Empty<string>()));

    [Fact]
    public void A_label_that_already_ends_in_sql_is_not_doubled_up()
        => Assert.Equal("report.sql", ScratchNaming.NamedFileName("report.sql", Array.Empty<string>()));

    [Fact]
    public void A_taken_name_is_refused_so_the_caller_can_fall_back()
        => Assert.Null(ScratchNaming.NamedFileName("report", new[] { "REPORT.SQL" }));

    [Fact]
    public void Characters_a_filesystem_wont_take_are_stripped()
        => Assert.Equal("abc.sql", ScratchNaming.NamedFileName("a/b\0c", Array.Empty<string>()));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("//")]
    public void A_label_with_no_usable_characters_is_refused(string label)
        => Assert.Null(ScratchNaming.NamedFileName(label, Array.Empty<string>()));

    // ---- placeholder labels ----

    [Theory]
    [InlineData("Scratch 1", true)]
    [InlineData("Scratch 42", true)]
    [InlineData("Scratch", false)]
    [InlineData("Scratch ", false)]
    [InlineData("Scratch pad", false)]
    [InlineData("scratch 1", false)]     // the generated form is capitalised
    [InlineData("My Scratch 1", false)]
    [InlineData(null, false)]
    public void Generated_labels_are_told_apart_from_typed_ones(string? label, bool expected)
        => Assert.Equal(expected, ScratchNaming.IsGeneratedLabel(label));

    /// <summary>Make a posix-style test path usable on the host OS.</summary>
    private static string Norm(string p) => Path.GetFullPath(p.Replace('/', Path.DirectorySeparatorChar));
}
