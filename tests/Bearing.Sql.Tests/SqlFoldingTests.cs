using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Deriving one collapsible region per multi-line statement — the basis for folding a query to its
/// header line. Regions begin at the end of the first line so that line (often a comment) stays visible.
/// </summary>
public class SqlFoldingTests
{
    [Fact]
    public void Single_line_statements_produce_no_regions()
    {
        Assert.Empty(SqlFolding.ComputeFoldRegions("select 1; select 2;"));
    }

    [Fact]
    public void Multi_line_statement_folds_from_end_of_first_line_to_last_char()
    {
        const string sql = "select a,\n       b\nfrom t;";
        var regions = SqlFolding.ComputeFoldRegions(sql);

        var r = Assert.Single(regions);
        Assert.Equal(sql.IndexOf('\n'), r.Start);        // end of "select a,"
        Assert.Equal(sql.Length, r.End);                  // just past the ';'
    }

    [Fact]
    public void First_line_stays_visible_when_folded()
    {
        const string sql = "-- monthly report\nselect *\nfrom sales;";
        var r = Assert.Single(SqlFolding.ComputeFoldRegions(sql));

        // Everything up to the region start is the visible header — the leading comment line.
        Assert.Equal("-- monthly report", sql[..r.Start]);
    }

    [Fact]
    public void One_region_per_multi_line_statement()
    {
        const string sql = "select 1\nfrom a;\n\nselect 2\nfrom b;";
        Assert.Equal(2, SqlFolding.ComputeFoldRegions(sql).Count);
    }

    [Fact]
    public void Mixed_single_and_multi_line_only_folds_the_multi_line_one()
    {
        const string sql = "select 1;\n\nselect 2\nfrom b;";
        var r = Assert.Single(SqlFolding.ComputeFoldRegions(sql));
        Assert.Contains("select 2", sql[..r.Start]);
    }

    [Fact]
    public void Empty_buffer_produces_no_regions()
    {
        Assert.Empty(SqlFolding.ComputeFoldRegions(""));
    }

    // ---- line endings -----------------------------------------------------------------------------

    /// <summary>
    /// A region starts at the end of the first line's <b>text</b>, never one character further along.
    /// <para>
    /// Under LF those are the same offset, which is why every fixture in this file passed while a CRLF
    /// buffer showed an empty gutter. A document line ends at the CR, so an offset at the LF is one past the
    /// line — and the fold margin, which matches a folding to the line its start falls in, then builds no
    /// marker. The section still existed and the fold commands still worked; there was simply nothing to
    /// click, and nothing anywhere reported a problem.
    /// </para>
    /// </summary>
    [Fact]
    public void A_region_starts_at_the_end_of_the_first_lines_text_under_either_line_ending()
    {
        var lf = "select a,\n       b\nfrom t;";
        var crlf = "select a,\r\n       b\r\nfrom t;";

        var lfRegion = Assert.Single(SqlFolding.ComputeFoldRegions(lf));
        var crlfRegion = Assert.Single(SqlFolding.ComputeFoldRegions(crlf));

        // Index 9 is the comma ending "select a," in both, so both regions begin at the same place in the
        // text — the difference in delimiter width must not move it.
        Assert.Equal(9, lfRegion.Start);
        Assert.Equal(9, crlfRegion.Start);
    }

    [Fact]
    public void Every_statement_of_a_crlf_buffer_still_folds()
    {
        // The reported shape: several multi-line statements, CRLF throughout, and no fold buttons at all.
        var sql = "select a,\r\n       b\r\nfrom t;\r\n\r\nselect c\r\nfrom u;\r\n\r\nupdate v\r\n   set d = 1;";

        var regions = SqlFolding.ComputeFoldRegions(sql);

        Assert.Equal(3, regions.Count);
        foreach (var r in regions)
        {
            // It may point AT the delimiter — that is what a line's EndOffset is — but never past it. One
            // character further is inside the CRLF pair, which is the offset that produced no marker.
            Assert.True(r.Start > 0 && sql[r.Start - 1] is not ('\r' or '\n'),
                $"region start {r.Start} is not immediately after the first line's text");
            Assert.True(sql[r.Start] is '\r' or '\n',
                $"region start {r.Start} sits inside the line rather than at its end");
        }
    }

    [Fact]
    public void A_crlf_statement_whose_first_line_is_empty_folds_nothing()
    {
        // Backing up over the CR must not walk the start onto or behind the statement's own start.
        Assert.Empty(SqlFolding.ComputeFoldRegions("\r\nselect 1;"));
    }
}
