using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace Bearing.App.Results;

/// <summary>What a rendered JSON span is, so the view can colour it.</summary>
public enum JsonSpanKind
{
    /// <summary>Indentation, braces, brackets, commas, the <c>": "</c> after a key, and a folded
    /// container's <c>{…3…}</c> placeholder. Never searched.</summary>
    Punctuation,
    /// <summary>A quoted object member name.</summary>
    Key,
    String,
    Number,
    /// <summary><c>true</c> / <c>false</c> / <c>null</c>.</summary>
    Keyword,
}

/// <summary>One coloured run of text on a rendered JSON line. <see cref="IsMatch"/> is set by
/// <see cref="JsonText.Highlight"/> on the exact substring the find query hit.</summary>
public sealed record JsonSpan(string Text, JsonSpanKind Kind, bool IsMatch = false);

/// <summary>
/// One line of the fully expanded document. A line that opens a non-empty container also carries what it
/// takes to fold it: how many following lines belong to it, and how the line reads once folded.
/// </summary>
/// <param name="Path">Stable id of the value this line belongs to (<c>$.2.0</c>) — ordinals, not keys, so
/// it can't collide with a key containing a dot. Fold state is keyed by it and survives a re-render.</param>
/// <param name="Spans">The line, expanded.</param>
/// <param name="CanFold">Whether this line opens a container that has children.</param>
/// <param name="FoldedLines">How many lines after this one disappear when it folds (its descendants plus
/// its own closing brace).</param>
/// <param name="FoldedSpans">The line as <c>"crew": {…3…},</c> when folded.</param>
public sealed record JsonLine(
    string Path,
    IReadOnlyList<JsonSpan> Spans,
    bool CanFold = false,
    int FoldedLines = 0,
    IReadOnlyList<JsonSpan>? FoldedSpans = null)
{
    public string Text => string.Concat(Spans.Select(s => s.Text));
}

/// <summary>One visible line: what to draw, and the chevron to draw beside it (if any).</summary>
public sealed record JsonRow(IReadOnlyList<JsonSpan> Spans, string? FoldPath, bool IsFolded)
{
    public string Text => string.Concat(Spans.Select(s => s.Text));
}

/// <summary>
/// Renders a parsed <see cref="JsonTreeNode"/> as actual indented JSON — the lines a reader expects, split
/// into coloured spans — and handles folding and find over them. Replaces the fold-tree the cell inspector
/// used to show (issue #34): the document is the view, and folding happens in it rather than instead of it.
/// <para>
/// Pure and UI-free, so the exact text, the fold arithmetic and the highlighting are unit-testable without
/// Avalonia. This is also the only JSON formatter in the app: what the inspector shows is what Copy puts on
/// the clipboard (<see cref="Plain"/>).
/// </para>
/// </summary>
public static class JsonText
{
    private const int IndentWidth = 2;
    private const string RootPath = "$";

    /// <summary>Faithful, readable escaping: valid JSON, but non-ASCII text stays legible instead of
    /// turning into <c>ö</c> escapes (this output is read by a person, not embedded in HTML).</summary>
    private static readonly JsonSerializerOptions StringOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Render a parsed value as the fully expanded, indented document.</summary>
    public static IReadOnlyList<JsonLine> Render(JsonTreeNode root)
    {
        var lines = new List<JsonLine>();
        Emit(root, RootPath, depth: 0, last: true);
        return lines;

        void Emit(JsonTreeNode node, string path, int depth, bool last)
        {
            var head = new List<JsonSpan>();
            Indent(head, depth);
            if (node.Key is not null)
            {
                head.Add(new JsonSpan(Quote(node.Key), JsonSpanKind.Key));
                head.Add(new JsonSpan(": ", JsonSpanKind.Punctuation));
            }

            if (!node.IsContainer)
            {
                head.Add(Scalar(node));
                Comma(head, last);
                lines.Add(new JsonLine(path, head));
                return;
            }

            var (open, close) = node.Kind == JsonNodeKind.Array ? ("[", "]") : ("{", "}");
            if (node.ChildCount == 0)
            {
                head.Add(new JsonSpan(open + close, JsonSpanKind.Punctuation));
                Comma(head, last);
                lines.Add(new JsonLine(path, head));
                return;
            }

            // The folded form of this same line, built before the opening brace is appended to it.
            var folded = new List<JsonSpan>(head) { new(Summary(node), JsonSpanKind.Punctuation) };
            Comma(folded, last);

            head.Add(new JsonSpan(open, JsonSpanKind.Punctuation));
            var opening = lines.Count;
            lines.Add(new JsonLine(path, head, CanFold: true, FoldedSpans: folded));

            for (var i = 0; i < node.ChildCount; i++)
                Emit(node.Children[i], $"{path}.{i}", depth + 1, last: i == node.ChildCount - 1);

            var tail = new List<JsonSpan>();
            Indent(tail, depth);
            tail.Add(new JsonSpan(close, JsonSpanKind.Punctuation));
            Comma(tail, last);
            lines.Add(new JsonLine(path, tail));

            lines[opening] = lines[opening] with { FoldedLines = lines.Count - 1 - opening };
        }

        static void Indent(List<JsonSpan> spans, int depth)
        {
            if (depth > 0) spans.Add(new JsonSpan(new string(' ', depth * IndentWidth), JsonSpanKind.Punctuation));
        }

        static void Comma(List<JsonSpan> spans, bool last)
        {
            if (!last) spans.Add(new JsonSpan(",", JsonSpanKind.Punctuation));
        }
    }

    /// <summary>The whole document as plain text — what Copy puts on the clipboard, folded or not.</summary>
    public static string Plain(IReadOnlyList<JsonLine> lines)
        => string.Join("\n", lines.Select(l => l.Text));

    /// <summary>
    /// The lines to draw for a given fold state: a folded container collapses to its <c>{…3…}</c> line and
    /// its descendants are skipped, so folding a parent hides a folded child without any bookkeeping.
    /// </summary>
    public static IReadOnlyList<JsonRow> Flatten(IReadOnlyList<JsonLine> lines, IReadOnlySet<string> folded)
    {
        var rows = new List<JsonRow>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.CanFold && folded.Contains(line.Path))
            {
                rows.Add(new JsonRow(line.FoldedSpans!, line.Path, IsFolded: true));
                i += line.FoldedLines;
                continue;
            }
            rows.Add(new JsonRow(line.Spans, line.CanFold ? line.Path : null, IsFolded: false));
        }
        return rows;
    }

    /// <summary>
    /// Every foldable path in the document, for collapse-all. <paramref name="includeRoot"/> is off for
    /// collapse-all itself: folding the root would replace the whole value with one <c>{…12…}</c>
    /// line, which is never what "collapse all" means — the root chevron is still there to do it by hand.
    /// </summary>
    public static IReadOnlyList<string> FoldablePaths(IReadOnlyList<JsonLine> lines, bool includeRoot = true)
        => lines.Where(l => l.CanFold && (includeRoot || l.Path != RootPath)).Select(l => l.Path).ToList();

    /// <summary>
    /// The containers that have to open for every match on <paramref name="query"/> to be visible — the
    /// ancestors of each matching line. A container whose own key matches doesn't need to open: that key is
    /// legible on the folded line.
    /// </summary>
    public static IReadOnlySet<string> PathsToReveal(IReadOnlyList<JsonLine> lines, string? query)
    {
        var reveal = new HashSet<string>();
        var q = query?.Trim() ?? "";
        if (q.Length == 0) return reveal;

        foreach (var line in lines.Where(l => Matches(l.Spans, q)))
            for (var dot = line.Path.IndexOf('.'); dot > 0; dot = line.Path.IndexOf('.', dot + 1))
                reveal.Add(line.Path[..dot]);   // "$", "$.2", … up to but not including the line itself
        return reveal;
    }

    /// <summary>
    /// Split every searchable span on occurrences of <paramref name="query"/> (case-insensitive) so the
    /// matched substrings alone carry <see cref="JsonSpan.IsMatch"/>, and report how many were found.
    /// Punctuation and indentation are never searched; an empty query returns the rows untouched.
    /// </summary>
    public static IReadOnlyList<JsonRow> Highlight(IReadOnlyList<JsonRow> rows, string? query, out int matches)
    {
        matches = 0;
        var q = query?.Trim() ?? "";
        if (q.Length == 0) return rows;

        var count = 0;
        var result = new List<JsonRow>(rows.Count);
        foreach (var row in rows)
        {
            var spans = new List<JsonSpan>(row.Spans.Count);
            foreach (var span in row.Spans)
            {
                if (span.Kind == JsonSpanKind.Punctuation) { spans.Add(span); continue; }

                var cursor = 0;
                while (span.Text.IndexOf(q, cursor, StringComparison.OrdinalIgnoreCase) is var hit && hit >= 0)
                {
                    if (hit > cursor) spans.Add(span with { Text = span.Text[cursor..hit] });
                    spans.Add(span with { Text = span.Text.Substring(hit, q.Length), IsMatch = true });
                    cursor = hit + q.Length;
                    count++;
                }
                if (cursor == 0) spans.Add(span);
                else if (cursor < span.Text.Length) spans.Add(span with { Text = span.Text[cursor..] });
            }
            result.Add(row with { Spans = spans });
        }

        matches = count;
        return result;
    }

    private static bool Matches(IReadOnlyList<JsonSpan> spans, string query)
        => spans.Any(s => s.Kind != JsonSpanKind.Punctuation
                          && s.Text.Contains(query, StringComparison.OrdinalIgnoreCase));

    /// <summary>A folded container's placeholder — the same shorthand the old fold-tree used.</summary>
    private static string Summary(JsonTreeNode node)
        => node.Kind == JsonNodeKind.Array ? $"[…{node.ChildCount}…]" : $"{{…{node.ChildCount}…}}";

    private static JsonSpan Scalar(JsonTreeNode node) => node.Kind switch
    {
        JsonNodeKind.String => new JsonSpan(Quote(node.Value ?? ""), JsonSpanKind.String),
        JsonNodeKind.Number => new JsonSpan(node.Value ?? "0", JsonSpanKind.Number),
        _ => new JsonSpan(node.Value ?? "null", JsonSpanKind.Keyword),
    };

    /// <summary>A JSON string literal, quotes and escapes included.</summary>
    private static string Quote(string value) => JsonSerializer.Serialize(value, StringOptions);
}
