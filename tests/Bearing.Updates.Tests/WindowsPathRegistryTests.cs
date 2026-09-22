using System;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Xunit;

namespace Bearing.Updates.Tests;

/// <summary>
/// The registry half of the PATH edit, driven against a <b>scratch key</b> — never
/// <c>HKCU\Environment</c>, which is the developer's own PATH and not a fixture.
/// <para>
/// The editing rules are pinned purely in <see cref="WindowsPathEntryTests"/>. What only this can answer is
/// how the code behaves against a real <see cref="RegistryKey"/>: a value that is not there, and a value
/// whose kind has to survive the round trip. Both were wrong — <c>GetValueKind</c> reports absence by
/// throwing, and asking it before reading turned a profile with no <c>Path</c> value into a silent no-op.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsPathRegistryTests : IDisposable
{
    private const string Dir = @"C:\Users\x\AppData\Local\BearingSql\current";
    private const string PathValue = "Path";

    private readonly string _scratch = @"Software\Bearing.Tests\" + Guid.NewGuid().ToString("N");

    private RegistryKey Open() => Registry.CurrentUser.CreateSubKey(_scratch, writable: true)!;

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_scratch, throwOnMissingSubKey: false); } catch { }
    }

    private static bool Add(RegistryKey key, string directory)
        => WindowsPath.Apply(key, path => WindowsPathEntry.WithEntry(path, directory));

    private static bool Remove(RegistryKey key, string directory)
        => WindowsPath.Apply(key, path => WindowsPathEntry.WithoutEntry(path, directory));

    /// <summary>
    /// The bug this file exists for. A clean profile has no <c>Path</c> under its Environment key at all, and
    /// the first version asked for the value's <i>kind</i> before reading it — which throws when there is no
    /// value. The throw reached the outer best-effort catch and the install reported nothing, so `bearing`
    /// simply never became a command for that user.
    /// </summary>
    [SkippableFact]
    public void A_user_with_no_path_value_at_all_still_gets_the_entry()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The registry is Windows'.");
        using var key = Open();

        Assert.Null(key.GetValue(PathValue));      // the state under test: no value, not an empty one

        Assert.True(Add(key, Dir));
        Assert.Equal(Dir, key.GetValue(PathValue, "", RegistryValueOptions.DoNotExpandEnvironmentNames));

        // And created as REG_EXPAND_SZ: a PATH is the canonical one, and a REG_SZ PATH silently stops
        // resolving the first %SystemRoot% anybody adds to it later.
        Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind(PathValue));
    }

    /// <summary>
    /// A PATH holding <c>%SystemRoot%</c> must come back holding it, not holding today's expansion of it —
    /// the classic way an installer corrupts one, and invisible until the machine changes underneath.
    /// </summary>
    [SkippableFact]
    public void An_unexpanded_variable_survives_a_real_round_trip()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The registry is Windows'.");
        using var key = Open();
        key.SetValue(PathValue, @"%SystemRoot%;%SystemRoot%\System32", RegistryValueKind.ExpandString);

        Assert.True(Add(key, Dir));

        var written = (string)key.GetValue(PathValue, "", RegistryValueOptions.DoNotExpandEnvironmentNames)!;
        Assert.Equal($@"%SystemRoot%;%SystemRoot%\System32;{Dir}", written);
        Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind(PathValue));
    }

    /// <summary>A PATH that was a plain string stays one: rewriting someone's REG_SZ as REG_EXPAND_SZ is a
    /// change to their configuration that adding a directory did not ask for.</summary>
    [SkippableFact]
    public void An_existing_kind_is_not_changed()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The registry is Windows'.");
        using var key = Open();
        key.SetValue(PathValue, @"C:\Windows", RegistryValueKind.String);

        Assert.True(Add(key, Dir));

        Assert.Equal(RegistryValueKind.String, key.GetValueKind(PathValue));
    }

    /// <summary>
    /// Every update re-asserts the entry, so this is the common case rather than an edge one — and a write
    /// that changed nothing would still rewrite the user's PATH and broadcast a change to every running
    /// program. The <c>false</c> is what stops the broadcast.
    /// </summary>
    [SkippableFact]
    public void Re_adding_writes_nothing_and_says_so()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The registry is Windows'.");
        using var key = Open();
        key.SetValue(PathValue, $@"C:\Windows;{Dir}", RegistryValueKind.ExpandString);

        Assert.False(Add(key, Dir));
        Assert.Equal($@"C:\Windows;{Dir}", key.GetValue(PathValue));
    }

    [SkippableFact]
    public void Uninstalling_takes_the_entry_back_off()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The registry is Windows'.");
        using var key = Open();
        key.SetValue(PathValue, $@"C:\Windows;{Dir}\", RegistryValueKind.ExpandString);

        Assert.True(Remove(key, Dir));

        Assert.Equal(@"C:\Windows", key.GetValue(PathValue));
    }

    [SkippableFact]
    public void Removing_from_a_path_that_never_had_it_writes_nothing()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The registry is Windows'.");
        using var key = Open();
        key.SetValue(PathValue, @"C:\Windows", RegistryValueKind.ExpandString);

        Assert.False(Remove(key, Dir));
    }

    /// <summary>An install followed by an uninstall leaves the PATH exactly as it was found, which is the
    /// whole promise of the uninstall half.</summary>
    [SkippableFact]
    public void Install_then_uninstall_is_the_identity()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The registry is Windows'.");
        using var key = Open();
        const string original = @"%SystemRoot%;C:\Program Files\Git\cmd";
        key.SetValue(PathValue, original, RegistryValueKind.ExpandString);

        Add(key, Dir);
        Add(key, Dir);      // an update re-asserting it
        Remove(key, Dir);

        Assert.Equal(
            original, key.GetValue(PathValue, "", RegistryValueOptions.DoNotExpandEnvironmentNames));
    }
}
