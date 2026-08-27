using System;
using System.Diagnostics;

namespace Bearing.App.Services;

/// <summary>
/// Hands a URL to the user's browser. Best-effort in the same way <see cref="FileReveal"/> is: a machine
/// with no browser, or a sandbox that blocks launching one, must not turn a link into an error dialog.
/// </summary>
public static class BrowserLaunch
{
    /// <summary>
    /// Open <paramref name="url"/> in the default browser. Only <c>http</c> and <c>https</c> are accepted —
    /// the URLs here come off a network feed, and <c>UseShellExecute</c> will happily launch a local
    /// executable or a <c>file:</c> path if handed one, so the scheme check is the guard, not a formality.
    /// </summary>
    /// <returns>False if the URL was refused or nothing could be launched.</returns>
    public static bool Open(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        // Not a pattern: Uri.UriSchemeHttp is a static readonly field, not a constant.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        try
        {
            // UseShellExecute is what makes this the *default browser* rather than a hardcoded one: it
            // routes through ShellExecute on Windows, `open` on macOS and xdg-open on Linux.
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
