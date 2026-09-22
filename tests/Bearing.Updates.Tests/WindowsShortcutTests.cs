using System;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using Bearing.Updates;
using Xunit;

namespace Bearing.Updates.Tests;

/// <summary>
/// The COM half, against real <c>.lnk</c> files this test writes in a temp directory — never the user's own
/// Start Menu, which is why <c>WindowsShortcut.Retarget(path, …)</c> is internal and the public entry that
/// walks the real folders is not called here.
/// <para>
/// The deciding is pinned in <see cref="WindowsShortcutTargetTests"/>. What only this can answer is whether
/// the shell actually reads and writes the field: a <c>.lnk</c> is a binary format driven through late-bound
/// COM, so "it compiles" says nothing about whether a shortcut comes back changed.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsShortcutTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bearing-shortcut", Guid.NewGuid().ToString("N"));

    private readonly string _app;

    public WindowsShortcutTests()
    {
        _app = Path.Combine(_dir, "current");
        Directory.CreateDirectory(_app);
        // Real files, so the shell has something to resolve: a .lnk to a path that does not exist can be
        // saved but is a different thing from the one an installer left behind.
        File.WriteAllText(Path.Combine(_app, "bearing.exe"), "");
        File.WriteAllText(Path.Combine(_app, "bearing-app.exe"), "");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, string target)
    {
        var path = Path.Combine(_dir, name);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        var shell = Activator.CreateInstance(shellType)!;
        var link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path])!;
        var linkType = link.GetType();
        linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, [target]);
        linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        return path;
    }

    private static string? TargetOf(string path)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        var shell = Activator.CreateInstance(shellType)!;
        var link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path])!;
        return link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
    }

    private bool Retarget(string path)
        => WindowsShortcut.Retarget(path, _app, "bearing.exe", "bearing-app.exe");

    /// <summary>The migration, end to end: the shortcut an upgrading machine is left holding comes back
    /// aimed at the window rather than at the command.</summary>
    [SkippableFact]
    public void A_stale_shortcut_is_rewritten_on_disk()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Shortcuts are Windows'.");
        var lnk = Write("Bearing.lnk", Path.Combine(_app, "bearing.exe"));

        Assert.True(Retarget(lnk));

        Assert.Equal(Path.Combine(_app, "bearing-app.exe"), TargetOf(lnk));
    }

    /// <summary>Every update after 1.1.0 runs this again, so the second pass has to write nothing — and
    /// "nothing" has to be observable, because that is what keeps the shell from re-reading the file.</summary>
    [SkippableFact]
    public void Running_it_again_writes_nothing()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Shortcuts are Windows'.");
        var lnk = Write("Bearing.lnk", Path.Combine(_app, "bearing.exe"));
        Retarget(lnk);
        var after = File.GetLastWriteTimeUtc(lnk);

        Assert.False(Retarget(lnk));

        Assert.Equal(after, File.GetLastWriteTimeUtc(lnk));
        Assert.Equal(Path.Combine(_app, "bearing-app.exe"), TargetOf(lnk));
    }

    /// <summary>
    /// The safety condition, proved against a real file rather than only against the string logic: a
    /// shortcut to some other <c>bearing.exe</c> is left exactly as it was. Rewriting one of these is the
    /// failure that cannot be undone by re-running an installer.
    /// </summary>
    [SkippableFact]
    public void A_shortcut_to_another_program_is_left_untouched()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Shortcuts are Windows'.");
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var other = Path.Combine(elsewhere, "bearing.exe");
        File.WriteAllText(other, "");
        var lnk = Write("Someone else's.lnk", other);

        Assert.False(Retarget(lnk));

        Assert.Equal(other, TargetOf(lnk));
    }

    /// <summary>A fresh 1.1.0 install already points at the window; the migration must not report work it
    /// did not do, because the caller counts the result.</summary>
    [SkippableFact]
    public void A_fresh_install_is_a_no_op()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Shortcuts are Windows'.");
        var lnk = Write("Bearing.lnk", Path.Combine(_app, "bearing-app.exe"));

        Assert.False(Retarget(lnk));
        Assert.Equal(Path.Combine(_app, "bearing-app.exe"), TargetOf(lnk));
    }

    /// <summary>The working directory is set with the target, so the two halves of the shortcut cannot
    /// disagree about which install they mean.</summary>
    [SkippableFact]
    public void The_working_directory_follows_the_target()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Shortcuts are Windows'.");
        var lnk = Write("Bearing.lnk", Path.Combine(_app, "bearing.exe"));

        Retarget(lnk);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        var shell = Activator.CreateInstance(shellType)!;
        var link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [lnk])!;
        var workDir = link.GetType()
            .InvokeMember("WorkingDirectory", BindingFlags.GetProperty, null, link, null) as string;

        Assert.Equal(_app, workDir);
    }

    /// <summary>It runs inside an installer callback, where an exception is the one outcome that is worse
    /// than doing nothing.</summary>
    [SkippableFact]
    public void A_file_that_is_not_a_shortcut_is_declined_rather_than_thrown_over()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Shortcuts are Windows'.");
        var notALink = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(notALink, "not a shortcut");

        Assert.False(Retarget(notALink));
        Assert.False(Retarget(Path.Combine(_dir, "does-not-exist.lnk")));
    }
}
