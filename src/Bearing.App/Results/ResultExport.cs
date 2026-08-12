using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Bearing.App.ViewModels;

namespace Bearing.App.Results;

/// <summary>A result-set export target. CSV is text; Excel is a real workbook (<see cref="XlsxWriter"/>).</summary>
public enum ExportFormat
{
    Csv,
    Xlsx,
}

/// <summary>
/// Writes a <see cref="TableBlock"/> to a file. The formatting is pure (<see cref="TableFormats"/> /
/// <see cref="XlsxWriter"/>); this adds the two things a file needs — a sensible name and an atomic write —
/// and nothing else, so the view-model that orchestrates an export owns no I/O of its own.
/// </summary>
public static class ResultExport
{
    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Xlsx => "xlsx",
        _ => "csv",
    };

    public static string Label(ExportFormat format) => format switch
    {
        ExportFormat.Xlsx => "Excel workbook",
        _ => "CSV",
    };

    /// <summary>
    /// Write <paramref name="block"/> to <paramref name="path"/>.
    /// <para>
    /// Via a temp file in the same directory and an atomic move, like every other write in the app: an export
    /// interrupted halfway (full disk, a cancelled run, a crash) must not leave a truncated file that looks
    /// like a complete one — especially not on top of a previous good export the user chose to overwrite.
    /// </para>
    /// </summary>
    public static void Write(string path, TableBlock block, ExportFormat format, string sheetName)
    {
        var temp = path + ".tmp";
        try
        {
            using (var file = File.Create(temp))
            {
                if (format == ExportFormat.Xlsx) XlsxWriter.Write(file, block, sheetName);
                else
                {
                    // A BOM so Excel opens a UTF-8 CSV as UTF-8 rather than guessing the system code page and
                    // mangling every non-ASCII value.
                    using var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    writer.Write(TableFormats.Csv(block));
                }
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort (§5.2) */ }
        }
    }

    /// <summary>
    /// A default file name for a result: the table it came from when that's known, else the tab name, else
    /// "result" — with a timestamp, since exporting the same query twice is the normal case and silently
    /// overwriting yesterday's file is not what anyone means by Export.
    /// </summary>
    public static string SuggestedName(ResultSetViewModel result, string? tabName, DateTime now, ExportFormat format)
    {
        var stem = result.EditTarget?.Table
            ?? (string.IsNullOrWhiteSpace(tabName) ? null : tabName)
            ?? "result";
        return $"{Slug(stem)}-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.{Extension(format)}";
    }

    /// <summary>The Excel sheet name for a result — the source table when known, else "Result".</summary>
    public static string SheetName(ResultSetViewModel result)
        => XlsxWriter.SafeSheetName(result.EditTarget?.Table ?? "Result");

    /// <summary>A file-name-safe stem: path separators and the platform's invalid characters become '-',
    /// runs collapse, and the result is capped so a long tab title can't produce an unopenable name.</summary>
    internal static string Slug(string text)
    {
        // Both separators explicitly: on Unix, GetInvalidFileNameChars() reports only '/' and NUL, so a tab
        // named "a\b" would keep its backslash — legal here, but a landmine the moment the file is opened on
        // (or copied to) Windows.
        var invalid = Path.GetInvalidFileNameChars().Concat(['.', ' ', '/', '\\']).ToHashSet();
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var mapped = invalid.Contains(ch) ? '-' : ch;
            if (mapped == '-' && (sb.Length == 0 || sb[^1] == '-')) continue;
            sb.Append(mapped);
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0) slug = "result";
        return slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }
}
