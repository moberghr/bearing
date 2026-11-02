using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Bearing.Updates;
using Xunit;

namespace Bearing.Updates.Tests;

/// <summary>
/// `bearing` / `bearing file.sql` through the AppImage held the terminal until the window closed, because the
/// image's own process was the window. The image now restarts itself in the background when a shell started
/// it, and only then.
/// </summary>
public class AppImageDetachTests : IDisposable
{
    private const string Image = "/home/u/Applications/BearingSql.AppImage";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bearing-detach", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void From_a_terminal_inside_the_image_it_detaches()
        => Assert.True(AppImageDetach.ShouldDetach(Image, terminalAttached: true, marker: null));

    /// <summary>The desktop launcher and the update restart have no terminal: one mount, as before.</summary>
    [Fact]
    public void Without_a_terminal_it_stays()
        => Assert.False(AppImageDetach.ShouldDetach(Image, terminalAttached: false, marker: null));

    [Fact]
    public void Outside_an_image_it_stays()
    {
        Assert.False(AppImageDetach.ShouldDetach(null, terminalAttached: true, marker: null));
        Assert.False(AppImageDetach.ShouldDetach("", terminalAttached: true, marker: null));
    }

    /// <summary>The relaunched image inherits the terminal check's answer only if its streams leak; the marker
    /// makes "only once" not depend on that.</summary>
    [Fact]
    public void The_relaunched_image_never_relaunches_again()
        => Assert.False(AppImageDetach.ShouldDetach(Image, terminalAttached: true, marker: "1"));

    /// <summary>
    /// The relaunch itself, against a stand-in image that outlives the call by seconds: the call must come
    /// back at once, with the arguments and the marker delivered intact.
    /// </summary>
    [SkippableFact]
    public async Task Relaunch_returns_at_once_and_hands_over_arguments_and_the_marker()
    {
        Skip.If(OperatingSystem.IsWindows(), "AppImages are Linux-only");

        Directory.CreateDirectory(_dir);
        var image = Path.Combine(_dir, "fake.AppImage");
        var output = Path.Combine(_dir, "out.txt");
        File.WriteAllText(image,
            "#!/bin/sh\nprintf '%s|' \"$BEARING_DETACHED\" \"$@\" > \"" + output + ".tmp\"\n"
            + "mv \"" + output + ".tmp\" \"" + output + "\"\nsleep 5\n");
        File.SetUnixFileMode(image, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var clock = Stopwatch.StartNew();
        Assert.True(AppImageDetach.Relaunch(image, ["/tmp/a b.sql", "--demo"], TextWriter.Null));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");   // not the child's 5 s

        for (var i = 0; i < 100 && !File.Exists(output); i++) await Task.Delay(50);
        Assert.Equal("1|/tmp/a b.sql|--demo|", File.ReadAllText(output));
    }

    /// <summary>
    /// <c>&amp;</c> makes the hand-off report success whatever the second image does, and its terminal is
    /// gone — so what it says on the way down has to land in the log, or a launch that fails leaves nothing.
    /// </summary>
    [SkippableFact]
    public async Task What_the_relaunched_image_prints_to_stderr_lands_in_the_log()
    {
        Skip.If(OperatingSystem.IsWindows(), "AppImages are Linux-only");

        Directory.CreateDirectory(_dir);
        var image = Path.Combine(_dir, "broken.AppImage");
        var log = Path.Combine(_dir, "detached.log");
        File.WriteAllText(image, "#!/bin/sh\necho 'cannot mount' >&2\nexit 1\n");
        File.SetUnixFileMode(image, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.True(AppImageDetach.Relaunch(image, [], TextWriter.Null, log));

        for (var i = 0; i < 100 && !(File.Exists(log) && File.ReadAllText(log).Length > 0); i++) await Task.Delay(50);
        Assert.Equal("cannot mount\n", File.ReadAllText(log));
    }

    /// <summary>A log that can't be written must not stop the image starting: a failed redirect would.</summary>
    [SkippableFact]
    public async Task An_unwritable_log_still_starts_the_image()
    {
        Skip.If(OperatingSystem.IsWindows(), "AppImages are Linux-only");

        Directory.CreateDirectory(_dir);
        var image = Path.Combine(_dir, "fake.AppImage");
        var output = Path.Combine(_dir, "started");
        File.WriteAllText(image, "#!/bin/sh\ntouch \"" + output + "\"\n");
        File.SetUnixFileMode(image, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.True(AppImageDetach.Relaunch(image, [], TextWriter.Null, Path.Combine(_dir, "no-such-dir", "detached.log")));

        for (var i = 0; i < 100 && !File.Exists(output); i++) await Task.Delay(50);
        Assert.True(File.Exists(output), "the image never started");
    }
}
