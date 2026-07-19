using System.Linq;
using Squirrel.App.Results;
using Xunit;

namespace Squirrel.App.Tests;

public class JsonTreeTests
{
    [Fact]
    public void Parses_object_into_typed_scalar_nodes()
    {
        var root = JsonTree.Parse("""{ "title": "Alien", "year": 1979, "hd": true, "note": null }""");

        Assert.NotNull(root);
        Assert.Equal(JsonNodeKind.Object, root!.Kind);
        Assert.Equal(4, root.ChildCount);

        var byKey = root.Children.ToDictionary(c => c.Key!);
        Assert.Equal(JsonNodeKind.String, byKey["title"].Kind);
        Assert.Equal("Alien", byKey["title"].Value);
        Assert.Equal(JsonNodeKind.Number, byKey["year"].Kind);
        Assert.Equal("1979", byKey["year"].Value);
        Assert.Equal(JsonNodeKind.Boolean, byKey["hd"].Kind);
        Assert.Equal(JsonNodeKind.Null, byKey["note"].Kind);
    }

    [Fact]
    public void Parses_nested_arrays()
    {
        var root = JsonTree.Parse("""{ "tags": ["a", "b", "c"] }""");
        var tags = root!.Children.Single();
        Assert.Equal(JsonNodeKind.Array, tags.Kind);
        Assert.Equal(3, tags.ChildCount);
        Assert.Equal("[…3…]", tags.CollapsedSummary);
        Assert.All(tags.Children, c => Assert.Null(c.Key)); // array elements are unkeyed
    }

    [Fact]
    public void Invalid_json_returns_null()
    {
        Assert.Null(JsonTree.Parse("not json"));
        Assert.Null(JsonTree.Parse("{ unterminated"));
        Assert.Null(JsonTree.Parse(""));
        Assert.Null(JsonTree.Parse("   "));
    }

    [Fact]
    public void Prettify_indents_valid_json_and_passes_through_invalid()
    {
        var pretty = JsonTree.Prettify("""{"a":1}""");
        Assert.Contains("\n", pretty);          // now multi-line
        Assert.Contains("\"a\"", pretty);
        Assert.Equal("not json", JsonTree.Prettify("not json")); // unchanged
    }

    [Fact]
    public void Find_flags_matching_key_and_value_and_expands_ancestors()
    {
        var root = JsonTree.Parse("""{ "outer": { "needle": "haystack" } }""")!;
        var outer = root.Children.Single();
        outer.IsExpanded = false; // collapsed to start

        var count = JsonTree.ApplyFind(root, "needle");

        Assert.Equal(1, count);
        Assert.True(outer.Children.Single().IsMatch); // the "needle" node
        Assert.True(outer.IsExpanded);                // ancestor revealed
        Assert.True(root.IsExpanded);
    }

    [Fact]
    public void Find_matches_are_case_insensitive_and_hit_values()
    {
        var root = JsonTree.Parse("""{ "k": "HayStack" }""")!;
        Assert.Equal(1, JsonTree.ApplyFind(root, "haystack"));
    }

    [Fact]
    public void Empty_find_clears_previous_matches()
    {
        var root = JsonTree.Parse("""{ "k": "v" }""")!;
        JsonTree.ApplyFind(root, "k");
        Assert.True(root.Children.Single().IsMatch);

        Assert.Equal(0, JsonTree.ApplyFind(root, ""));
        Assert.False(root.Children.Single().IsMatch);
    }

    [Fact]
    public void SetExpandedAll_toggles_every_container()
    {
        var root = JsonTree.Parse("""{ "a": { "b": [1, 2] } }""")!;

        JsonTree.SetExpandedAll(root, false);
        Assert.False(root.IsExpanded);
        Assert.All(Descend(root).Where(n => n.IsContainer), n => Assert.False(n.IsExpanded));

        JsonTree.SetExpandedAll(root, true);
        Assert.All(Descend(root).Where(n => n.IsContainer), n => Assert.True(n.IsExpanded));
    }

    private static System.Collections.Generic.IEnumerable<JsonTreeNode> Descend(JsonTreeNode n)
    {
        yield return n;
        foreach (var c in n.Children)
            foreach (var d in Descend(c))
                yield return d;
    }
}
