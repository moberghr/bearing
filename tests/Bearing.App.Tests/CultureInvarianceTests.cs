using System;
using System.Globalization;
using System.Linq;
using Bearing.App.Formatting;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Cell text must not depend on the machine's locale. Every assertion here runs under <c>hr-HR</c>
/// (comma decimal separator) because that is the only way this class of bug is visible — on an en-US or
/// invariant box the buggy and the fixed code produce identical output, which is exactly why it shipped.
/// <para>
/// The stakes are not cosmetic. <c>CellFormat.Display</c> feeds three consumers at once: the grid, every
/// clipboard/export format (<c>TableFormats.Text</c>), and the inline editor — whose text goes back through
/// <c>ResultEditModel.Coerce</c>, which parses invariantly. Display and parse disagreeing on the decimal
/// separator turned 9.5 into 95 on save.
/// </para>
/// </summary>
public class CultureInvarianceTests
{
    /// <summary>Runs the body under a pinned culture — see <see cref="CultureScope"/>, which this shares
    /// with the assertions that pin a locale for the opposite reason.</summary>
    private static void InCulture(string name, Action body) => CultureScope.In(name, body);

    // ---- display ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("hr-HR")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void Numbers_render_invariantly_whatever_the_locale(string culture) => InCulture(culture, () =>
    {
        Assert.Equal("9.5", CellFormat.Display(9.5m));
        Assert.Equal("9.5", CellFormat.Display(9.5d));
        Assert.Equal("9.5", CellFormat.Display(9.5f));
        Assert.Equal("-0.25", CellFormat.Display(-0.25m));
        Assert.Equal("1234567.5", CellFormat.Display(1234567.5m));   // and no group separators
        Assert.Equal("1234567", CellFormat.Display(1234567));
        Assert.Equal("1234567", CellFormat.Display(1234567L));
    });

    [Fact]
    public void Display_and_the_sql_literal_agree_on_every_number() => InCulture("hr-HR", () =>
    {
        // Copy as ▸ CSV and Copy as ▸ SQL render the same cell through different functions. If they can
        // disagree, one of them is wrong and nothing says which.
        foreach (object n in new object[] { 9.5m, -0.25m, 1234567.5d, 0.1f, 42, -7L, (short)3, (byte)200 })
            Assert.Equal(CellFormat.Display(n), SqlValue.Literal(n));
    });

    // ---- export formats --------------------------------------------------------------------------

    private static ResultSetViewModel Sample()
    {
        var columns = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int)),
            new ColumnDescriptor("price", "numeric", typeof(decimal)),
        };
        var result = new QueryResult(columns, new[] { new object?[] { 1, 9.5m } }, 1, TimeSpan.Zero, null, null, false);
        return new ResultSetViewModel(result, "select * from t", pageable: false);
    }

    [Fact]
    public void Csv_carries_a_number_as_a_number_not_a_quoted_string() => InCulture("hr-HR", () =>
    {
        // The original report. Under hr-HR the decimal rendered "9,5", which then tripped CsvField's comma
        // check and left as the *string* "9,5" — so every consumer read the column as text.
        var row = TableFormats.Csv(TableBlock.ForResult(Sample())).Split("\r\n")[1];
        Assert.Equal("1,9.5", row);
        Assert.DoesNotContain("\"", row);
    });

    [Fact]
    public void Markdown_and_html_carry_the_invariant_number_too() => InCulture("hr-HR", () =>
    {
        var block = TableBlock.ForResult(Sample());
        Assert.Contains("| 1 | 9.5 |", TableFormats.Markdown(block));
        Assert.Contains(">9.5<", TableFormats.Html(block));
    });

    [Fact]
    public void Sql_insert_and_in_list_stay_invariant() => InCulture("hr-HR", () =>
    {
        var block = TableBlock.ForResult(Sample());
        Assert.Contains("values (1, 9.5)", TableFormats.SqlInsert(block, "public", "t"));
        Assert.Equal("1, 9.5", TableFormats.InList(block));
    });

    // ---- the edit round-trip ---------------------------------------------------------------------

    private static readonly EditTarget Target = new("public", "t",
    [
        new EditableColumn(0, "id", IsPrimaryKey: true),
        new EditableColumn(1, "price", IsPrimaryKey: false),
    ]);

    private static ResultSetViewModel Editable(params object?[] values)
    {
        var columns = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), 1, 1),
            new ColumnDescriptor("price", "numeric", typeof(decimal), 1, 2),
        };
        var result = new QueryResult(columns, new[] { values }, 1, TimeSpan.Zero, null, null, false);
        var rs = new ResultSetViewModel(result, "select * from t", pageable: true) { EditTarget = Target };
        rs.CaptureOriginals();
        return rs;
    }

    [Fact]
    public void A_displayed_number_typed_straight_back_saves_the_same_value() => InCulture("hr-HR", () =>
    {
        // The corruption path, end to end: what the grid shows is what the editor hands back, and Coerce
        // parses invariantly. Before the fix Display gave "9,5" and this saved 95.
        var rs = Editable(1, 1.5m);
        rs.SetCell(rs.Rows[0], 1, CellFormat.Display(9.5m));

        var change = Assert.Single(ResultEditModel.BuildPendingChanges(rs, Target));
        Assert.Contains(change.Command.Parameters, p => Equals(p.Value, 9.5m));
        Assert.DoesNotContain(change.Command.Parameters, p => Equals(p.Value, 95m));
    });

    [Fact]
    public void A_locale_typed_decimal_is_refused_rather_than_silently_multiplied() => InCulture("hr-HR", () =>
    {
        // A Croatian user typing their own decimal form. Convert.ChangeType would return 95 without error;
        // the raw string goes to the server instead, which rejects it visibly.
        var rs = Editable(1, 1.5m);
        rs.SetCell(rs.Rows[0], 1, "9,5");

        var change = Assert.Single(ResultEditModel.BuildPendingChanges(rs, Target));
        Assert.DoesNotContain(change.Command.Parameters, p => Equals(p.Value, 95m));
        Assert.Contains(change.Command.Parameters, p => Equals(p.Value, "9,5"));
    });

    [Fact]
    public void A_genuine_number_still_parses() => InCulture("hr-HR", () =>
    {
        Assert.True(CellFormat.TryParseNumber("9.5", typeof(decimal), out var m));
        Assert.Equal(9.5m, m);
        Assert.True(CellFormat.TryParseNumber("-42", typeof(int), out var i));
        Assert.Equal(-42, i);
        Assert.True(CellFormat.TryParseNumber("1.5e3", typeof(double), out var d));
        Assert.Equal(1500d, d);

        // Group separators are refused on purpose — that is what made "9,5" read as 95.
        Assert.False(CellFormat.TryParseNumber("9,5", typeof(decimal), out _));
        Assert.False(CellFormat.TryParseNumber("1,234", typeof(int), out _));
    });
}
