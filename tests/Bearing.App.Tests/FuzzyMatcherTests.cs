using Bearing.App.Input;
using Xunit;

namespace Bearing.App.Tests;

public class FuzzyMatcherTests
{
    private static FuzzyMatcher.Result M(string text, string query) => FuzzyMatcher.Match(text, query);

    [Fact]
    public void The_two_reported_cases_match_snake_case()
    {
        // "accounting_lines should be reachable from al, and from accli too"
        Assert.True(M("accounting_lines", "al").IsMatch);
        Assert.True(M("accounting_lines", "accli").IsMatch);
    }

    [Fact]
    public void Initials_of_a_pascal_case_name_match_and_score_as_initials()
    {
        // The gap the old scorer had: it lower-cased first, so AccountingLines earned no word-start
        // bonus from "al" and sank below noise.
        var match = M("AccountingLines", "al");
        Assert.Equal(MatchQuality.Initials, match.Quality);
        Assert.Equal(M("accounting_lines", "al").Score, match.Score);
    }

    [Fact]
    public void Camel_case_boundaries_count_as_word_starts()
        => Assert.Equal(MatchQuality.Initials, M("orderLineTotal", "olt").Quality);

    [Fact]
    public void Non_subsequence_queries_do_not_match()
    {
        Assert.False(M("accounting_lines", "zx").IsMatch);
        Assert.False(M("accounting_lines", "la").IsMatch);   // order matters
        Assert.Equal(MatchQuality.None, M("users", "q").Quality);
    }

    [Theory]
    [InlineData("where", "where", MatchQuality.Exact)]
    [InlineData("WHERE", "wh", MatchQuality.Prefix)]
    [InlineData("warehouse_shipments", "shi", MatchQuality.Substring)]
    [InlineData("warehouse_shipments", "ws", MatchQuality.Initials)]
    [InlineData("warehouse_shipments", "wrh", MatchQuality.Subsequence)]
    public void Quality_buckets_rank_literal_hits_above_scattered_ones(string text, string query, MatchQuality expected)
        => Assert.Equal(expected, M(text, query).Quality);

    [Fact]
    public void Contiguous_and_early_hits_score_higher_within_a_quality()
    {
        Assert.True(M("accounting_lines", "accli").Score > M("accounting_lines", "al").Score);
        Assert.True(M("order_lines", "ol").Score > M("total_order_lines", "ol").Score);
    }

    [Fact]
    public void An_empty_query_matches_everything()
        => Assert.True(M("anything", "").IsMatch);

    [Fact]
    public void Matching_ignores_case_in_both_directions()
    {
        Assert.Equal(MatchQuality.Exact, M("Users", "users").Quality);
        Assert.Equal(MatchQuality.Prefix, M("users", "US").Quality);
    }

    [Theory]
    [InlineData("accounting_lines", 0, true)]
    [InlineData("accounting_lines", 11, true)]   // after '_'
    [InlineData("accounting_lines", 1, false)]
    [InlineData("AccountingLines", 10, true)]    // lower → upper
    [InlineData("ACCOUNTING", 5, false)]         // inside an all-caps run
    public void Word_starts_read_separators_and_case_transitions(string text, int index, bool expected)
        => Assert.Equal(expected, FuzzyMatcher.IsWordStart(text, index));
}
