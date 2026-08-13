using System.Diagnostics;
using System.Text;

namespace Bearing.Persistence;

/// <summary>
/// Runs a small OS helper CLI and captures its result. Shared by the keychain stores that shell out —
/// <see cref="SecretToolSecretStore"/> (libsecret) and <see cref="MacKeychainSecretStore"/>
/// (<c>security</c>) — so the process plumbing has one implementation: arguments passed as a list rather
/// than a command line (no shell, no quoting to get wrong), optional stdin, and both output pipes drained
/// concurrently.
/// </summary>
internal static class CliRunner
{
    /// <param name="stdin">Written to the child's standard input as UTF-8 and then closed. This is how a
    /// password reaches a helper without appearing in its argument list.</param>
    public static async Task<CliResult> RunAsync(
        string file, string[] args, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}.");
        if (stdin is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(stdin);
            await proc.StandardInput.BaseStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            proc.StandardInput.Close();
        }

        // Drain both pipes concurrently. Reading one to the end while the other fills its buffer deadlocks;
        // the outputs here are tiny, but the failure mode would be a hung app rather than a wrong answer.
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return new CliResult(proc.ExitCode, stdout.Result, stderr.Result);
    }
}

internal readonly record struct CliResult(int Exit, string Stdout, string Stderr)
{
    /// <summary>What to put in an exception message: the helper's own stderr when it said something,
    /// otherwise the exit code, which is all we have.</summary>
    public string Detail => Stderr.Trim() is { Length: > 0 } err ? err : $"exit code {Exit}";
}
