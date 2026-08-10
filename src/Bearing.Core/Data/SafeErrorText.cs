using System.Text.RegularExpressions;

namespace Bearing.Core.Data;

/// <summary>
/// Turns a driver/runtime exception message into text that is safe to show the user.
/// <para>
/// The concrete hazard is <b>credentials</b>: a connect-time or parse-time failure from the driver can quote
/// the whole connection string, password included, and that string then lands in the results pane, the status
/// bar and the query log. Those values are redacted here.
/// </para>
/// <para>
/// Host, port and database are deliberately <b>kept</b>. This is a local tool showing the user the server
/// they themselves configured — and the connect path already names the endpoint on purpose
/// (<c>Could not connect to 'x' (host:port/db)</c>). Stripping it would remove the most useful half of every
/// network, TLS and DNS error while protecting nobody.
/// </para>
/// </summary>
public static class SafeErrorText
{
    // key=value up to the next ';' or line break — the shape credentials take inside a connection string.
    private static readonly Regex Credential = new(
        @"\b(?<key>password|passwd|pwd)\s*=\s*(?<value>[^;\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Redacted message for <paramref name="ex"/> (empty message → the exception's type name, so a
    /// blank error still says something).</summary>
    public static string Of(Exception ex)
        => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : Redact(ex.Message);

    /// <summary>Replace any <c>password=…</c> value with <c>***</c>. Pure — unit-tested.</summary>
    public static string Redact(string message)
        => Credential.Replace(message, m => $"{m.Groups["key"].Value}=***");
}
