using System.Collections.Generic;
using System.Linq;
using Bearing.App.Results;
using Xunit;

namespace Bearing.App.Tests;

public class JsonTextTests
{
    private static IReadOnlyList<JsonLine> Lines(string json) => JsonText.Render(JsonTree.Parse(json)!);

    private static string Render(string json) => JsonText.Plain(Lines(json));

    /// <summary>The document as it draws with <paramref name="folded"/> collapsed.</summary>
    private static string Visible(string json, params string[] folded)
        => string.Join("\n", JsonText.Flatten(Lines(json), new HashSet<string>(folded)).Select(r => r.Text));

    private static IReadOnlyList<JsonRow> Rows(string json, params string[] folded)
        => JsonText.Flatten(Lines(json), new HashSet<string>(folded));

    [Fact]
    public void Renders_an_object_as_indented_json()
    {
        var text = Render("""{"title":"Alien","year":1979}""");

        Assert.Equal(
            """
            {
              "title": "Alien",
              "year": 1979
            }
            """.ReplaceLineEndings("\n"),
            text);
    }

    [Fact]
    public void Nests_containers_and_commas_every_element_but_the_last()
    {
        var text = Render("""{"tags":["a","b"],"crew":{"pilot":"Dallas"},"hd":true,"note":null}""");

        Assert.Equal(
            """
            {
              "tags": [
                "a",
                "b"
              ],
              "crew": {
                "pilot": "Dallas"
              },
              "hd": true,
              "note": null
            }
            """.ReplaceLineEndings("\n"),
            text);
    }

    [Fact]
    public void Empty_containers_stay_on_one_line()
    {
        Assert.Equal(
            """
            {
              "tags": [],
              "crew": {}
            }
            """.ReplaceLineEndings("\n"),
            Render("""{"tags":[],"crew":{}}"""));
    }

    [Fact]
    public void Scalar_root_renders_alone()
    {
        Assert.Equal("42", Render("42"));
        Assert.Equal("\"lone\"", Render("\"lone\""));
    }

    [Fact]
    public void String_values_are_escaped_but_non_ascii_stays_readable()
    {
        var text = Render("""{"q":"say \"hi\"\n","name":"Ripleyö"}""");

        Assert.Contains("\\\"hi\\\"", text);      // quotes escaped
        Assert.Contains(@"\n", text);          // newline escaped, not a real line break
        Assert.Contains("Ripleyö", text);      // left as-is, not a ö escape
        Assert.Equal(4, text.Split('\n').Length);
    }

    [Fact]
    public void Spans_carry_the_kind_the_view_colours_by()
    {
        var member = Lines("""{"year":1979}""")[1].Spans;

        Assert.Equal(JsonSpanKind.Punctuation, member[0].Kind);   // indent
        Assert.Equal(new JsonSpan("\"year\"", JsonSpanKind.Key), member[1]);
        Assert.Equal(new JsonSpan(": ", JsonSpanKind.Punctuation), member[2]);
        Assert.Equal(new JsonSpan("1979", JsonSpanKind.Number), member[3]);
    }

    // ---- folding ----------------------------------------------------------------------------------

    [Fact]
    public void Folding_a_container_collapses_it_to_a_placeholder_and_hides_its_lines()
    {
        Assert.Equal(
            """
            {
              "tags": […2…],
              "hd": true
            }
            """.ReplaceLineEndings("\n"),
            Visible("""{"tags":["a","b"],"hd":true}""", "$.0"));
    }

    [Fact]
    public void An_object_folds_to_braces_and_keeps_its_trailing_comma()
    {
        Assert.Equal(
            """
            {
              "crew": {…1…},
              "hd": true
            }
            """.ReplaceLineEndings("\n"),
            Visible("""{"crew":{"pilot":"Dallas"},"hd":true}""", "$.0"));

        // Last member: no comma.
        Assert.Equal(
            """
            {
              "crew": {…1…}
            }
            """.ReplaceLineEndings("\n"),
            Visible("""{"crew":{"pilot":"Dallas"}}""", "$.0"));
    }

    [Fact]
    public void Folding_the_root_leaves_one_line()
    {
        Assert.Equal("{…2…}", Visible("""{"a":1,"b":[2,3]}""", "$"));
    }

    [Fact]
    public void Folding_a_parent_swallows_an_already_folded_child()
    {
        var rows = Rows("""{"outer":{"inner":[1,2]}}""", "$.0.0", "$.0");

        Assert.Equal(3, rows.Count);                              // "{", folded "outer", "}"
        Assert.Contains("{…1…}", rows[1].Text);
    }

    [Fact]
    public void Only_non_empty_containers_can_fold()
    {
        var paths = JsonText.FoldablePaths(Lines("""{"a":1,"empty":{},"list":[[]]}"""));

        Assert.Equal(new[] { "$", "$.2" }, paths);   // root and "list"; not the scalar, not {} or the inner []
    }

    [Fact]
    public void Rows_carry_a_chevron_only_on_the_lines_that_open_a_container()
    {
        var rows = Rows("""{"tags":["a"]}""");

        Assert.Equal("$", rows[0].FoldPath);        // {
        Assert.Equal("$.0", rows[1].FoldPath);      // "tags": [
        Assert.Null(rows[2].FoldPath);              // "a"
        Assert.Null(rows[3].FoldPath);              // ]
        Assert.Null(rows[4].FoldPath);              // }
        Assert.All(rows, r => Assert.False(r.IsFolded));

        Assert.True(Rows("""{"tags":["a"]}""", "$.0")[1].IsFolded);
    }

    // ---- find -------------------------------------------------------------------------------------

    [Fact]
    public void Highlight_marks_only_the_matched_substring()
    {
        var rows = Rows("""{"needle":"haystack"}""");

        var marked = JsonText.Highlight(rows, "stack", out var matches);

        Assert.Equal(1, matches);
        var matched = marked.SelectMany(r => r.Spans).Where(s => s.IsMatch).ToList();
        Assert.Equal("stack", Assert.Single(matched).Text);
        // Splitting a span never changes the text it draws.
        Assert.Equal(rows.Select(r => r.Text), marked.Select(r => r.Text));
    }

    [Fact]
    public void Highlight_matches_keys_and_values_case_insensitively_and_counts_every_hit()
    {
        JsonText.Highlight(Rows("""{"ship":"Ship of ships"}"""), "SHIP", out var matches);

        Assert.Equal(3, matches); // the key, and twice in the value
    }

    [Fact]
    public void Highlight_never_matches_punctuation_or_indentation()
    {
        var rows = Rows("""{"a":{"b":1}}""");

        JsonText.Highlight(rows, "{", out var braces);
        JsonText.Highlight(rows, " ", out var spaces);

        Assert.Equal(0, braces);
        Assert.Equal(0, spaces);
    }

    [Fact]
    public void A_folded_placeholder_is_not_searchable()
    {
        JsonText.Highlight(Rows("""{"tags":["needle"]}""", "$.0"), "needle", out var matches);

        Assert.Equal(0, matches); // it's hidden — PathsToReveal is what opens it
    }

    [Fact]
    public void Empty_find_leaves_the_rows_untouched()
    {
        var rows = Rows("""{"k":"v"}""");

        var same = JsonText.Highlight(rows, "  ", out var matches);

        Assert.Equal(0, matches);
        Assert.Same(rows, same);
        Assert.DoesNotContain(same.SelectMany(r => r.Spans), s => s.IsMatch);
    }

    [Fact]
    public void PathsToReveal_names_the_ancestors_of_every_match()
    {
        var reveal = JsonText.PathsToReveal(Lines("""{"a":{"b":{"c":"needle"}}}"""), "needle");

        Assert.Equal(new[] { "$", "$.0", "$.0.0" }, reveal.OrderBy(p => p));
    }

    [Fact]
    public void PathsToReveal_ignores_a_container_whose_own_key_matches()
    {
        // "crew" is legible on the folded line, so folding it isn't hiding the match.
        var reveal = JsonText.PathsToReveal(Lines("""{"crew":{"pilot":"Dallas"}}"""), "crew");

        Assert.Equal(new[] { "$" }, reveal);
    }

    [Fact]
    public void PathsToReveal_is_empty_without_a_query()
    {
        var lines = Lines("""{"a":{"b":1}}""");

        Assert.Empty(JsonText.PathsToReveal(lines, ""));
        Assert.Empty(JsonText.PathsToReveal(lines, "   "));
        Assert.Empty(JsonText.PathsToReveal(lines, null));
    }
}
