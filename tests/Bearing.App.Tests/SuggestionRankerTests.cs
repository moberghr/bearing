using System.Collections.Generic;
using System.Linq;
using Bearing.App.Completion;
using Bearing.Core.Completion;
using Xunit;

namespace Bearing.App.Tests;

public class SuggestionRankerTests
{
    private static Suggestion Table(string name) => new()
    {
        DisplayText = name,
        FilterText = name,
        ReplacementText = name,
        Kind = SuggestionKind.Table,
        Priority = 10,
    };

    private static Suggestion Column(string name) => new()
    {
        DisplayText = name,
        FilterText = name,
        ReplacementText = name,
        Kind = SuggestionKind.Column,
        Priority = 8,
    };

    private static Suggestion Keyword(string word) => new()
    {
        DisplayText = word,
        ReplacementText = word,
        Kind = SuggestionKind.Keyword,
        Priority = 1,
    };

    private static string[] Names(IEnumerable<Suggestion> s) => s.Select(x => x.DisplayText).ToArray();

    [Fact]
    public void Initials_reach_a_snake_case_table()
    {
        var ranked = SuggestionRanker.Rank(new[] { Table("accounting_lines"), Table("users") }, "al");
        Assert.Equal(new[] { "accounting_lines" }, Names(ranked));
    }

    [Fact]
    public void A_scattered_subsequence_still_reaches_it()
        => Assert.Equal(new[] { "accounting_lines" },
            Names(SuggestionRanker.Rank(new[] { Table("accounting_lines"), Table("users") }, "accli")));

    [Fact]
    public void A_near_literal_keyword_hit_outranks_a_scattered_table_hit()
    {
        // Quality decides before Priority does, so WHERE stays reachable while typing "wh".
        var ranked = SuggestionRanker.Rank(new[] { Table("warehouse_shipments"), Keyword("WHERE") }, "whe");
        Assert.Equal(new[] { "WHERE", "warehouse_shipments" }, Names(ranked));
    }

    [Fact]
    public void At_equal_quality_the_engine_priority_wins()
    {
        // Both are prefix hits: the table (10) sorts above the keyword (1), and the column (8) between.
        var ranked = SuggestionRanker.Rank(new[] { Keyword("ORDER"), Table("orders"), Column("order_id") }, "or");
        Assert.Equal(new[] { "orders", "order_id", "ORDER" }, Names(ranked));
    }

    [Fact]
    public void Non_matching_suggestions_are_dropped()
        => Assert.Empty(SuggestionRanker.Rank(new[] { Table("users"), Keyword("SELECT") }, "zzq"));

    [Fact]
    public void An_empty_query_keeps_the_engine_order_untouched()
    {
        var input = new[] { Keyword("SELECT"), Table("users") };
        Assert.Same(input, SuggestionRanker.Rank(input, ""));
    }

    [Fact]
    public void A_span_that_stopped_being_a_name_ends_completion()
    {
        // Ctrl+Space then space: the span holds " ", which trimmed to an empty query and kept the whole
        // schema on screen (and the popup alive) instead of closing.
        var input = new[] { Table("users"), Keyword("SELECT") };
        Assert.Empty(SuggestionRanker.Rank(input, " "));
        Assert.Empty(SuggestionRanker.Rank(input, "users, "));
        Assert.Empty(SuggestionRanker.Rank(input, "u("));
        Assert.False(SuggestionRanker.IsNameFragment(" "));
        Assert.True(SuggestionRanker.IsNameFragment(""));
        Assert.True(SuggestionRanker.IsNameFragment("\"__Mig"));
    }

    [Fact]
    public void A_partially_typed_quoted_identifier_matches_the_bare_name()
    {
        // The list shows bare names, so "__Mig — quote and all — has to match __MigrationHistory.
        var ranked = SuggestionRanker.Rank(new[] { Table("__MigrationHistory"), Table("users") }, "\"__Mig");
        Assert.Equal(new[] { "__MigrationHistory" }, Names(ranked));
    }

    [Fact]
    public void Filtering_matches_FilterText_not_the_inserted_text()
    {
        // A join snippet inserts "orders o on …" but is filtered by the joined table's name.
        var join = new Suggestion
        {
            DisplayText = "orders",
            FilterText = "orders",
            ReplacementText = "orders o on o.user_id = u.id",
            Kind = SuggestionKind.Join,
            Priority = 20,
        };
        Assert.Equal(new[] { "orders" }, Names(SuggestionRanker.Rank(new[] { join }, "ord")));
        Assert.Empty(SuggestionRanker.Rank(new[] { join }, "user"));
    }
}
