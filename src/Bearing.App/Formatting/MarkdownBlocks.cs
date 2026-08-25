using System;
using System.Collections.Generic;
using System.Text;

namespace Bearing.App.Formatting;

/// <summary>What a parsed line of release-note markdown is.</summary>
public enum MarkdownBlockKind
{
    /// <summary>Running text. Consecutive non-blank lines are folded into one, as markdown does.</summary>
    Paragraph,

    /// <summary><c>#</c>…<c>######</c>; the count is <see cref="MarkdownBlock.Level"/>.</summary>
    Heading,

    /// <summary>
    /// A list item, bulleted (<c>-</c>/<c>*</c>/<c>+</c>) or numbered (<c>1.</c>).
    /// <see cref="MarkdownBlock.Level"/> is its nesting depth from 1, and
    /// <see cref="MarkdownBlock.Marker"/> is what to draw in front of it.
    /// </summary>
    Bullet,

    /// <summary>A fenced block, kept verbatim — no inline parsing inside it.</summary>
    Code,

    /// <summary>A <c>---</c> horizontal rule.</summary>
    Rule,
}

/// <summary>
/// A run of text within a block, carrying only the emphasis the renderer needs to vary. Link spans are
/// <b>coloured but not underlined</b>: the notes dialog can't make an inline run clickable, and underlining
/// text that does nothing when clicked is a worse lie than colouring text that points somewhere. The card's
/// "Open on GitHub" button is the way out to a page where the links do work.
/// </summary>
public readonly record struct MarkdownSpan(string Text, bool Bold = false, bool Code = false, bool Link = false);

/// <summary>One block of a release note, ready to be turned into a control.</summary>
/// <param name="Level">Heading level, or list nesting depth, both from 1. Zero elsewhere.</param>
/// <param name="Marker">What a list item draws in its gutter — a bullet, or the number the author wrote.
/// Empty for every other kind. A numbered list keeps its own numbering rather than being renumbered: a
/// release note that says "step 3" means the one the author called 3.</param>
public sealed record MarkdownBlock(
    MarkdownBlockKind Kind,
    int Level,
    IReadOnlyList<MarkdownSpan> Spans,
    string Marker = "")
{
    /// <summary>The block's text with all emphasis dropped — for measuring, tooltips and tests.</summary>
    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var span in Spans) sb.Append(span.Text);
            return sb.ToString();
        }
    }
}

/// <summary>
/// A deliberately small markdown reader for release notes — headings, bullets, fenced code, rules, and
/// inline code / bold / links. Not a CommonMark implementation and not trying to be.
/// <para>
/// It exists instead of a markdown package because this input is not arbitrary markdown: it is the text
/// <c>build/velopack.sh</c> produces (commit subjects with <c>#nn</c> issue refs) or a hand-written
/// <c>docs/release-notes/&lt;version&gt;.md</c>. A renderer that styles itself would also have to be fought
/// back into the Kanagawa tokens, whereas this hands the view plain spans and lets it use the same brushes
/// as everything else. Unknown syntax degrades to its literal text rather than disappearing — an unreadable
/// note is recoverable, a silently empty one is not.
/// </para>
/// <para>Pure and allocation-cheap, so the whole of it is unit-testable without a window (§2.5/§4.3).</para>
/// </summary>
public static class MarkdownBlocks
{
    /// <summary>Deepest nesting a bullet is indented for; beyond this the extra depth is just lost margin.</summary>
    private const int MaxBulletDepth = 3;

    /// <summary>Parse <paramref name="markdown"/> into blocks, top to bottom. Empty input gives no blocks.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        var code = new List<string>();
        var fenced = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, 0, ParseInline(string.Join(" ", paragraph))));
            paragraph.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (fenced)
                {
                    // Closing fence: emit even an empty block, so an empty fence doesn't silently vanish.
                    blocks.Add(new MarkdownBlock(
                        MarkdownBlockKind.Code, 0, [new MarkdownSpan(string.Join("\n", code), Code: true)]));
                    code.Clear();
                }
                else
                {
                    FlushParagraph();
                }

                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                code.Add(raw);
                continue;
            }

            if (line.Trim().Length == 0)
            {
                FlushParagraph();
                continue;
            }

            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (IsRule(trimmed))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Rule, 0, []));
                continue;
            }

            if (HeadingLevel(trimmed) is { } level)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(
                    MarkdownBlockKind.Heading, level, ParseInline(trimmed[level..].TrimStart(' ', '#'))));
                continue;
            }

            if (ListItem(trimmed) is var (marker, item) && item is not null)
            {
                FlushParagraph();
                // Two spaces per level is the common convention; anything deeper flattens rather than
                // marching off the right edge of a fixed-width dialog.
                var depth = Math.Clamp(indent / 2 + 1, 1, MaxBulletDepth);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Bullet, depth, ParseInline(item), marker));
                continue;
            }

            paragraph.Add(trimmed);
        }

        // An unterminated fence is malformed input; keep what it held rather than dropping the tail.
        if (code.Count > 0)
            blocks.Add(new MarkdownBlock(
                MarkdownBlockKind.Code, 0, [new MarkdownSpan(string.Join("\n", code), Code: true)]));
        FlushParagraph();
        return blocks;
    }

    /// <summary>
    /// Split a line into emphasis runs. Anything that opens but never closes (a lone backtick, a <c>[</c>
    /// with no target) is kept as literal text — release notes are written by hand and a stray bracket must
    /// not eat the rest of the line.
    /// </summary>
    internal static IReadOnlyList<MarkdownSpan> ParseInline(string text)
    {
        var spans = new List<MarkdownSpan>();
        var literal = new StringBuilder();

        void Flush()
        {
            if (literal.Length == 0) return;
            spans.Add(new MarkdownSpan(literal.ToString()));
            literal.Clear();
        }

        for (var i = 0; i < text.Length;)
        {
            var c = text[i];

            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    spans.Add(new MarkdownSpan(text[(i + 1)..end], Code: true));
                    i = end + 1;
                    continue;
                }
            }
            else if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    spans.Add(new MarkdownSpan(text[(i + 2)..end], Bold: true));
                    i = end + 2;
                    continue;
                }
            }
            else if (c == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close > i && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var target = text.IndexOf(')', close + 2);
                    if (target > close)
                    {
                        Flush();
                        // The label is what a reader wants; the URL is unreachable from here anyway, so it
                        // is dropped rather than shown as noise beside it.
                        spans.Add(new MarkdownSpan(text[(i + 1)..close], Link: true));
                        i = target + 1;
                        continue;
                    }
                }
            }
            else if (c == '#' && IssueRefEnd(text, i) is { } refEnd)
            {
                Flush();
                spans.Add(new MarkdownSpan(text[i..refEnd], Link: true));
                i = refEnd;
                continue;
            }

            literal.Append(c);
            i++;
        }

        Flush();
        return spans;
    }

    /// <summary>
    /// End index of a <c>#123</c> issue reference starting at <paramref name="start"/>, or null if that isn't
    /// one. Must start a word — <c>abc#1</c> is not a reference, and neither is the <c>#</c> of a heading,
    /// which is handled before this is ever reached.
    /// </summary>
    private static int? IssueRefEnd(string text, int start)
    {
        if (start > 0 && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] is not ('(' or '[' or '-'))
            return null;

        var i = start + 1;
        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
        return i > start + 1 ? i : null;
    }

    /// <summary>Heading level for <c>#</c>…<c>######</c> followed by a space, or null when it isn't a heading.</summary>
    private static int? HeadingLevel(string trimmed)
    {
        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
        return hashes is > 0 and <= 6 && hashes < trimmed.Length && trimmed[hashes] == ' ' ? hashes : null;
    }

    /// <summary>
    /// The gutter marker and the text of a list item, or a null body when the line isn't one. Numbered items
    /// are recognised as well as bulleted: without this a <c>1. / 2. / 3.</c> list — ordinary in a
    /// hand-written release note — falls through to the paragraph path and is folded into one run-on line,
    /// which is mangling rather than the graceful degradation this class promises.
    /// </summary>
    private static (string Marker, string? Body) ListItem(string trimmed)
    {
        if (trimmed.Length > 1 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
            return ("•", trimmed[2..].TrimStart());

        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;
        if (digits is > 0 and <= 3 && digits + 1 < trimmed.Length
            && trimmed[digits] is '.' or ')' && trimmed[digits + 1] == ' ')
        {
            return (trimmed[..(digits + 1)], trimmed[(digits + 2)..].TrimStart());
        }

        return ("", null);
    }

    /// <summary>Three or more of the same rule character, and nothing else.</summary>
    private static bool IsRule(string trimmed)
    {
        if (trimmed.Length < 3) return false;
        var c = trimmed[0];
        if (c is not ('-' or '*' or '_')) return false;
        foreach (var ch in trimmed)
            if (ch != c) return false;
        return true;
    }
}
