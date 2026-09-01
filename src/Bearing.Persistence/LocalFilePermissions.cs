using System;
using System.IO;

namespace Bearing.Persistence;

/// <summary>What hardening a local file's permissions achieved.</summary>
public enum FileHardening
{
    /// <summary>Narrowed to the owner alone.</summary>
    OwnerOnly,

    /// <summary>Nothing to do: the platform's own per-user directory already restricts it.</summary>
    PlatformDefault,

    /// <summary>The file is not there (yet).</summary>
    Missing,

    /// <summary>The attempt failed. Best-effort: persistence must not take the app down (§5.2).</summary>
    Failed,
}

/// <summary>
/// Restricts a local store's files to their owner (#22).
/// <para>
/// The query log is a plaintext record of the SQL a user ran against production, and SQLite created it with
/// whatever the process umask allowed — 0644 under a common umask, which on a shared or multi-user machine is
/// world-readable. Nothing about the log's contents justifies that.
/// </para>
/// <para>
/// Best-effort by design, like every other persistence path here: a filesystem that cannot express the mode
/// (a FAT volume, a mounted share) must not stop the app from starting. The outcome is returned rather than
/// thrown so the caller can report the posture instead of guessing at it — the same rule the secret store
/// follows (§1.1).
/// </para>
/// </summary>
public static class LocalFilePermissions
{
    /// <summary>
    /// Narrow <paramref name="path"/> to owner read/write.
    /// <para>
    /// A no-op on Windows, and deliberately so: <c>%LOCALAPPDATA%</c> is already ACL'd to the user, and
    /// rewriting a DACL by hand is how you end up with a file the user's own tools cannot open. Reported as
    /// <see cref="FileHardening.PlatformDefault"/> rather than as success, because claiming to have hardened
    /// something is worse than saying the platform did it.
    /// </para>
    /// </summary>
    public static FileHardening Harden(string path)
    {
        if (OperatingSystem.IsWindows()) return FileHardening.PlatformDefault;
        if (!File.Exists(path)) return FileHardening.Missing;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return FileHardening.OwnerOnly;
        }
        catch (Exception)
        {
            // A filesystem that cannot express the mode, or a file someone else owns. Not fatal.
            return FileHardening.Failed;
        }
    }

    /// <summary>
    /// Harden a SQLite database <b>and its sidecars</b>. In WAL mode the <c>-wal</c> file holds committed
    /// pages that have not been checkpointed yet, so a log hardened without it is a log whose most recent
    /// entries are still readable by anyone.
    /// </summary>
    public static FileHardening HardenDatabase(string dbPath)
    {
        var main = Harden(dbPath);
        Harden(dbPath + "-wal");
        Harden(dbPath + "-shm");
        Harden(dbPath + "-journal");
        return main;
    }
}
