using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;

namespace Bearing.Sessions;

/// <summary>
/// Obtains an Entra access token by shelling out to the Azure CLI
/// (<c>az account get-access-token --resource &lt;resource&gt; --output json</c>). No Azure SDK dependency —
/// it reuses the user's existing <c>az login</c>. The token becomes the connection's password; its expiry is
/// carried on the <see cref="Credential"/> so the session manager can disconnect before it goes stale. A
/// connection with <see cref="ConnectionInfo.UserFromEntraLogin"/> also gets the signed-in account's name on
/// the same credential, read off the token (<see cref="WantsUser"/>, <see cref="UserFromToken"/>).
/// <para>
/// The <em>resource</em> the token is minted for is per engine: Azure Database for PostgreSQL and Azure SQL
/// are separate audiences, and a token for one is rejected by the other. It therefore comes from
/// <see cref="ProviderTraits.EntraResource"/> keyed by the connection's provider — the Postgres value is
/// unchanged, so an existing Entra connection keeps minting exactly the token it minted before.
/// </para>
/// </summary>
public sealed class EntraTokenProvider : IEntraTokenProvider
{
    /// <summary>Default AAD resource/scope for Azure Database for PostgreSQL. Kept as the name every
    /// caller and test knew; <see cref="ResourceFor"/> is what actually decides per connection.</summary>
    public const string DefaultResource = "https://ossrdbms-aad.database.windows.net";

    /// <summary>Per-connection <see cref="ConnectionInfo.Options"/> key to override the resource
    /// entirely — still the last word, so a non-public cloud or a preview audience needs no code change.</summary>
    public const string ResourceOptionKey = "entra.resource";

    /// <summary>The resource to mint for: the connection's explicit override, else its engine's audience.
    /// Pure, so the per-engine choice is testable without invoking az.
    /// <para>
    /// The key is matched case-insensitively rather than by exact spelling: a bag deserialized from
    /// project.json is an ordinary ordinal dictionary, so a hand-written <c>Entra.Resource</c> would
    /// otherwise be ignored in silence and the token minted for the default audience.
    /// </para>
    /// </summary>
    public static string ResourceFor(ConnectionInfo info)
        => Override(info.Options) ?? ProviderTraits.For(info).EntraResource;

    private static string? Override(IReadOnlyDictionary<string, string> options)
    {
        foreach (var (key, value) in options)
            if (string.Equals(key, ResourceOptionKey, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        return null;
    }

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    public async Task<Credential> GetTokenAsync(ConnectionInfo info, CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAzAsync(ResourceFor(info), ct);
        if (exit != 0)
            throw new InvalidOperationException(FormatAzError(exit, stderr));

        Credential credential;
        try { credential = ParseTokenResponse(stdout); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Could not read the Entra token returned by az: {ex.Message}", ex);
        }
        // Read off the token itself rather than asked of az separately: the name then belongs to the identity
        // the token was minted for by construction, and no Graph call is needed that a tenant may block.
        return WantsUser(info) ? credential with { User = UserFromToken(credential.Secret!) } : credential;
    }

    /// <summary>True when this connection's login name comes from az rather than from its User field: it asked
    /// for that, and its engine's Entra login takes a user name at all (SQL Server's does not — there the
    /// token is the identity, and reading a name off it would be work whose answer is thrown away).</summary>
    public static bool WantsUser(ConnectionInfo info)
        => info.CredentialKind == CredentialKind.EntraToken
           && info.UserFromEntraLogin
           && ProviderTraits.For(info).EntraTakesUser;

    /// <summary>
    /// The signed-in account's user principal name, as Entra-authenticated Postgres expects the role to be
    /// named, out of the access token's claims: <c>upn</c> (v1 tokens), else <c>preferred_username</c> (v2).
    /// Pure, for the same reason as <see cref="ParseTokenResponse"/>.
    /// <para>
    /// The signature is not checked, and need not be: this only names the role, and the server verifies the
    /// token it is sent. <c>unique_name</c> is deliberately not a fallback — for a guest it is
    /// <c>live.com#…</c>, which is no role's name.
    /// </para>
    /// <para>
    /// No name is refused rather than passed on: a connect with an empty user name fails at the server with a
    /// message about the role, not about az, and the cause is several steps away from there.
    /// </para>
    /// </summary>
    public static string UserFromToken(string jwt)
    {
        string? upn = null;
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length >= 2)
            {
                using var claims = JsonDocument.Parse(Base64UrlDecode(parts[1]));
                upn = Claim(claims.RootElement, "upn") ?? Claim(claims.RootElement, "preferred_username");
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new InvalidOperationException($"Could not read the signed-in user from the Entra token: {ex.Message}", ex);
        }
        return upn ?? throw new InvalidOperationException(
            "The Entra token names no signed-in user. A service principal or managed identity has no user "
            + "principal name — untick \"User is whoever az is signed in as\" and type the role name instead.");
    }

    private static string? Claim(JsonElement claims, string name)
        => claims.ValueKind == JsonValueKind.Object
           && claims.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim() : null;

    private static byte[] Base64UrlDecode(string segment)
    {
        var s = segment.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    /// <summary>Parse the JSON emitted by <c>az account get-access-token</c>. Pure and side-effect-free so it
    /// can be unit-tested without invoking az. Handles both the epoch <c>expires_on</c> (newer az) and the
    /// local wall-clock <c>expiresOn</c> field.</summary>
    public static Credential ParseTokenResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("accessToken", out var tokenEl) || tokenEl.GetString() is not { Length: > 0 } token)
            throw new FormatException("az did not return an accessToken.");

        DateTimeOffset? expires = null;

        // Prefer the unambiguous epoch form (az may emit it as a number or a numeric string).
        if (root.TryGetProperty("expires_on", out var epoch))
        {
            long secs = epoch.ValueKind == JsonValueKind.Number && epoch.TryGetInt64(out var n) ? n
                : epoch.ValueKind == JsonValueKind.String && long.TryParse(epoch.GetString(), out var s) ? s
                : 0;
            if (secs > 0) expires = DateTimeOffset.FromUnixTimeSeconds(secs);
        }

        // Fall back to the local-time string az has always emitted, e.g. "2026-07-31 15:04:05.000000".
        if (expires is null && root.TryGetProperty("expiresOn", out var local) && local.ValueKind == JsonValueKind.String
            && DateTime.TryParse(local.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
        {
            expires = new DateTimeOffset(dt);
        }

        return new Credential(token, expires);
    }

    /// <summary>Where az's usual installers put it on macOS / Linux. An app started from Finder, Spotlight or the
    /// dock inherits launchd's minimal PATH (<c>/usr/bin:/bin:/usr/sbin:/sbin</c>), which misses Homebrew (#167).</summary>
    private static readonly string[] WellKnownAzDirs = ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"];

    /// <summary>The az to start: the first <c>az</c> on <paramref name="path"/>, else in a well-known install
    /// directory, else bare <c>az</c> so a miss still reports "not found". Windows is left to its own lookup
    /// (az is <c>az.cmd</c> there). Pure over <paramref name="exists"/> so it is testable without az.</summary>
    public static string ResolveAz(string? path, Func<string, bool> exists, bool isWindows)
    {
        if (isWindows) return "az";
        var dirs = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Concat(WellKnownAzDirs);
        return dirs.Select(dir => Path.Combine(dir, "az")).FirstOrDefault(exists) ?? "az";
    }

    private static async Task<(int Exit, string Out, string Err)> RunAzAsync(string resource, CancellationToken ct)
    {
        var az = ResolveAz(Environment.GetEnvironmentVariable("PATH"), File.Exists, OperatingSystem.IsWindows());
        var psi = new ProcessStartInfo(az)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("account");
        psi.ArgumentList.Add("get-access-token");
        psi.ArgumentList.Add("--resource");
        psi.ArgumentList.Add(resource);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add("json");

        Process proc;
        try { proc = Process.Start(psi) ?? throw new InvalidOperationException("az could not be started."); }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Azure CLI (az) was not found. Install it and run `az login`.", ex);
        }

        using (proc)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(RunTimeout);
            var outTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var errTask = proc.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                throw new InvalidOperationException("Timed out waiting for az to return an Entra token.");
            }
            return (proc.ExitCode, await outTask, await errTask);
        }
    }

    private static string FormatAzError(int exit, string stderr)
    {
        var msg = string.IsNullOrWhiteSpace(stderr) ? $"az exited with code {exit}." : stderr.Trim();
        if (msg.Contains("az login", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("AADSTS", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("refresh token", StringComparison.OrdinalIgnoreCase))
            return "Entra sign-in required — run `az login`. (" + msg + ")";
        return "Could not obtain an Entra token from az: " + msg;
    }
}
