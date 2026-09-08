using System;
using Bearing.Core.Workspace;

namespace Bearing.Sql;

/// <summary>
/// The dials on <see cref="SqlFormat"/>. Layout and case only — there is deliberately nothing here that
/// could change what a statement means, which is what keeps the safety argument in <see cref="SqlFormat"/>
/// true for every combination rather than for the defaults.
/// <para>
/// A record with defaults rather than parameters on <c>Format</c>, so a new dial does not change the
/// signature every caller and test uses.
/// </para>
/// </summary>
public sealed record SqlFormatOptions
{
    /// <summary>What the app formats with when nothing says otherwise — and what the tests use, so the
    /// golden layouts pin the shipped defaults rather than a test-only combination.</summary>
    public static readonly SqlFormatOptions Default = new();

    /// <summary>What to do with keyword case. See <see cref="SqlKeywordCase"/>.</summary>
    public SqlKeywordCase KeywordCase { get; init; } = SqlKeywordCase.Upper;

    /// <summary>
    /// Line ending to write, or null to take it from the text being formatted.
    /// <para>
    /// It exists for the caller who has more context than the formatter does. Formatting a <i>selection</i>
    /// hands over only the selected fragment, and a single-line fragment out of a CRLF document contains no
    /// CRLF to detect — so the formatter would emit LF and splice bare LFs into a CRLF buffer, which is the
    /// whole-file diff the detection exists to avoid. The editor knows the document's convention; the
    /// formatter only ever sees the piece.
    /// </para>
    /// </summary>
    public string? Newline { get; init; }

    private readonly int _indentWidth = 4;

    /// <summary>
    /// Spaces per level of nesting. Clamped to 1..16 on the way in: this is arithmetic in the writer, and a
    /// zero would silently produce output with no structure at all while a negative would throw from
    /// somewhere far away from the setting that caused it.
    /// </summary>
    public int IndentWidth
    {
        get => _indentWidth;
        init => _indentWidth = Math.Clamp(value, 1, 16);
    }
}
