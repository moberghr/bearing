using Bearing.App.ViewModels;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The Scripts filter's pure half (#47): what a name/content filter matches, and the line shown as the
/// reason it matched. No filesystem, no tree — that is the point of extracting it (§2.5).
/// </summary>
public class ScriptSearchTests
{
    [Fact]
    public void Name_matching_is_a_case_insensitive_substring()
    {
        Assert.True(ScriptSearch.MatchesName("Monthly-Settlements.sql", "settle"));
        Assert.True(ScriptSearch.MatchesName("monthly.sql", "MONTH"));
        Assert.False(ScriptSearch.MatchesName("monthly.sql", "settlements"));
        // Not fuzzy: a subsequence is not a match, or "mts" would hit half the folder.
        Assert.False(ScriptSearch.MatchesName("monthly.sql", "mts"));
    }

    [Fact]
    public void An_empty_filter_matches_every_name()
        => Assert.True(ScriptSearch.MatchesName("anything.sql", ""));

    [Fact]
    public void A_filter_under_three_characters_stays_name_only()
    {
        Assert.False(ScriptSearch.WantsContentSearch(""));
        Assert.False(ScriptSearch.WantsContentSearch("ab"));
        Assert.True(ScriptSearch.WantsContentSearch("abc"));
    }

    [Fact]
    public void MatchingLine_is_the_first_line_containing_the_filter()
    {
        var sql = "select *\nfrom settlements s\njoin settlements t on t.id = s.id\nwhere 1 = 1";
        Assert.Equal("from settlements s", ScriptSearch.MatchingLine(sql, "settlements"));
    }

    [Fact]
    public void MatchingLine_is_case_insensitive_and_keeps_the_text_as_written()
        => Assert.Equal("FROM Settlements", ScriptSearch.MatchingLine("select 1\nFROM Settlements", "settlements"));

    [Fact]
    public void MatchingLine_is_null_when_the_text_does_not_contain_the_filter()
    {
        Assert.Null(ScriptSearch.MatchingLine("select * from invoices", "settlements"));
        Assert.Null(ScriptSearch.MatchingLine("", "settlements"));
        Assert.Null(ScriptSearch.MatchingLine("select 1", ""));
    }

    [Fact]
    public void MatchingLine_handles_the_first_line_the_last_line_and_crlf()
    {
        Assert.Equal("select settlements", ScriptSearch.MatchingLine("select settlements\nfrom x", "settlements"));
        Assert.Equal("from settlements", ScriptSearch.MatchingLine("select *\r\nfrom settlements", "settlements"));
        Assert.Equal("settlements", ScriptSearch.MatchingLine("settlements", "settlements"));
    }

    [Fact]
    public void MatchingLine_collapses_indentation_so_the_snippet_is_not_blank()
        => Assert.Equal("and s.paid is null",
            ScriptSearch.MatchingLine("select *\n    \tand s.paid is null   \n", "paid"));

    [Fact]
    public void MatchingLine_caps_a_long_line_with_an_ellipsis()
    {
        var line = "select " + new string('x', 400) + " settlements";
        var shown = ScriptSearch.MatchingLine(line, "select")!;
        Assert.Equal(ScriptSearch.MaxMatchLineLength + 1, shown.Length); // + the ellipsis
        Assert.EndsWith("…", shown);
    }
}
