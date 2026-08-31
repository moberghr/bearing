using System;
using System.Collections.Generic;
using System.Linq;

namespace Bearing.App.Connections;

/// <summary>
/// The "/"-separated folder path used by <see cref="Bearing.Core.Data.ConnectionInfo.Folder"/> and
/// <see cref="Bearing.Core.Workspace.ProjectManifest.ConnectionFolders"/>. Pure string work, kept in one
/// place because a path arrives from three directions that each spell it slightly differently: the UI, a
/// hand-edited <c>project.json</c>, and a DBeaver import (which uses exactly this convention).
/// </summary>
public static class FolderPath
{
    public const char Separator = '/';

    /// <summary>The canonical form: segments trimmed, blanks dropped, rejoined. An all-blank path
    /// normalises to null — the panel's root — so <c>""</c>, <c>"/"</c> and <c>"  /  "</c> can't become
    /// three different folders that all render as nothing.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var joined = string.Join(Separator, Segments(path));
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>The path's non-blank segments, outermost first.</summary>
    public static IEnumerable<string> Segments(string? path)
        => (path ?? "").Split(Separator)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);

    /// <summary>The last segment — what the folder row is labelled. Null for the root.</summary>
    public static string? Name(string? path) => Segments(path).LastOrDefault();

    /// <summary>The containing folder, or null when the path is already top level.</summary>
    public static string? Parent(string? path)
    {
        var segments = Segments(path).ToList();
        return segments.Count <= 1 ? null : string.Join(Separator, segments.Take(segments.Count - 1));
    }

    /// <summary>Every path from the outermost folder down to <paramref name="path"/> itself, so a nested
    /// path can be materialised one level at a time.</summary>
    public static IEnumerable<string> Ancestry(string? path)
    {
        var acc = new List<string>();
        foreach (var segment in Segments(path))
        {
            acc.Add(segment);
            yield return string.Join(Separator, acc);
        }
    }

    /// <summary>Append a child segment to a parent path (either may be null/blank).</summary>
    public static string? Combine(string? parent, string child)
        => Normalize(string.IsNullOrWhiteSpace(parent) ? child : $"{parent}{Separator}{child}");

    /// <summary>Whether <paramref name="path"/> is <paramref name="ancestor"/> or sits inside it. Used to
    /// stop a folder being dragged into its own descendant, and to re-file a subtree when one is renamed.
    /// The root (null) contains everything.</summary>
    public static bool IsWithin(string? path, string? ancestor)
    {
        var a = Normalize(ancestor);
        if (a is null) return true;
        var p = Normalize(path);
        if (p is null) return false;
        return string.Equals(p, a, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(a + Separator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Re-root <paramref name="path"/> from under <paramref name="from"/> to under
    /// <paramref name="to"/>, leaving anything outside <paramref name="from"/> untouched. This is what moves
    /// a folder's whole subtree when it is renamed or dragged.</summary>
    public static string? Rebase(string? path, string from, string? to)
    {
        var f = Normalize(from);
        if (f is null || !IsWithin(path, f)) return Normalize(path);
        var tail = Normalize(path)![f.Length..].TrimStart(Separator);
        return tail.Length == 0 ? Normalize(to) : Combine(to, tail);
    }

    /// <summary>Strip the separator from a name typed by the user, so "a/b" cannot silently create a
    /// nested folder from a rename box that only ever asked for one segment.</summary>
    public static string SanitizeSegment(string name)
        => name.Replace(Separator, '-').Trim();
}
