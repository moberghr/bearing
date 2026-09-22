using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Bearing.Updates;

/// <summary>
/// Putting the install directory on the user's PATH, so `bearing` is a command rather than a file — and
/// taking it off again on uninstall.
/// <para>
/// <b>Per user, never the machine.</b> Bearing installs to <c>%LocalAppData%</c> without elevation, so the
/// machine PATH is neither ours to edit nor reachable; <c>HKCU\Environment</c> is the matching scope.
/// </para>
/// <para>
/// <b>Read unexpanded, always.</b> <c>Environment.GetEnvironmentVariable(…, User)</c> expands
/// <c>%SystemRoot%</c> and the like, so reading with it and writing the result back would bake today's
/// values into the user's PATH permanently — the classic way an installer corrupts one. The registry is
/// read with <see cref="RegistryValueOptions.DoNotExpandEnvironmentNames"/> and written back with whatever
/// kind it already had.
/// </para>
/// <para>
/// Best-effort throughout (§5.2): a PATH that cannot be edited is a command the user types a full path to,
/// which is a much smaller problem than an install that fails because of it.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsPath
{
    private const string EnvironmentKey = "Environment";
    private const string PathValue = "Path";

    /// <summary>Add <paramref name="directory"/> if it is not already there. Returns whether anything was
    /// written, which is what tells a caller whether to broadcast.</summary>
    public static bool Add(string directory) => Edit(path => WindowsPathEntry.WithEntry(path, directory));

    /// <summary>Remove every copy of <paramref name="directory"/>.</summary>
    public static bool Remove(string directory) => Edit(path => WindowsPathEntry.WithoutEntry(path, directory));

    /// <summary>
    /// The kind to write the value back as — the existing one, or <c>ExpandString</c> when there is no value
    /// yet. <see cref="RegistryKey.GetValueKind"/> reports absence by throwing rather than by a return value,
    /// which is the whole reason this is a method: a PATH is the canonical REG_EXPAND_SZ, and creating one as
    /// REG_SZ means the first <c>%SystemRoot%</c> anybody adds to it later stops resolving.
    /// </summary>
    private static RegistryValueKind KindOf(RegistryKey key)
    {
        try { return key.GetValueKind(PathValue); }
        catch (IOException) { return RegistryValueKind.ExpandString; }
    }

    private static bool Edit(Func<string, string?> change)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKey, writable: true);
            if (key is null) return false;

            if (!Apply(key, change)) return false;

            Broadcast();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Read the value, apply <paramref name="change"/>, write it back — the whole of the registry work, and
    /// the only part a test can drive, since the real one is the developer's own PATH. A test points this at
    /// a scratch key instead (internal, per this repo's <c>InternalsVisibleTo</c> pattern).
    /// </summary>
    /// <returns>Whether anything was written, which is what tells the caller whether to broadcast.</returns>
    internal static bool Apply(RegistryKey key, Func<string, string?> change)
    {
        // A user who has never had a PATH of their own has no value here at all, which is not an error —
        // and this is the order that makes that true. GetValueKind *throws* on a missing value, so
        // asking it first turned a clean profile with no HKCU\Environment\Path into a silent no-op via
        // Edit's catch: `bearing` never reached PATH, with nothing said. Found by review.
        var current = key.GetValue(PathValue, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string
                      ?? "";

        var kind = KindOf(key);

        if (change(current) is not { } updated) return false;

        // The kind is preserved: a PATH holding %SystemRoot% must stay REG_EXPAND_SZ or those entries
        // stop resolving, and rewriting it as REG_SZ is the second classic way to break one.
        key.SetValue(PathValue, updated, kind);
        return true;
    }

    /// <summary>
    /// Tell everything already running that the environment changed. Without it the new PATH reaches only
    /// processes started after the next sign-in — including, confusingly, a terminal the user already had
    /// open when they installed.
    /// <para>
    /// Sent with a timeout and without waiting for a reply, because a single unresponsive window would
    /// otherwise hang an installer that has nothing else left to do.
    /// </para>
    /// </summary>
    private static void Broadcast()
    {
        try
        {
            SendMessageTimeoutW(
                HwndBroadcast, WmSettingChange, UIntPtr.Zero, "Environment",
                SmtoAbortIfHung, millisecondsTimeout: 1000, out _);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The PATH is written either way; this only decides how soon it is noticed.
        }
    }

    private static readonly nint HwndBroadcast = 0xffff;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeoutW(
        nint hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint millisecondsTimeout,
        out UIntPtr result);
}
