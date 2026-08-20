using System.Linq;
using Bearing.App.Results;
using Xunit;

namespace Bearing.App.Tests;

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
        Assert.True(tags.IsContainer);
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
}
