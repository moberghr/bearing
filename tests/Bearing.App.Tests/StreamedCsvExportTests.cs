using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Results;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The streamed CSV export (<see cref="ResultExport.WriteCsvStreamAsync"/>) — the path a `bearing query
/// --out report.csv` takes, where the rows go to the file a batch at a time and the whole result is never
/// in memory.
/// <para>
/// The load-bearing assertion is the first one: the streamed file is the <b>same bytes</b> as the
/// materialising path's. Two renderings of one format is how a quoting rule, a line ending or a BOM ends up
/// differing between "exported from the app" and "exported from the command", so the test is equality
/// against the existing writer rather than a hand-written expectation of what CSV looks like.
/// </para>
/// </summary>
public class StreamedCsvExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bearing-stream-csv", Guid.NewGuid().ToString("N"));

    public StreamedCsvExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly ColumnDescriptor[] Columns =
    [
        new("id", "int4", typeof(int)),
        new("name", "text", typeof(string)),
        new("note", "text", typeof(string)),
    ];

    /// <summary>Every shape the CSV writer has a rule for, so an equality assertion is worth something:
    /// a NULL beside an empty string, a comma, a quote, an embedded newline, leading space, non-ASCII.</summary>
    private static readonly object?[][] Rows =
    [
        [1, "widget", "plain"],
        [2, null, ""],
        [3, "a,b", "he said \"hi\""],
        [4, "two\r\nlines", "  padded  "],
        [5, "naïve", "ü€"],
    ];

    /// <summary>The rows as a provider hands them over: batched, every batch naming its columns.</summary>
    private static async IAsyncEnumerable<RowBatch> Stream(
        IReadOnlyList<object?[]> rows, int size, bool truncated = false,
        Exception? failAfter = null, int failAt = int.MaxValue,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sent = 0;
        foreach (var chunk in rows.Chunk(size))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            if (failAfter is not null && sent >= failAt) throw failAfter;
            sent += chunk.Length;
            yield return new RowBatch(Columns, chunk, Truncated: false);
        }

        if (failAfter is not null && sent >= failAt) throw failAfter;
        await Task.Yield();
        yield return new RowBatch(Columns, [], truncated);
    }

    [Fact]
    public async Task A_streamed_csv_is_byte_for_byte_what_the_batch_writer_produces()
    {
        var batched = Path.Combine(_dir, "batched.csv");
        var streamed = Path.Combine(_dir, "streamed.csv");

        ResultExport.Write(batched, new TableBlock(Columns, Rows), ExportFormat.Csv, "Result 1");
        var written = await ResultExport.WriteCsvStreamAsync(streamed, Stream(Rows, size: 2));

        Assert.Equal(File.ReadAllBytes(batched), File.ReadAllBytes(streamed));
        Assert.Equal(new ResultExport.StreamedCsv(Rows.Length, Columns.Length, false), written);
    }

    /// <summary>
    /// Where the batches fall must not be visible in the file — a boundary landing between two rows is the
    /// only way a streamed write could differ from a whole one, and a 1-row batch is that case at every row.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(1000)]
    public async Task The_batch_size_does_not_reach_the_file(int size)
    {
        var expected = Path.Combine(_dir, $"expected-{size}.csv");
        var actual = Path.Combine(_dir, $"actual-{size}.csv");

        ResultExport.Write(expected, new TableBlock(Columns, Rows), ExportFormat.Csv, "Result 1");
        await ResultExport.WriteCsvStreamAsync(actual, Stream(Rows, size));

        Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(actual));
    }

    /// <summary>
    /// A result with no rows still has a shape, and a CSV with no header line is one `read_csv` cannot parse
    /// — it would take the first data row as the header, or raise on an empty file. This is why every batch
    /// carries its columns and why a row-returning stream always yields at least one of them.
    /// </summary>
    [Fact]
    public async Task An_empty_result_still_gets_its_header()
    {
        var path = Path.Combine(_dir, "empty.csv");

        var written = await ResultExport.WriteCsvStreamAsync(path, Stream([], size: 100));

        Assert.Equal("id,name,note\r\n", File.ReadAllText(path));
        Assert.Equal(0, written.Rows);
        Assert.Equal(3, written.Columns);
    }

    [Fact]
    public async Task The_ceiling_that_stopped_the_read_is_reported()
    {
        var path = Path.Combine(_dir, "capped.csv");

        var written = await ResultExport.WriteCsvStreamAsync(path, Stream(Rows, size: 2, truncated: true));

        Assert.True(written.Truncated);
        Assert.Equal(Rows.Length, written.Rows);
    }

    /// <summary>
    /// The reason this path writes through a temp file. A streamed read reports failure by <b>throwing
    /// part-way</b>, with rows already written — so without the move the caller would be left with a
    /// plausible-looking fraction of the answer, on top of a previous good export they chose to overwrite.
    /// </summary>
    [Fact]
    public async Task A_read_that_fails_part_way_leaves_the_previous_file_alone()
    {
        var path = Path.Combine(_dir, "report.csv");
        File.WriteAllText(path, "the previous export\r\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => ResultExport.WriteCsvStreamAsync(
            path, Stream(Rows, size: 2, failAfter: new InvalidOperationException("connection lost"), failAt: 2)));

        Assert.Equal("the previous export\r\n", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));    // and no half-file beside it either
    }

    /// <summary>
    /// A statement with no result shape yields no batches at all (a DDL, an UPDATE). That is different from
    /// a result with no rows, and the difference has to survive as far as the caller — which reports it as a
    /// refusal. Nothing may be written for it: replacing a good file with a BOM would be the export failing
    /// destructively.
    /// </summary>
    [Fact]
    public async Task A_statement_with_no_shape_writes_nothing()
    {
        var path = Path.Combine(_dir, "nothing.csv");
        File.WriteAllText(path, "still here\r\n");

        var written = await ResultExport.WriteCsvStreamAsync(path, Empty());

        Assert.Equal(0, written.Columns);
        Assert.Equal("still here\r\n", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        static async IAsyncEnumerable<RowBatch> Empty()
        {
            await Task.Yield();
            yield break;
        }
    }

    /// <summary>
    /// A path that cannot be created is the caller's mistake about the <b>file</b>, and it is told apart
    /// from a failure of the read by where it was thrown rather than by guessing at an exception type. It
    /// matters because the CLI attributes the two differently: reporting a bad directory as "the query
    /// could not be run" also writes a failed-execution row to the audit log for a statement that ran
    /// perfectly well (§1.11d).
    /// </summary>
    [Fact]
    public async Task A_path_that_cannot_be_created_is_the_files_failure_not_the_reads()
    {
        var path = Path.Combine(_dir, "no-such-directory", "report.csv");

        var ex = await Assert.ThrowsAsync<ResultExport.ExportWriteException>(
            () => ResultExport.WriteCsvStreamAsync(path, Stream(Rows, size: 2)));

        Assert.Equal(path, ex.Path);
        Assert.Contains(path, ex.Message);
    }

    /// <summary>And the other side of that line: a failure while the rows are being read is the read's, and
    /// reaches the caller as whatever the driver threw.</summary>
    [Fact]
    public async Task A_failure_while_reading_is_still_the_reads()
    {
        var path = Path.Combine(_dir, "partial.csv");

        await Assert.ThrowsAsync<InvalidOperationException>(() => ResultExport.WriteCsvStreamAsync(
            path, Stream(Rows, size: 2, failAfter: new InvalidOperationException("connection lost"), failAt: 2)));
    }

    /// <summary>A row shorter than the header is padded rather than ending its line early — a ragged line
    /// is what turns one bad row into a file a reader rejects wholesale.</summary>
    [Fact]
    public async Task A_short_row_is_padded_to_the_header_width()
    {
        var path = Path.Combine(_dir, "ragged.csv");
        object?[][] ragged = [[1, "only two"]];

        await ResultExport.WriteCsvStreamAsync(path, Stream(ragged, size: 10));

        Assert.Equal("id,name,note\r\n1,only two,\r\n", File.ReadAllText(path));
    }
}
