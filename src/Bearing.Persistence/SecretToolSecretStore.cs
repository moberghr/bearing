using System.Diagnostics;
using System.Text;
using Bearing.Core.Workspace;

namespace Bearing.Persistence;

/// <summary>
/// OS keychain via the freedesktop Secret Service (libsecret's <c>secret-tool</c> CLI). Secrets are
/// keyed by attributes {app=bearing, connection=&lt;guid&gt;} and never touch any file on disk.
/// </summary>
public sealed class SecretToolSecretStore : ISecretStore
{
    // Matches the app dir name so a dev profile (BEARING_PROFILE) keeps its keychain entries
    // separate from the installed app's — same isolation as config/data dirs.
    private static string App => BearingPaths.AppDirName;
    public bool IsSecure => true;

    public async Task SetPasswordAsync(Guid connectionId, string password, CancellationToken ct)
    {
        var (exit, _, err) = await RunAsync(
            new[] { "store", "--label", $"Bearing connection {connectionId}", "app", App, "connection", connectionId.ToString() },
            stdin: password, ct);
        if (exit != 0)
            throw new InvalidOperationException($"secret-tool store failed: {err}");
    }

    public async Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken ct)
    {
        var (exit, stdout, _) = await RunAsync(
            new[] { "lookup", "app", App, "connection", connectionId.ToString() }, stdin: null, ct);
        if (exit != 0) return null;                 // not found
        return stdout.TrimEnd('\n');
    }

    public async Task DeleteAsync(Guid connectionId, CancellationToken ct)
        => await RunAsync(new[] { "clear", "app", App, "connection", connectionId.ToString() }, stdin: null, ct);

    /// <summary>Store→lookup→clear a probe secret to confirm a keyring is actually reachable.</summary>
    public static async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            var probe = Guid.NewGuid();
            var store = new SecretToolSecretStore();
            await store.SetPasswordAsync(probe, "probe", ct);
            var value = await store.GetPasswordAsync(probe, ct);
            await store.DeleteAsync(probe, ct);
            return value == "probe";
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int exit, string stdout, string stderr)> RunAsync(
        string[] args, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start secret-tool.");
        if (stdin is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(stdin);
            await proc.StandardInput.BaseStream.WriteAsync(bytes, ct);
            proc.StandardInput.Close();
        }

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, stdout, stderr);
    }
}
