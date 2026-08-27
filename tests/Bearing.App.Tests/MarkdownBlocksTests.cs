using System.Linq;
using Bearing.App.Formatting;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The release-note markdown reader. Worth pinning closely because the dialog it feeds cannot be tested at
/// all (§4.3) — if a note renders as an empty window, this is the only place that would have caught it.
/// </summary>
public class MarkdownBlocksTests
{
    [Fact]
    public void Empty_input_produces_no_blocks()
    {
        Assert.Empty(MarkdownBlocks.Parse(null));
        Assert.Empty(MarkdownBlocks.Parse(""));
        Assert.Empty(MarkdownBlocks.Parse("   \n\n  "));
    }

    [Fact]
    public void Headings_carry_their_level()
    {
        var blocks = MarkdownBlocks.Parse("# One\n## Two\n### Three");

        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(MarkdownBlockKind.Heading, b.Kind));
        Assert.Equal([1, 2, 3], blocks.Select(b => b.Level));
        Assert.Equal(["One", "Two", "Three"], blocks.Select(b => b.Text));
    }

    [Fact]
    public void A_hash_with_no_space_is_not_a_heading()
    {
        // "#42 is fixed" is an issue reference, not a level-1 heading — getting this wrong would render a
        // whole bullet's worth of text at title size.
        var block = Assert.Single(MarkdownBlocks.Parse("#42 is fixed"));
        Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind);
    }

    [Theory]
    [InlineData("- item")]
    [InlineData("* item")]
    [InlineData("+ item")]
    public void Every_bullet_marker_is_recognised(string line)
    {
        var block = Assert.Single(MarkdownBlocks.Parse(line));
        Assert.Equal(MarkdownBlockKind.Bullet, block.Kind);
        Assert.Equal("item", block.Text);
        Assert.Equal(1, block.Level);
        Assert.Equal("•", block.Marker);
    }

    [Fact]
    public void A_numbered_list_stays_a_list_and_keeps_its_own_numbers()
    {
        // Otherwise these fold into one run-on paragraph — "1. First 2. Second 3. Third" — which is
        // mangling, not the graceful degradation the parser promises.
        var blocks = MarkdownBlocks.Parse("1. First\n2. Second\n10) Tenth");

        Assert.All(blocks, b => Assert.Equal(MarkdownBlockKind.Bullet, b.Kind));
        Assert.Equal(["First", "Second", "Tenth"], blocks.Select(b => b.Text));
        // The author's numbering, not a renumbering: a note that says "step 10" must still say 10.
        Assert.Equal(["1.", "2.", "10)"], blocks.Select(b => b.Marker));
    }

    [Fact]
    public void A_bare_number_is_not_a_list_item()
    {
        // "2026 was a good year" and a version like "0.3.0 shipped" must stay prose.
        var block = Assert.Single(MarkdownBlocks.Parse("2026 was a good year"));
        Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind);
    }

    [Fact]
    public void Indented_bullets_nest_and_stop_nesting()
    {
        var blocks = MarkdownBlocks.Parse("- top\n  - under\n            - very deep");

        Assert.Equal([1, 2, 3], blocks.Select(b => b.Level));
    }

    [Fact]
    public void Consecutive_lines_fold_into_one_paragraph_and_a_blank_line_breaks_it()
    {
        var blocks = MarkdownBlocks.Parse("first line\nsecond line\n\nnew para");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("first line second line", blocks[0].Text);
        Assert.Equal("new para", blocks[1].Text);
    }

    [Fact]
    public void A_fenced_block_is_kept_verbatim()
    {
        var block = Assert.Single(MarkdownBlocks.Parse("```sql\nSELECT 1\n  -- not a bullet\n```"));

        Assert.Equal(MarkdownBlockKind.Code, block.Kind);
        Assert.Equal("SELECT 1\n  -- not a bullet", block.Text);
    }

    [Fact]
    public void An_unclosed_fence_still_yields_its_contents()
    {
        // Malformed input from a hand-written note must not swallow the rest of the release.
        var block = Assert.Single(MarkdownBlocks.Parse("```\nSELECT 1"));

        Assert.Equal(MarkdownBlockKind.Code, block.Kind);
        Assert.Equal("SELECT 1", block.Text);
    }

    [Fact]
    public void A_rule_is_its_own_block()
    {
        var blocks = MarkdownBlocks.Parse("before\n\n---\n\nafter");

        Assert.Equal(MarkdownBlockKind.Rule, blocks[1].Kind);
        Assert.Equal(3, blocks.Count);
    }

    [Fact]
    public void Inline_code_bold_and_links_become_their_own_spans()
    {
        var block = Assert.Single(MarkdownBlocks.Parse("run `SELECT 1` in **bold** see [the docs](http://x)"));

        Assert.Equal("run SELECT 1 in bold see the docs", block.Text);
        Assert.Contains(block.Spans, s => s.Code && s.Text == "SELECT 1");
        Assert.Contains(block.Spans, s => s.Bold && s.Text == "bold");
        Assert.Contains(block.Spans, s => s.Link && s.Text == "the docs");
    }

    [Fact]
    public void Issue_references_are_linked_where_they_start_a_word()
    {
        var block = Assert.Single(MarkdownBlocks.Parse("closes #45 but not abc#9 or a bare #"));

        Assert.Equal(["#45"], block.Spans.Where(s => s.Link).Select(s => s.Text));
        Assert.Equal("closes #45 but not abc#9 or a bare #", block.Text);
    }

    [Fact]
    public void Unclosed_emphasis_stays_literal()
    {
        // A stray backtick or bracket in a hand-written note must not eat the rest of the line.
        var block = Assert.Single(MarkdownBlocks.Parse("a ` stray and [half a link"));

        Assert.Equal("a ` stray and [half a link", block.Text);
        Assert.All(block.Spans, s => Assert.False(s.Code || s.Bold || s.Link));
    }

    [Fact]
    public void A_real_generated_release_note_parses_into_what_it_looks_like()
    {
        // The exact shape build/velopack.sh emits, so a change to either end shows up here.
        const string notes = """
                             Changes since v0.2.1:

                             - bump version to 0.3.0
                             - leave a space after an accepted completion - closes #41

                             ### Install

                             - **Windows** — `BearingSql-win-Setup.exe` (per-user, no admin).
                             """;

        var blocks = MarkdownBlocks.Parse(notes);

        Assert.Equal(MarkdownBlockKind.Paragraph, blocks[0].Kind);
        Assert.Equal("Changes since v0.2.1:", blocks[0].Text);
        Assert.Equal(3, blocks.Count(b => b.Kind == MarkdownBlockKind.Bullet));
        var heading = Assert.Single(blocks, b => b.Kind == MarkdownBlockKind.Heading);
        Assert.Equal(3, heading.Level);
        Assert.Equal("Install", heading.Text);
        Assert.Contains(blocks.SelectMany(b => b.Spans), s => s.Link && s.Text == "#41");
        Assert.Contains(blocks.SelectMany(b => b.Spans), s => s.Bold && s.Text == "Windows");
        Assert.Contains(blocks.SelectMany(b => b.Spans), s => s.Code && s.Text == "BearingSql-win-Setup.exe");
    }
}
