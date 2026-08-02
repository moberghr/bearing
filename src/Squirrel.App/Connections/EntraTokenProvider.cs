using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Squirrel.Core.Data;

namespace Squirrel.App.Connections;

/// <summary>
/// Obtains an Entra access token by shelling out to the Azure CLI
/// (<c>az account get-access-token --resource &lt;resource&gt; --output json</c>). No Azure SDK dependency —
/// it reuses the user's existing <c>az login</c>. The token becomes the Postgres password; its expiry is
/// carried on the <see cref="Credential"/> so the session manager can disconnect before it goes stale.
/// </summary>
public sealed class EntraTokenProvider : IEntraTokenProvider
{
    /// <summary>Default AAD resource/scope for Azure Database for PostgreSQL.</summary>
    public const string DefaultResource = "https://ossrdbms-aad.database.windows.net";

    /// <summary>Per-connection <see cref="ConnectionInfo.Options"/> key to override the resource above.</summary>
    public const string ResourceOptionKey = "entra.resource";

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    public async Task<Credential> GetTokenAsync(ConnectionInfo info, CancellationToken ct)
    {
        var resource = info.Options.TryGetValue(ResourceOptionKey, out var r) && !string.IsNullOrWhiteSpace(r)
            ? r.Trim()
            : DefaultResource;

        var (exit, stdout, stderr) = await RunAzAsync(resource, ct);
        if (exit != 0)
            throw new InvalidOperationException(FormatAzError(exit, stderr));

        try { return ParseTokenResponse(stdout); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Could not read the Entra token returned by az: {ex.Message}", ex);
        }
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

    private static async Task<(int Exit, string Out, string Err)> RunAzAsync(string resource, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("az")
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
                "Azure CLI (az) was not found on PATH. Install it and run `az login`.", ex);
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
