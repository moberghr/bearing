using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Completion;
using Bearing.Core.Completion;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The popup's row data: every emitted kind gets its own glyph, and the fields the template draws come
/// straight off the suggestion. The visuals themselves need eyeball QA (§4.3) — this pins the mapping.
/// </summary>
public class CompletionItemTests
{
    private static BearingCompletionData Item(SuggestionKind kind, string? detail = null, string? trailing = null)
        => new(new Suggestion
        {
            DisplayText = "orders",
            DetailText = detail,
            TrailingText = trailing,
            ReplacementText = "orders o",
            Kind = kind,
            Priority = 10,
        });

    [Fact]
    public void Every_kind_resolves_a_glyph_and_a_colour()
    {
        foreach (var kind in Enum.GetValues<SuggestionKind>())
        {
            var item = Item(kind);
            Assert.StartsWith("Icon.", item.IconKey);
            Assert.NotEmpty(item.IconColorKey);
        }
    }

    [Fact]
    public void The_kinds_the_engine_emits_are_visually_distinct()
    {
        // Joins in particular: accepting one inserts a whole JOIN … ON … clause, so it must not look
        // like another table.
        var emitted = new[]
        {
            SuggestionKind.Table, SuggestionKind.View, SuggestionKind.Column,
            SuggestionKind.Keyword, SuggestionKind.Join, SuggestionKind.Schema,
        };
        var glyphs = emitted.Select(k => Item(k).IconKey).ToArray();
        Assert.Equal(glyphs.Length, glyphs.Distinct().Count());
    }

    [Fact]
    public void Filtering_text_stays_the_bare_name_while_insertion_carries_the_sql()
    {
        var item = Item(SuggestionKind.Table);
        Assert.Equal("orders", item.Text);          // what the typed prefix is matched against
        Assert.Equal("orders", item.DisplayText);
    }

    [Fact]
    public void Detail_and_trailing_text_are_exposed_separately_not_padded_into_the_label()
    {
        var item = Item(SuggestionKind.Join, detail: "join → u", trailing: "o.user_id = u.id");
        Assert.Equal("orders", item.DisplayText);   // no four-space column hack
        Assert.Equal("join → u", item.DetailText);
        Assert.Equal("o.user_id = u.id", item.TrailingText);
        Assert.DoesNotContain("    ", item.DisplayText);
    }

    [Fact]
    public void The_content_fallback_is_the_plain_name_not_a_padded_two_column_string()
        => Assert.Equal("orders", Item(SuggestionKind.Join, detail: "join → u").Content);
}
