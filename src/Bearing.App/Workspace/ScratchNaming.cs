using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Bearing.App.Workspace;

/// <summary>
/// Names for scratch files: <c>yyyy-MM-dd-NN.sql</c>, dated so the folder reads chronologically and
/// numbered so several buffers on one day don't collide. Pure — the caller supplies today's date and the
/// names already in the folder — so ordering and collision behaviour are testable without a filesystem.
/// </summary>
public static class ScratchNaming
{
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// The first free <c>yyyy-MM-dd-NN.sql</c> for <paramref name="date"/>. Counting restarts each day and
    /// fills gaps, so deleting a scratch file frees its number rather than leaving a permanent hole.
    /// Comparison is case-insensitive, matching the rest of the scripts tree.
    /// </summary>
    public static string NextFileName(DateOnly date, IEnumerable<string> existingFileNames)
    {
        var taken = existingFileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stamp = date.ToString(DateFormat, CultureInfo.InvariantCulture);
        for (var n = 1; ; n++)
        {
            var candidate = $"{stamp}-{n:00}.sql";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> lives in <paramref name="scratchDirectory"/> (directly or nested).
    /// This — not "has no file" — is what makes a tab scratch once buffers are file-backed, so a tab whose
    /// file has been moved out of the folder stops being scratch by construction.
    /// </summary>
    public static bool IsUnderScratch(string? path, string? scratchDirectory)
    {
        if (path is null || scratchDirectory is null) return false;
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scratchDirectory));
        var full = Path.GetFullPath(path);
        return full.Length > dir.Length
            && full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
            && (full[dir.Length] == Path.DirectorySeparatorChar || full[dir.Length] == Path.AltDirectorySeparatorChar);
    }
}
