using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Bearing.App.Services;

/// <summary>
/// Puts HTML on the clipboard as the platform's own <b>HTML flavour</b>, not as HTML source text — which is
/// the whole difference between pasting a table into Teams / Outlook / Word / Excel and pasting a wall of
/// <c>&lt;table&gt;</c> markup. A plain-text alternative rides along on the same clipboard entry, so a target
/// that only takes text (a terminal, an editor) still gets something useful.
/// <para>
/// The flavour's name is platform-specific and is passed to the OS as-is, so each one is spelled the way that
/// system spells it: <c>text/html</c> on Linux/BSD (X11 and Wayland both use mime types), <c>public.html</c>
/// on macOS (a UTI, not a mime type), and <c>HTML Format</c> on Windows — where the payload is not plain HTML
/// at all but CF_HTML, a byte-offset header followed by the markup (see <see cref="CfHtml"/>).
/// </para>
/// <para>
/// <b>Every flavour is written as bytes</b>, never as a string. Measured on X11/XWayland with Avalonia 12.1:
/// a platform format created with <c>CreateStringPlatformFormat</c> is <i>advertised</i> on the clipboard but
/// serves an <b>empty payload</b> — so a target asks for HTML, gets zero bytes, and falls back to the plain
/// text, which is exactly the "Teams pastes plaintext" symptom this class exists to fix. Byte formats serve
/// correctly. Bytes also settle the encoding question rather than leaving it to the backend, which CF_HTML
/// needs anyway (its header counts bytes).
/// </para>
/// </summary>
public static class HtmlClipboard
{
    /// <summary>
    /// Copy <paramref name="html"/> as rich HTML plus <paramref name="plainText"/> as the fallback.
    /// Best-effort: a platform whose clipboard rejects the flavour still gets the plain text, and a failure
    /// never throws at the caller (a copy is not worth taking the app down for).
    /// </summary>
    public static async Task SetAsync(TopLevel? top, string html, string plainText)
    {
        if (top?.Clipboard is not { } clipboard) return;
        try
        {
            var (flavour, payload) = Payload(html, CurrentTarget());
            var item = new DataTransferItem();
            item.SetText(plainText);
            item.Set(DataFormat.CreateBytesPlatformFormat(flavour), payload);

            // Deliberately NOT disposed here, and deliberately not in a `using`: the clipboard keeps the
            // transfer and serves the data lazily when a target actually pastes (which is how X11 and Wayland
            // clipboards work), then disposes it itself once it is unused. Disposing it on the way out of this
            // method would leave a dead object on the clipboard and the paste would come back empty.
            var transfer = new DataTransfer();
            transfer.Add(item);
            await clipboard.SetDataAsync(transfer);
        }
        catch (Exception)
        {
            // The rich flavour didn't take (unsupported backend, a headless run). Plain text is better than
            // nothing, and better than an error dialog over a Copy.
            try { await clipboard.SetTextAsync(plainText); } catch { /* clipboard is best-effort */ }
        }
    }

    /// <summary>A minimal HTML document around a fragment. Word and LibreOffice are happier with a document
    /// than with a bare fragment; browsers and Teams accept either.</summary>
    internal static string Document(string fragment)
        => "<html><head><meta charset=\"utf-8\"></head><body>" + fragment + "</body></html>";

    /// <summary>How the running OS names and shapes its HTML clipboard flavour.</summary>
    internal enum RichHtmlTarget
    {
        /// <summary>Linux/BSD, X11 and Wayland alike: the <c>text/html</c> mime type.</summary>
        MimeHtml,

        /// <summary>macOS: the <c>public.html</c> Uniform Type Identifier.</summary>
        AppleHtml,

        /// <summary>Windows: the <c>HTML Format</c> clipboard format, whose payload is CF_HTML.</summary>
        WindowsCfHtml,
    }

    internal static RichHtmlTarget CurrentTarget()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? RichHtmlTarget.WindowsCfHtml
         : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? RichHtmlTarget.AppleHtml
         : RichHtmlTarget.MimeHtml;

    /// <summary>The flavour name and the exact bytes to publish under it. Split out from
    /// <see cref="SetAsync"/> so all three platforms' shapes are testable from any one of them — the Windows
    /// payload in particular can't be checked by hand on a Linux box.</summary>
    internal static (string Flavour, byte[] Bytes) Payload(string html, RichHtmlTarget target) => target switch
    {
        RichHtmlTarget.WindowsCfHtml => ("HTML Format", CfHtml.Wrap(html)),
        RichHtmlTarget.AppleHtml => ("public.html", Encoding.UTF8.GetBytes(Document(html))),
        _ => ("text/html", Encoding.UTF8.GetBytes(Document(html))),
    };
}

/// <summary>
/// The Windows CF_HTML clipboard payload: a small plain-ASCII header whose four numbers are <b>byte</b>
/// offsets into the payload itself, followed by the markup with fragment markers. Pure and unit-tested,
/// because it is both fiddly (the offsets describe the string that contains them) and unverifiable on this
/// machine — get a number wrong and Windows silently pastes nothing.
/// </summary>
internal static class CfHtml
{
    private const string StartFragmentMarker = "<!--StartFragment-->";
    private const string EndFragmentMarker = "<!--EndFragment-->";

    /// <summary>Wrap an HTML fragment as a CF_HTML payload, UTF-8 encoded.</summary>
    public static byte[] Wrap(string fragment)
    {
        var body = "<html><head><meta charset=\"utf-8\"></head><body>"
                 + StartFragmentMarker + fragment + EndFragmentMarker
                 + "</body></html>";

        // Two passes: the header's length depends on the numbers, and the numbers depend on the header's
        // length. Fixed-width (10-digit, zero-padded) values are what makes the second pass exact — the
        // header written with placeholders is byte-for-byte the same size as the final one.
        var header = Header(0, 0, 0, 0);
        var headerBytes = Encoding.UTF8.GetByteCount(header);
        var startHtml = headerBytes;
        var endHtml = headerBytes + Encoding.UTF8.GetByteCount(body);
        var startFragment = headerBytes
            + Encoding.UTF8.GetByteCount(body[..(body.IndexOf(StartFragmentMarker, StringComparison.Ordinal) + StartFragmentMarker.Length)]);
        var endFragment = headerBytes
            + Encoding.UTF8.GetByteCount(body[..body.IndexOf(EndFragmentMarker, StringComparison.Ordinal)]);

        return Encoding.UTF8.GetBytes(Header(startHtml, endHtml, startFragment, endFragment) + body);
    }

    private static string Header(int startHtml, int endHtml, int startFragment, int endFragment)
        => "Version:0.9\r\n"
         + $"StartHTML:{Offset(startHtml)}\r\n"
         + $"EndHTML:{Offset(endHtml)}\r\n"
         + $"StartFragment:{Offset(startFragment)}\r\n"
         + $"EndFragment:{Offset(endFragment)}\r\n";

    private static string Offset(int value) => value.ToString("D10", CultureInfo.InvariantCulture);
}
