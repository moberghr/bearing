using System.Collections.Generic;
using Bearing.App.ViewModels;

namespace Bearing.App.Results;

/// <summary>What Copy as ▸ puts on the clipboard. <see cref="Tsv"/> is what plain Copy (Ctrl+C) does and is
/// listed here only so one menu can drive them all.</summary>
public enum CopyFormat
{
    /// <summary>Tab-separated, no header — the spreadsheet paste format (plain Ctrl+C).</summary>
    Tsv,

    /// <summary>RFC 4180 CSV with a header row.</summary>
    Csv,

    /// <summary>A GitHub-flavoured Markdown table.</summary>
    Markdown,

    /// <summary>An array of row objects keyed by column name.</summary>
    Json,

    /// <summary>A styled table, placed on the clipboard as the platform's HTML flavour so it pastes into
    /// Teams / Outlook / Word / Excel as a real table.</summary>
    Html,

    /// <summary>One <c>insert into … values (…);</c> per row.</summary>
    SqlInsert,

    /// <summary>The values as a comma-separated list of SQL literals, for the inside of an
    /// <c>in (…)</c>.</summary>
    InList,
}

/// <summary>
/// Turns a selection into clipboard text for a given <see cref="CopyFormat"/>. The pure formatting lives in
/// <see cref="TableFormats"/>; this is the one place that knows which format the menu items mean and where
/// the SQL target name comes from.
/// </summary>
public static class CopyRenderer
{
    /// <summary>The alternatives to plain Copy, in menu order — most-reached-for first. One list, so the
    /// context menu and the registered commands can't drift apart. <see cref="CopyFormat.Tsv"/> is absent
    /// because that is what plain Copy already does.</summary>
    public static IReadOnlyList<CopyFormat> Alternatives { get; } =
    [
        CopyFormat.Html,
        CopyFormat.InList,
        CopyFormat.Csv,
        CopyFormat.Markdown,
        CopyFormat.Json,
        CopyFormat.SqlInsert,
    ];

    /// <summary>Used when a result isn't a single editable table (a join, a view, an expression select), so
    /// there is no table name to insert into. Deliberately conspicuous: the SQL is a starting point that has
    /// to be pointed at a table, and a plausible-looking wrong name would be worse.</summary>
    public const string UnknownTable = "«table»";

    /// <summary>Menu label for a format. Named by <i>where the result goes</i> rather than by its file type
    /// where that is what the user is actually choosing between.</summary>
    public static string Label(CopyFormat format) => format switch
    {
        CopyFormat.Tsv => "Tab-separated (no header)",
        CopyFormat.Csv => "CSV",
        CopyFormat.Markdown => "Markdown table",
        CopyFormat.Json => "JSON",
        CopyFormat.Html => "Table (Teams, Outlook, Word, Excel)",
        CopyFormat.SqlInsert => "SQL INSERT statements",
        // Named for the SQL construct, not for its punctuation: "comma-separated values" sat one line above
        // "CSV", which is the same words for a different thing. The example carries the rest of the meaning
        // (values only, no parentheses) in less space than a sentence would.
        CopyFormat.InList => "SQL IN list — 1, 2, 'abc'",
        _ => format.ToString(),
    };

    /// <summary>Render <paramref name="block"/> in <paramref name="format"/>. <paramref name="result"/> is
    /// consulted only for the SQL target's schema/table.</summary>
    public static string Render(ResultSetViewModel result, TableBlock block, CopyFormat format) => format switch
    {
        CopyFormat.Csv => TableFormats.Csv(block),
        CopyFormat.Markdown => TableFormats.Markdown(block),
        CopyFormat.Json => TableFormats.Json(block),
        CopyFormat.Html => TableFormats.Html(block),
        // The result carries the engine it came from, so the pasted SQL is valid where the rows live.
        CopyFormat.InList => TableFormats.InList(block, result.Traits),
        CopyFormat.SqlInsert => TableFormats.SqlInsert(
            block, result.Traits, result.EditTarget?.Schema, result.EditTarget?.Table ?? UnknownTable),
        // Tsv keeps its own gap-preserving shape (see TableBlock.ForSelection) and is produced by
        // GridSelectionOps straight off the selection, so it never reaches here.
        _ => TableFormats.Csv(block),
    };
}
