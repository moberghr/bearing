using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bearing.Sessions;

/// <summary>
/// Runs the Azure CLI. One place for finding <c>az</c> (#167), starting it without a window, bounding it with a
/// timeout and reading both streams — shared by the Entra token (<see cref="EntraTokenProvider"/>) and the
/// server browser (<see cref="AzureCatalog"/>), so the two can't disagree about which az they ran.
/// </summary>
internal static class AzCli
{
    /// <summary>Where az's usual installers put it on macOS / Linux. An app started from Finder, Spotlight or the
    /// dock inherits launchd's minimal PATH (<c>/usr/bin:/bin:/usr/sbin:/sbin</c>), which misses Homebrew (#167).</summary>
    private static readonly string[] WellKnownAzDirs = ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"];

    /// <summary>See <see cref="EntraTokenProvider.ResolveAz"/>. Not consulted on Windows, where cmd.exe finds az
    /// (<see cref="WindowsArguments"/>).</summary>
    public static string Resolve(string? path, Func<string, bool> exists, bool isWindows)
    {
        if (isWindows)
        {
            return "az";
        }
        // Literal ':' and '/', not Path.PathSeparator / Path.Combine: those follow the host, not isWindows.
        var dirs = (path ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Concat(WellKnownAzDirs);
        return dirs.Select(dir => $"{dir.TrimEnd('/')}/az").FirstOrDefault(exists) ?? "az";
    }

    /// <summary>Run az with <paramref name="args"/>. Throws <see cref="InvalidOperationException"/> when az is
    /// missing or outlives <paramref name="timeout"/> (naming <paramref name="waitingFor"/>); a non-zero exit is
    /// returned, not thrown, so each caller words its own failure.</summary>
    public static async Task<(int Exit, string Out, string Err)> RunAsync(
        IEnumerable<string> args, TimeSpan timeout, string waitingFor, CancellationToken ct)
    {
        var isWindows = OperatingSystem.IsWindows();
        ProcessStartInfo psi;
        if (isWindows)
        {
            // az is az.cmd on Windows, a batch file. CreateProcess appends only ".exe" to a bare name, so starting
            // "az" directly never finds it; cmd.exe does, through PATH and PATHEXT (what Azure.Identity does too).
            psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            {
                Arguments = WindowsArguments(args),
            };
        }
        else
        {
            psi = new ProcessStartInfo(Resolve(Environment.GetEnvironmentVariable("PATH"), File.Exists, isWindows: false));
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
        }
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("az could not be started.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(NotFound, ex);
        }

        using (proc)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout);
            var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(cts.Token);
            // The stream reads sit inside the try too: a timeout that fires while the tail of a large answer is
            // still being read is the same timeout, and must not surface as a bare cancellation.
            try
            {
                await proc.WaitForExitAsync(cts.Token);
                var (stdout, stderr) = (await outTask, await errTask);
                // Through cmd.exe a missing az is no Win32Exception but cmd's own "is not recognized" exit.
                if (isWindows && proc.ExitCode == CmdNotFoundExit)
                {
                    throw new InvalidOperationException(NotFound);
                }
                return (proc.ExitCode, stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
                throw new InvalidOperationException($"Timed out waiting for az to return {waitingFor}.");
            }
        }
    }

    private const string NotFound = "Azure CLI (az) was not found. Install it and run `az login`.";

    /// <summary>cmd.exe's exit code when the command it was asked to run doesn't exist.</summary>
    private const int CmdNotFoundExit = 9009;

    /// <summary>
    /// cmd.exe's arguments for running az with <paramref name="args"/>: <c>/d</c> skips AutoRun, <c>/v:off</c>
    /// keeps <c>!</c> literal, and <c>/s /c "…"</c> runs the quoted line as written. Every argument is quoted, so
    /// <c>&amp;</c>, <c>|</c>, <c>&lt;</c>, <c>&gt;</c> and <c>^</c> are literal inside it. What quoting can't
    /// protect — a <c>"</c>, a <c>%</c> (expanded even inside quotes) or a line break — is refused rather than
    /// passed on. The arguments are ours (resource ids, URLs, a temp path), so a refusal is a bug report, not
    /// something a user can type. Pure.
    /// </summary>
    public static string WindowsArguments(IEnumerable<string> args)
    {
        var line = new List<string> { "az" };
        foreach (var arg in args)
        {
            if (arg.IndexOfAny(['"', '%', '\r', '\n']) >= 0)
            {
                throw new InvalidOperationException($"Refusing to pass {arg} to az through cmd.exe: it cannot be quoted safely.");
            }
            line.Add('"' + arg + '"');
        }
        return "/d /v:off /s /c \"" + string.Join(' ', line) + "\"";
    }

    /// <summary>The sentence for a non-zero az exit: a sign-in hint when that is what az complained about,
    /// otherwise az's own words about <paramref name="what"/>. One wording for every caller.</summary>
    public static string Describe(int exit, string stderr, string what)
    {
        var msg = string.IsNullOrWhiteSpace(stderr) ? $"az exited with code {exit}." : stderr.Trim();
        return NeedsLogin(msg)
            ? "Azure sign-in required — run `az login`. (" + msg + ")"
            : $"Could not get {what} from az: " + msg;
    }

    /// <summary>True when az's complaint is that nobody is signed in (or the sign-in has lapsed) — the one failure
    /// with a fix worth naming.</summary>
    public static bool NeedsLogin(string message)
        => message.Contains("az login", StringComparison.OrdinalIgnoreCase)
           || message.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
           || message.Contains("AADSTS", StringComparison.OrdinalIgnoreCase)
           || message.Contains("refresh token", StringComparison.OrdinalIgnoreCase);
}
