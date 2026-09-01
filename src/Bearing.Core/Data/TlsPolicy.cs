using System;
using System.Collections.Generic;
using System.Linq;

namespace Bearing.Core.Data;

/// <summary>
/// What a <see cref="TlsMode"/> actually guarantees, and which mode a connection is running under (#23).
/// Pure, so the wording a user is warned with and the precedence between the typed field and the legacy
/// options bag are both unit-testable without a server (§2.5).
/// <para>
/// The distinction this exists to make visible: <b>encryption and identity are separate</b>.
/// <see cref="TlsMode.Require"/> reads like the secure choice and is the one people pick, but it accepts any
/// certificate — it stops an eavesdropper and does nothing about a man in the middle. Only the Verify modes
/// check who is on the other end.
/// </para>
/// </summary>
public static class TlsPolicy
{
    /// <summary>The mode a fresh connection gets: the driver's own default, so adding this setting changed no
    /// existing connection's behaviour.</summary>
    public const TlsMode Default = TlsMode.Prefer;

    /// <summary>The options-bag key this setting used to live in, before it had a field of its own.</summary>
    public const string LegacyOptionKey = "sslmode";

    /// <summary>
    /// What a <b>new</b> connection should start on — deliberately not <see cref="Default"/>, which exists
    /// only so an older project file keeps the behaviour it already had.
    /// <para>
    /// Anything reached over a network gets <see cref="TlsMode.Require"/>: this tool holds database
    /// credentials, and a mode that may or may not encrypt and never says which is not a defensible starting
    /// point for one. Loopback gets <see cref="TlsMode.Prefer"/>, because that traffic does not cross a
    /// network to be intercepted on — and because the usual first connection anyone makes is to a local
    /// container, where the stock image has TLS switched off. Requiring it there would make the first thing
    /// the tool ever does fail, which teaches the user to turn the setting off rather than to keep it.
    /// </para>
    /// </summary>
    public static TlsMode DefaultFor(string? host)
        => IsLoopback(host) ? TlsMode.Prefer : TlsMode.Require;

    /// <summary>
    /// Whether a host is this machine. Text-only on purpose: no DNS lookup, because a dialog cannot block on
    /// one and a name that resolves to a loopback address today may not tomorrow. An unrecognised name is
    /// treated as remote, which is the safe direction to be wrong in.
    /// </summary>
    public static bool IsLoopback(string? host)
    {
        var h = host?.Trim().Trim('[', ']');
        if (string.IsNullOrEmpty(h)) return false;
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (h == "::1" || h == "0:0:0:0:0:0:0:1") return true;
        // Any 127.0.0.0/8 address, not just 127.0.0.1 — 127.0.0.53 and friends are equally local.
        return h.StartsWith("127.", StringComparison.Ordinal)
               && h.Split('.') is { Length: 4 } parts
               && parts.All(part => byte.TryParse(part, out _));
    }

    /// <summary>Whether the mode guarantees the session is encrypted. <see cref="TlsMode.Prefer"/> does not:
    /// it may or may not be, depending on what the server offered, and it never reports which.</summary>
    public static bool Encrypts(TlsMode mode)
        => mode is TlsMode.Require or TlsMode.VerifyCa or TlsMode.VerifyFull;

    /// <summary>Whether the mode checks the server's identity, rather than only encrypting the pipe to it.</summary>
    public static bool VerifiesServer(TlsMode mode)
        => mode is TlsMode.VerifyCa or TlsMode.VerifyFull;

    /// <summary>
    /// Whether the mode is worth warning about. Everything short of <see cref="TlsMode.VerifyFull"/> is:
    /// each leaves a real attack open, and the warning names which one rather than scolding in general.
    /// </summary>
    public static bool NeedsWarning(TlsMode mode) => mode != TlsMode.VerifyFull;

    /// <summary>Short label for the picker.</summary>
    public static string Label(TlsMode mode) => mode switch
    {
        TlsMode.Prefer => "Prefer — encrypt if offered",
        TlsMode.Require => "Require — encrypted, unverified",
        TlsMode.VerifyCa => "Verify CA — trusted issuer",
        TlsMode.VerifyFull => "Verify Full — issuer and hostname",
        TlsMode.Disable => "Disable — never encrypt",
        _ => mode.ToString(),
    };

    /// <summary>
    /// What this mode leaves open, in one sentence, said plainly enough to act on. Never a bare "insecure":
    /// the user has to be able to tell which of the two guarantees is missing, because that decides whether
    /// the risk matters on their network.
    /// </summary>
    public static string Advice(TlsMode mode) => mode switch
    {
        TlsMode.Disable =>
            "Everything, your password included, crosses the network in the clear. Only safe over a loopback "
            + "or a tunnel you already trust.",
        TlsMode.Prefer =>
            "TLS is used only if the server offers it, and you are not told either way — so this connection "
            + "may be sending your password in the clear right now. Require encryption to rule that out.",
        TlsMode.Require =>
            "Encrypted, but the server's certificate is accepted without being checked: an eavesdropper is "
            + "stopped, an impersonator is not. Verify Full also checks who answered.",
        TlsMode.VerifyCa =>
            "The certificate must chain to a trusted authority, but it need not have been issued for this "
            + "host — a valid certificate for another server is accepted. Verify Full checks the hostname too.",
        TlsMode.VerifyFull => "Encrypted, and the server's certificate must be valid for this host.",
        _ => "",
    };

    /// <summary>The modes to offer, strongest first — the order a picker should read in, so the safe choice
    /// is the one at the top rather than the one buried under the familiar default.</summary>
    public static IReadOnlyList<TlsMode> Choices { get; } =
    [
        TlsMode.VerifyFull,
        TlsMode.VerifyCa,
        TlsMode.Require,
        TlsMode.Prefer,
        TlsMode.Disable,
    ];

    /// <summary>
    /// The mode a connection is really running under.
    /// <para>
    /// The typed field wins. It falls back to a legacy <c>sslmode</c> in the options bag only while the field
    /// is still at its default, which is what keeps projects written before the field existed — and DBeaver
    /// imports, which write the bag — behaving exactly as they did. Once the connection dialog writes the
    /// field it also strips the bag entry, so there is never a second source of truth for a security setting.
    /// </para>
    /// </summary>
    public static TlsMode Resolve(ConnectionInfo info)
        => info.Tls != Default ? info.Tls : FromOptions(info.Options) ?? info.Tls;

    /// <summary>A legacy bag entry parsed into a mode, or null when there is none or it is unreadable. An
    /// unreadable value is not silently treated as insecure — the field's default stands.</summary>
    public static TlsMode? FromOptions(IReadOnlyDictionary<string, string> options)
    {
        var key = options.Keys.FirstOrDefault(k => string.Equals(k, LegacyOptionKey, StringComparison.OrdinalIgnoreCase));
        return key is not null ? Parse(options[key]) : null;
    }

    /// <summary>
    /// Parse an <c>sslmode</c> spelling. Accepts what Postgres writes in a connection string, hyphens and
    /// underscores included (<c>verify-full</c>, <c>verify_ca</c>), because that is what a copied connection
    /// string and a DBeaver export actually contain.
    /// </summary>
    public static TlsMode? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().Replace("-", "").Replace("_", "").ToLowerInvariant() switch
        {
            "disable" or "disabled" or "off" => TlsMode.Disable,
            "prefer" or "preferred" or "allow" => TlsMode.Prefer,
            "require" or "required" or "on" => TlsMode.Require,
            "verifyca" => TlsMode.VerifyCa,
            "verifyfull" => TlsMode.VerifyFull,
            _ => null,
        };
    }

    /// <summary>The <c>sslmode</c> spelling for a mode — Postgres's own, for display and for copying into a
    /// connection string.</summary>
    public static string ToSslMode(TlsMode mode) => mode switch
    {
        TlsMode.Disable => "disable",
        TlsMode.Prefer => "prefer",
        TlsMode.Require => "require",
        TlsMode.VerifyCa => "verify-ca",
        TlsMode.VerifyFull => "verify-full",
        _ => "prefer",
    };
}
