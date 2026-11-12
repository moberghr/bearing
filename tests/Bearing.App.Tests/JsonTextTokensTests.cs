using System.Collections.Generic;
using System.Linq;
using Bearing.App.Results;
using Xunit;

namespace Bearing.App.Tests;

public class JsonTextTokensTests
{
    private static JsonTextDocument Doc(string json)
    {
        var lines = JsonText.Render(JsonTree.Parse(json)!);
        return JsonText.Join(JsonText.Flatten(lines, new HashSet<string>()));
    }

    private static string Selected(JsonTextDocument doc, string near, int into = 0)
    {
        var (start, length) = JsonTextTokens.TokenAt(doc, doc.Text.IndexOf(near) + into);
        return doc.Text.Substring(start, length);
    }

    [Fact]
    public void A_string_selects_its_contents_without_the_quotes()
    {
        var doc = Doc("""{"title":"Alien: the director's cut","year":1979}""");

        Assert.Equal("Alien: the director's cut", Selected(doc, "director"));
        Assert.Equal("Alien: the director's cut", Selected(doc, "\"Alien"));   // on the opening quote
    }

    [Fact]
    public void A_key_selects_its_name_and_a_number_or_keyword_selects_whole()
    {
        var doc = Doc("""{"release_year":1979,"hd":true,"note":null}""");

        Assert.Equal("release_year", Selected(doc, "year"));
        Assert.Equal("1979", Selected(doc, "1979", into: 2));
        Assert.Equal("true", Selected(doc, "true"));
        Assert.Equal("null", Selected(doc, "null", into: 3));
    }

    [Fact]
    public void An_empty_string_selects_its_quotes()
    {
        var doc = Doc("""{"a":""}""");
        var at = doc.Text.LastIndexOf("\"\"");

        var (start, length) = JsonTextTokens.TokenAt(doc, at);

        Assert.Equal("\"\"", doc.Text.Substring(start, length));
    }

    [Fact]
    public void Punctuation_and_indentation_select_the_line_without_its_indentation()
    {
        var doc = Doc("""{"crew":{"pilot":"Dallas"}}""");
        var indent = doc.Text.IndexOf("    \"pilot\"");     // in the indentation before the key

        var (start, length) = JsonTextTokens.TokenAt(doc, indent + 1);

        Assert.Equal("\"pilot\": \"Dallas\"", doc.Text.Substring(start, length));
        Assert.Equal("\"crew\": {", Selected(doc, ": {", into: 2));
    }

    [Fact]
    public void Finds_http_and_https_links_in_string_values()
    {
        var doc = Doc("""{"home":"https://example.com/a?b=1","doc":"see http://x.org/p.","n":1}""");

        var links = JsonTextTokens.Links(doc);

        Assert.Equal(
            new[] { "https://example.com/a?b=1", "http://x.org/p" },   // sentence full stop trimmed
            links.Select(l => doc.Text.Substring(l.Start, l.Length)));
        Assert.Equal("https://example.com/a?b=1", links[0].Uri.OriginalString);
    }

    [Fact]
    public void Keys_other_schemes_and_hostless_text_are_not_links()
    {
        var doc = Doc("""{"https://key.example":"file:///etc/passwd","js":"javascript:alert(1)","bare":"http://"}""");

        Assert.Empty(JsonTextTokens.Links(doc));
    }

    [Fact]
    public void A_link_stops_at_a_json_escape()
    {
        var doc = Doc("{\"q\":\"\\\"https://a.example/x\\\" said\"}");   // "\"https://a.example/x\" said"

        var link = Assert.Single(JsonTextTokens.Links(doc));

        Assert.Equal("https://a.example/x", doc.Text.Substring(link.Start, link.Length));
    }

    [Fact]
    public void LinkAt_covers_the_link_characters_only()
    {
        var doc = Doc("""{"u":"go https://a.example now"}""");
        var links = JsonTextTokens.Links(doc);
        var link = links.Single();

        Assert.Equal(link, JsonTextTokens.LinkAt(links, link.Start));
        Assert.Equal(link, JsonTextTokens.LinkAt(links, link.End - 1));
        Assert.Null(JsonTextTokens.LinkAt(links, link.Start - 1));
        Assert.Null(JsonTextTokens.LinkAt(links, link.End));
    }

    [Fact]
    public void Segments_cut_a_string_span_around_its_links_and_cover_the_same_text()
    {
        var doc = Doc("""{"u":"a https://a.example b https://b.example","k":1}""");
        var links = JsonTextTokens.Links(doc);

        var segments = JsonTextTokens.Segments(doc.Colours, links);

        Assert.Equal(links.Select(l => (l.Start, l.Length)),
            segments.Where(s => s.IsLink).Select(s => (s.Start, s.Length)));
        Assert.Equal(doc.Colours.Sum(c => c.Length), segments.Sum(s => s.Length));
        for (var i = 1; i < segments.Count; i++)
            Assert.True(segments[i].Start >= segments[i - 1].Start + segments[i - 1].Length);   // no overlap
        Assert.All(segments.Where(s => s.IsLink), s => Assert.Equal(JsonSpanKind.String, s.Kind));
    }
}
