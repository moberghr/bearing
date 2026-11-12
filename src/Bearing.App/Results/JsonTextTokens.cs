using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Bearing.App.Results;

/// <summary>An http(s) address inside a string value of a <see cref="JsonTextDocument"/>, by character
/// offset into its text.</summary>
public readonly record struct JsonLink(int Start, int Length, Uri Uri)
{
    public int End => Start + Length;
}

/// <summary>A stretch of <see cref="JsonTextDocument.Text"/> to draw one way: its colour and whether it is
/// a link. The colour spans cut at link edges, so no two segments overlap.</summary>
public readonly record struct JsonTextSegment(int Start, int Length, JsonSpanKind Kind, bool IsLink);

/// <summary>
/// What a point in the rendered JSON text is: the token a triple-click selects, the links Ctrl+click opens,
/// and the drawing segments that underline them. Pure, over the <see cref="JsonTextDocument"/> the inspector
/// already drew — the colour spans are exactly the non-punctuation tokens, so no re-lexing.
/// </summary>
public static partial class JsonTextTokens
{
    // Stops at whitespace, a quote, a backslash (a JSON escape — the link ends where the string's text
    // stops being literal) and the angle brackets that commonly wrap an address in prose.
    [GeneratedRegex(@"https?://[^\s""\\<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    /// <summary>Punctuation that ends a sentence more often than an address.</summary>
    private const string TrailingPunctuation = ".,;:!?)]}'";

    /// <summary>
    /// Every http/https address in a string value (never in a key), in text order. Only those two schemes:
    /// the value comes from a database, and Ctrl+click hands it to the OS launcher.
    /// </summary>
    public static IReadOnlyList<JsonLink> Links(JsonTextDocument document)
    {
        var links = new List<JsonLink>();
        foreach (var span in document.Colours)
        {
            if (span.Kind != JsonSpanKind.String) continue;
            var text = document.Text.Substring(span.Start, span.Length);
            foreach (Match match in UrlPattern().Matches(text))
            {
                var value = match.Value.TrimEnd(TrailingPunctuation.ToCharArray());
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    && uri.Host.Length > 0)
                    links.Add(new JsonLink(span.Start + match.Index, value.Length, uri));
            }
        }
        return links;
    }

    /// <summary>The link under caret position <paramref name="position"/>, or null. A position is between
    /// characters, so the one just past a link's end is not on it.</summary>
    public static JsonLink? LinkAt(IReadOnlyList<JsonLink> links, int position)
    {
        foreach (var link in links)
            if (position >= link.Start && position < link.End) return link;
        return null;
    }

    /// <summary>
    /// What a triple-click at <paramref name="position"/> selects: a string's or key's contents without its
    /// quotes (the quotes too when it is empty), a number or keyword whole, and on punctuation or indentation
    /// the line's text without its indentation.
    /// </summary>
    public static (int Start, int Length) TokenAt(JsonTextDocument document, int position)
    {
        var text = document.Text;
        position = Math.Clamp(position, 0, text.Length);

        // Strictly inside first; then a caret on a token's right edge (a click on its last glyph's right
        // half lands there) — but not when that edge is also the next token's start.
        JsonColourSpan? hit = null;
        foreach (var span in document.Colours)
            if (position >= span.Start && position < span.Start + span.Length) { hit = span; break; }
        if (hit is null)
            foreach (var span in document.Colours)
                if (position == span.Start + span.Length) { hit = span; break; }

        if (hit is { } token)
        {
            if (token.Kind is JsonSpanKind.String or JsonSpanKind.Key && token.Length > 2)
                return (token.Start + 1, token.Length - 2);
            return (token.Start, token.Length);
        }

        var lineStart = position == 0 ? 0 : text.LastIndexOf('\n', position - 1) + 1;
        var lineEnd = text.IndexOf('\n', position);
        if (lineEnd < 0) lineEnd = text.Length;
        while (lineStart < lineEnd && text[lineStart] == ' ') lineStart++;
        return (lineStart, lineEnd - lineStart);
    }

    /// <summary>
    /// The colour spans cut at link edges, each piece marked as link or not. <paramref name="links"/> must
    /// be in text order and lie inside string spans, which <see cref="Links"/> guarantees.
    /// </summary>
    public static IReadOnlyList<JsonTextSegment> Segments(
        IReadOnlyList<JsonColourSpan> colours, IReadOnlyList<JsonLink> links)
    {
        var segments = new List<JsonTextSegment>(colours.Count + links.Count * 2);
        var next = 0;   // first link not yet behind us
        foreach (var span in colours)
        {
            var at = span.Start;
            var end = span.Start + span.Length;
            while (next < links.Count && links[next].End <= at) next++;
            for (var i = next; i < links.Count && links[i].Start < end; i++)
            {
                var link = links[i];
                if (link.Start > at) segments.Add(new JsonTextSegment(at, link.Start - at, span.Kind, false));
                var linkEnd = Math.Min(link.End, end);
                segments.Add(new JsonTextSegment(Math.Max(at, link.Start), linkEnd - Math.Max(at, link.Start), span.Kind, true));
                at = linkEnd;
            }
            if (at < end) segments.Add(new JsonTextSegment(at, end - at, span.Kind, false));
        }
        return segments;
    }
}
