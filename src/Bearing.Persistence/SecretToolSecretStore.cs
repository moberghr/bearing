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

    /// <summary>Always: the keychain is where a password belongs, so there's nothing to opt into.</summary>
    public bool CanStore => true;

    public async Task SetPasswordAsync(Guid connectionId, string password, CancellationToken ct)
    {
        var (exit, _, err) = await RunAsync(
            new[] { "store", "--label", $"Bearing connection {connectionId}", "app", App, "connection", connectionId.ToString() },
            stdin: password, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"secret-tool store failed: {err}");
    }

    public async Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken ct)
    {
        var (exit, stdout, _) = await RunAsync(
            new[] { "lookup", "app", App, "connection", connectionId.ToString() }, stdin: null, ct).ConfigureAwait(false);
        if (exit != 0) return null;                 // not found
        return stdout.TrimEnd('\n');
    }

    public async Task DeleteAsync(Guid connectionId, CancellationToken ct)
    {
        var (exit, _, err) = await RunAsync(
            new[] { "clear", "app", App, "connection", connectionId.ToString() }, stdin: null, ct).ConfigureAwait(false);
        if (exit == 0) return;

        // `clear` exits 1 for "nothing matched" *and* for a real failure (locked or absent keyring), with an
        // empty stderr in the first case — so the exit code alone can't be trusted either way. Check the
        // postcondition instead: if the secret is gone, the delete did its job; if it's still there, the
        // caller must hear about it rather than believe a credential was removed when it wasn't.
        if (await GetPasswordAsync(connectionId, ct).ConfigureAwait(false) is not null)
            throw new InvalidOperationException(
                $"secret-tool clear failed — the password is still in the keyring{(err.Length > 0 ? $": {err}" : ".")}");
    }

    /// <summary>Store→lookup→clear a probe secret to confirm a keyring is actually reachable.</summary>
    public static async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            var probe = Guid.NewGuid();
            var store = new SecretToolSecretStore();
            await store.SetPasswordAsync(probe, "probe", ct).ConfigureAwait(false);
            var value = await store.GetPasswordAsync(probe, ct).ConfigureAwait(false);
            await store.DeleteAsync(probe, ct).ConfigureAwait(false);
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
            await proc.StandardInput.BaseStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            proc.StandardInput.Close();
        }

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return (proc.ExitCode, stdout, stderr);
    }
}
