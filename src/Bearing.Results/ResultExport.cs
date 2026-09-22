using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;

namespace Bearing.Results;

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
    /// A failure that was the <b>file's</b>, not the data's — thrown only by
    /// <see cref="WriteCsvStreamAsync"/>, which is the one place here where both can happen inside one call.
    /// <para>
    /// Everything else in this class only writes, so a caller can attribute its failures by where it stood.
    /// A streaming write is reading from a server and writing to a disk at the same time, and the two want
    /// different sentences: "the query could not be run" about a directory that does not exist is wrong
    /// twice over, since it also puts a failed *statement* in the query log for a statement that ran fine.
    /// </para>
    /// </summary>
    public sealed class ExportWriteException(string path, Exception inner)
        : IOException($"Could not write {path}: {inner.Message}", inner)
    {
        public string Path { get; } = path;
    }

    /// <summary>What a streamed CSV turned out to hold, since nothing counted the rows beforehand.</summary>
    /// <param name="Rows">Rows written, header excluded.</param>
    /// <param name="Columns">Columns in the header row — 0 only when the statement returned no shape at all.</param>
    /// <param name="Truncated">The row ceiling stopped the read with rows still on the server.</param>
    public sealed record StreamedCsv(long Rows, int Columns, bool Truncated);

    /// <summary>
    /// Write a CSV from a row stream (<c>IQueryExecutor.StreamRowsAsync</c>), holding one batch in memory at
    /// a time rather than the whole result. Byte-for-byte what <see cref="Write"/> produces from the same
    /// rows — both go through <see cref="TableFormats.WriteCsvHeader"/> and
    /// <see cref="TableFormats.WriteCsvRow"/>, so there is no second rendering to drift.
    /// <para>
    /// The same temp-file-and-move as everything else here, and it earns its keep on this path in particular:
    /// a stream reports a failure by <b>throwing part-way</b>, with rows already written. The half-file goes
    /// in the <c>finally</c> and the caller is left with either the whole export or no file at all — never a
    /// truncated one that a script would happily read.
    /// </para>
    /// <para>
    /// Only the batch path can write xlsx: <see cref="XlsxWriter"/> needs every row to build the sheet, so a
    /// workbook export stays bounded by memory however it was asked for.
    /// </para>
    /// </summary>
    public static async Task<StreamedCsv> WriteCsvStreamAsync(
        string path, IAsyncEnumerable<RowBatch> batches, CancellationToken ct = default)
    {
        var temp = path + ".tmp";
        try
        {
            long rows = 0;
            var columns = 0;
            var truncated = false;
            var header = false;

            // Separately, and first: a path that cannot be created is the caller's mistake about the file
            // and has nothing to do with the statement. Everything after this point can fail for either
            // reason, so the two are told apart by where they were thrown rather than by guessing at an
            // exception type.
            FileStream created;
            try { created = File.Create(temp); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ExportWriteException(path, ex);
            }

            await using (var file = created)
            {
                // A BOM for the same reason Write has one: Excel guesses the system code page without it.
                var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                await using (writer.ConfigureAwait(false))
                {
                    await foreach (var batch in batches.WithCancellation(ct).ConfigureAwait(false))
                    {
                        // From the first batch, and every batch carries them — so an empty result still gets
                        // its header, which is what a reader like pandas needs to parse the file at all.
                        if (!header)
                        {
                            TableFormats.WriteCsvHeader(writer, batch.Columns);
                            columns = batch.Columns.Count;
                            header = true;
                        }
                        foreach (var row in batch.Rows) TableFormats.WriteCsvRow(writer, row, columns);
                        rows += batch.Rows.Count;
                        truncated = batch.Truncated;
                    }
                }
            }

            // A stream that named no columns is a statement with no result shape, not a result with no rows —
            // the second yields one empty batch and gets its header. Nothing is moved into place for the
            // first, so a caller that pointed --out at a statement like that keeps whatever was already
            // there rather than having it replaced by a file holding a BOM.
            if (!header) return new StreamedCsv(0, 0, false);

            // The move is the file's too: every row is read by now, so a failure here is about the
            // destination and not about the statement.
            try { File.Move(temp, path, overwrite: true); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ExportWriteException(path, ex);
            }

            return new StreamedCsv(rows, columns, truncated);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort (§5.2) */ }
        }
    }

    /// <summary>
    /// Write a whole run as one workbook, one sheet per entry (#12), through the same temp-file-and-move as
    /// <see cref="Write"/>: a workbook interrupted halfway must not look like a complete one, least of all on
    /// top of a previous good export.
    /// </summary>
    public static void WriteWorkbook(string path, IReadOnlyList<XlsxWriter.Sheet> sheets)
    {
        var temp = path + ".tmp";
        try
        {
            using (var file = File.Create(temp)) XlsxWriter.Write(file, sheets);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort (§5.2) */ }
        }
    }

    /// <summary>
    /// Write a table plus the notes that have to travel with it (#113's audit report), through the same
    /// temp-file-and-move as everything else here.
    /// <para>
    /// The notes are not decoration — they say what period the rows cover, whose activity it is, and whether
    /// the SQL was stored redacted — so they go with the file rather than in a dialog the reader of the file
    /// will never see. A workbook gets a second sheet. A CSV gets a <b>sibling</b> <c>.about.txt</c>, and stays a
    /// pure table with its header on row 1: RFC 4180 has no comment syntax, and the <c>#</c>-prefixed lines
    /// this used to write were not skipped by Excel, LibreOffice or pandas — the header landed on row 7, a
    /// note containing commas split into cells, and <c>read_csv</c> raised. The one place a CSV's notes can
    /// live without breaking the readers an auditor actually uses is beside it.
    /// </para>
    /// </summary>
    /// <returns>The paths written: the report, and for a CSV its notes file.</returns>
    public static IReadOnlyList<string> WriteReport(
        string path, TableBlock block, ExportFormat format, string sheetName, IReadOnlyList<string> notes)
    {
        if (format == ExportFormat.Xlsx)
        {
            Atomic(path, file => XlsxWriter.Write(file, [new XlsxWriter.Sheet(block, sheetName), NotesSheet(notes)]));
            return [path];
        }

        Atomic(path, file =>
        {
            using var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            writer.Write(TableFormats.Csv(block));
        });
        var about = NotesPathFor(path);
        Atomic(about, file =>
        {
            using var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            foreach (var note in notes) writer.WriteLine(note);
        });
        return [path, about];
    }

    /// <summary>Where a CSV report's notes go: <c>report.csv</c> → <c>report.about.txt</c>, beside it.</summary>
    public static string NotesPathFor(string csvPath)
        => Path.ChangeExtension(csvPath, null) + ".about.txt";

    /// <summary>Write through a temp file in the same directory and an atomic move, like every write here.</summary>
    private static void Atomic(string path, Action<Stream> write)
    {
        var temp = path + ".tmp";
        try
        {
            using (var file = File.Create(temp)) write(file);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort (§5.2) */ }
        }
    }

    /// <summary>The notes as their own one-column sheet, in order.</summary>
    private static XlsxWriter.Sheet NotesSheet(IReadOnlyList<string> notes)
    {
        var columns = new[] { new ColumnDescriptor("about", "text", typeof(string)) };
        var rows = notes.Select(n => new object?[] { n }).ToList();
        return new XlsxWriter.Sheet(new TableBlock(columns, rows), "About");
    }

    /// <summary>A default file name for a whole run's workbook: the tab it came from, else "run".</summary>
    public static string SuggestedRunName(string? tabName, DateTime now)
    {
        var stem = string.IsNullOrWhiteSpace(tabName) ? "run" : tabName;
        return $"{Slug(stem)}-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.xlsx";
    }

    /// <summary>A file-name-safe stem: path separators and the platform's invalid characters become '-',
    /// runs collapse, and the result is capped so a long tab title can't produce an unopenable name.</summary>
    public static string Slug(string text)
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
