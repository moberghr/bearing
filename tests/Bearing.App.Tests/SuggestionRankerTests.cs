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

    // ---- keywords matched by accident ------------------------------------------------------------

    [Fact]
    public void A_keyword_the_query_is_merely_buried_inside_sinks_below_the_schema()
    {
        // "ete" is scattered through "delete" and none of its start was typed, so it is an accident of
        // spelling rather than a request. Reported: keywords like this crowded out the tables.
        var ranked = SuggestionRanker.Rank([Keyword("delete"), Table("fee_types"), Column("item_type")], "ete");

        Assert.All(ranked, s => Assert.True(true));
        Assert.Equal(SuggestionKind.Keyword, ranked[^1].Kind);
        Assert.NotEqual(SuggestionKind.Keyword, ranked[0].Kind);
    }

    [Fact]
    public void A_keyword_whose_start_was_typed_keeps_its_place()
    {
        // The other half, and the reason this is not simply "priority first": typing "sel" and pressing
        // Enter has to still give you select, not whichever table happens to contain an s, an e and an l.
        var ranked = SuggestionRanker.Rank([Keyword("select"), Table("settlements")], "sel");

        Assert.Equal("select", ranked[0].DisplayText);
    }

    [Fact]
    public void A_keywords_initials_are_not_an_accident_either()
    {
        // "gb" for "group by" is deliberate shorthand, so that keyword is not demoted.
        var ranked = SuggestionRanker.Rank([Keyword("group by"), Table("global_batches")], "gb");

        Assert.Contains("group by", ranked.Select(s => s.DisplayText));
    }

    [Fact]
    public void At_equal_quality_a_table_still_beats_a_keyword()
    {
        // Unchanged behaviour, pinned so the demotion cannot be mistaken for the whole rule. "ord" is a
        // prefix of both, so quality ties and Priority decides.
        var ranked = SuggestionRanker.Rank([Keyword("order"), Table("orders")], "ord");

        Assert.Equal("orders", ranked[0].DisplayText);

        // …and an *exact* keyword match still wins, because at that point the user has typed the whole word.
        var exact = SuggestionRanker.Rank([Keyword("order"), Table("orders")], "order");
        Assert.Equal("order", exact[0].DisplayText);
    }
}
