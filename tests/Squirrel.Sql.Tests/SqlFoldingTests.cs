using Squirrel.Sql;
using Xunit;

namespace Squirrel.Sql.Tests;

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
}
