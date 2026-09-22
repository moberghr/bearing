namespace Bearing.Updates;

/// <summary>
/// Adding and removing one directory in a <c>;</c>-separated PATH, as text.
/// <para>
/// Pure, and separated from the registry on purpose: the editing is where PATH corruption comes from, and
/// it is the half that can be tested exhaustively without touching a machine's environment. The I/O half
/// (<see cref="WindowsPath"/>) is then small enough to read in one go.
/// </para>
/// </summary>
public static class WindowsPathEntry
{
    /// <summary>
    /// <paramref name="path"/> with <paramref name="directory"/> present exactly once, appended if it was
    /// not there. Returns null when nothing needs to change, so a caller can avoid a pointless write — and
    /// a pointless write here is not free: it rewrites the user's PATH and broadcasts a change to every
    /// running program.
    /// </summary>
    public static string? WithEntry(string path, string directory)
    {
        var entries = Split(path);
        if (entries.Any(e => Same(e, directory))) return null;

        // Appended rather than prepended: this command is not one anybody should be shadowing an existing
        // tool with, and a PATH entry that jumps the queue is how an install changes what an unrelated
        // command means.
        entries.Add(directory);
        return string.Join(';', entries);
    }

    /// <summary>
    /// <paramref name="path"/> without <paramref name="directory"/>, or null when it was not there.
    /// Removes every copy: an install that ran twice before this was idempotent could have left two.
    /// </summary>
    public static string? WithoutEntry(string path, string directory)
    {
        var entries = Split(path);
        var kept = entries.Where(e => !Same(e, directory)).ToList();
        return kept.Count == entries.Count ? null : string.Join(';', kept);
    }

    /// <summary>
    /// Whether two entries name the same directory. Case-insensitive because Windows paths are, and
    /// trailing separators and surrounding quotes are ignored because PATH entries are hand-edited and
    /// arrive in every spelling — <c>C:\x</c>, <c>C:\x\</c> and <c>"C:\x"</c> are one directory, and
    /// treating them as three is how an uninstall leaves an entry behind.
    /// </summary>
    private static bool Same(string a, string b)
        => string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string entry)
        => entry.Trim().Trim('"').TrimEnd('\\', '/');

    /// <summary>
    /// The entries, with empty ones dropped. An empty entry means "the current directory" to the Windows
    /// loader, so a PATH that gains one from a stray <c>;</c> is a real hazard — and this is the one place
    /// that would introduce one, by joining a list that still held it.
    /// </summary>
    private static List<string> Split(string path)
        => path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
