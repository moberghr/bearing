using System;

namespace Bearing.Updates;

/// <summary>
/// Deciding whether a shortcut points at the executable it was meant to, as text — the pure half of
/// <see cref="WindowsShortcut"/>, and the half where getting it wrong would rewrite something that is not
/// ours.
/// <para>
/// <b>Why this exists at all.</b> Velopack writes the Start Menu shortcut when the app is <i>installed</i>,
/// aimed straight at <c>…\current\&lt;mainExe&gt;</c> rather than at the stub beside it, and an update does
/// not rewrite it — <c>Update.exe</c> has no shortcut command. 1.1.0 renamed the window's executable from
/// <c>bearing.exe</c> to <c>bearing-app.exe</c> so the CLI could have the plain name (§1.11), so every
/// machine upgrading from 1.0.x keeps a shortcut aimed at what is now the <i>command</i>.
/// </para>
/// <para>
/// It still opens Bearing — the command with no arguments launches the window, which is exactly why that is
/// its behaviour — but through a console program, so a console flashes on the way. A one-time migration,
/// like <c>LegacySecretFiles.Purge</c>: it repairs the shortcut once and is a no-op on every machine and
/// every update after.
/// </para>
/// </summary>
public static class WindowsShortcutTarget
{
    /// <summary>
    /// Where this shortcut should point instead, or <b>null</b> when it should be left alone — which is the
    /// answer for everything except our own stale shortcut.
    /// <para>
    /// Three conditions, all required, because the alternative is editing a shortcut somebody else made:
    /// the target has to sit in <paramref name="appDirectory"/>, its file name has to be
    /// <paramref name="staleExe"/>, and the corrected file has to be a different name. A shortcut already
    /// aimed at <paramref name="appExe"/> returns null, so running this twice writes nothing.
    /// </para>
    /// </summary>
    /// <param name="target">The shortcut's current target, as read from the <c>.lnk</c>.</param>
    /// <param name="appDirectory">The directory the app was installed into.</param>
    /// <param name="staleExe">The file name the shortcut used to be aimed at (<c>bearing.exe</c>).</param>
    /// <param name="appExe">The file name it should be aimed at now (<c>bearing-app.exe</c>).</param>
    public static string? Corrected(string? target, string appDirectory, string staleExe, string appExe)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(appDirectory)) return null;
        if (string.Equals(staleExe, appExe, StringComparison.OrdinalIgnoreCase)) return null;

        // Quotes because a target is sometimes stored quoted, and whitespace because a hand-edited one can
        // carry it. Neither changes which file is meant.
        var trimmed = target.Trim().Trim('"').Trim();
        if (trimmed.Length == 0) return null;

        // Split by hand rather than with Path.GetFileName/GetDirectoryName, because those follow the
        // *running* platform's separator rules and this is always a Windows path: on Linux they treat the
        // whole of `C:\…\bearing.exe` as one file name with no directory, so every comparison below
        // silently answers "not ours". The shipped behaviour was never wrong — this only ever runs on
        // Windows — but logic that means different things on different hosts cannot be tested on either,
        // and CI caught it on the pure half within minutes.
        var separator = trimmed.LastIndexOfAny(['\\', '/']);
        if (separator <= 0) return null;                       // no directory at all is not our shortcut

        var targetDirectory = trimmed[..separator];
        var targetName = trimmed[(separator + 1)..];

        if (!string.Equals(targetName, staleExe, StringComparison.OrdinalIgnoreCase)) return null;
        if (!SameDirectory(targetDirectory, appDirectory)) return null;

        // The directory as the shortcut spelled it, so the repair changes the file name and nothing else.
        return $@"{targetDirectory}\{appExe}";
    }

    /// <summary>
    /// Whether two directory paths name the same place, as far as text can say: case-insensitively, with
    /// trailing separators and either separator treated alike. Deliberately <b>not</b> a filesystem check —
    /// this runs inside an installer callback, and the answer must not depend on what exists at the time.
    /// </summary>
    private static bool SameDirectory(string left, string right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
        => path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
}
