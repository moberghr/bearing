using System;
using System.Threading.Tasks;
using Bearing.App.Controls;
using Bearing.App.Formatting;
using Bearing.App.Results;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Digit grouping asserted where it actually lives: on the <c>TextBlock</c> a realized cell draws.
/// <para>
/// A pure test can only say what <see cref="NumberGrouping"/> returns; the claim being made here is that the
/// grid draws the grouped form <b>and</b> that the cell's own text — the one the clipboard, the exports and
/// the in-cell editor read — is still the raw invariant one. Those two facts are only jointly observable on
/// a live cell, which is §4.3's test for whether a UI test earns its place.
/// </para>
/// <para>
/// <see cref="NumberGrouping.Enabled"/> is process-wide, so every test here restores it. Leaving it flipped
/// would decide the outcome of whatever ran next in this collection — the ordering trap §4.5 documents.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class NumberGroupingRenderTests
{
    private readonly UiTestSession _ui;

    public NumberGroupingRenderTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task A_numeric_cell_draws_its_digits_grouped() => WithGrouping(true, () =>
    {
        var rs = ResultsHarness.SingleColumn("amount", "numeric", typeof(decimal), false, 1234567.89m);
        var (window, view) = ResultsHarness.Show(rs);

        var text = ResultsHarness.CellText(ResultsHarness.RequireCell(view, rs.Rows[0], 0));
        Assert.Equal("1,234,567.89", text.Text);
        // The same cell, asked the way every non-visual consumer asks it.
        Assert.Equal("1234567.89", GridSelectionOps.CellText(rs.Rows[0], 0));
        window.Close();
    });

    /// <summary>The copy path, end to end: what a selection of that cell puts on the clipboard carries no
    /// separator, whatever the cell is drawing. This is the assertion that would have caught #26.</summary>
    [Fact]
    public Task What_the_cell_draws_is_not_what_a_copy_carries() => WithGrouping(true, () =>
    {
        var rs = ResultsHarness.SingleColumn("amount", "numeric", typeof(decimal), false, 1234567.89m);
        var (window, view) = ResultsHarness.Show(rs);

        Assert.Equal("1,234,567.89", ResultsHarness.CellText(ResultsHarness.RequireCell(view, rs.Rows[0], 0)).Text);
        Assert.Equal("1234567.89", GridSelectionOps.Tsv(rs, [(rs.Rows[0], 0)]));
        window.Close();
    });

    /// <summary>Setting off, and the grid draws the raw value — the same assertion in reverse, so a broken
    /// wire-up cannot pass both.</summary>
    [Fact]
    public Task With_the_setting_off_the_cell_draws_the_raw_value() => WithGrouping(false, () =>
    {
        var rs = ResultsHarness.SingleColumn("amount", "numeric", typeof(decimal), false, 1234567.89m);
        var (window, view) = ResultsHarness.Show(rs);

        Assert.Equal("1234567.89", ResultsHarness.CellText(ResultsHarness.RequireCell(view, rs.Rows[0], 0)).Text);
        window.Close();
    });

    /// <summary>A text column holding something that looks like a number is not one. Grouping follows the
    /// column's CLR type, not the shape of the string — a product code is not a magnitude.</summary>
    [Fact]
    public Task A_text_column_that_looks_numeric_is_not_grouped() => WithGrouping(true, () =>
    {
        var rs = ResultsHarness.SingleColumn("code", "text", typeof(string), false, "1234567");
        var (window, view) = ResultsHarness.Show(rs);

        Assert.Equal("1234567", ResultsHarness.CellText(ResultsHarness.RequireCell(view, rs.Rows[0], 0)).Text);
        window.Close();
    });

    /// <summary>Foreign keys group too. The cell kind is built by a different method with its own TextBlock,
    /// which is precisely how #61's NULL styling drifted — so it is asserted rather than assumed.</summary>
    [Fact]
    public Task A_foreign_key_cell_groups_its_digits_too() => WithGrouping(true, () =>
    {
        object?[] row = [1, 1234567, "note"];
        var rs = ResultsHarness.ForeignKeyResult([row]);
        var (window, view) = ResultsHarness.Show(rs);

        Assert.Equal("1,234,567", ResultsHarness.CellText(ResultsHarness.RequireCell(view, row, 1)).Text);
        window.Close();
    });

    /// <summary>A NULL still reads as the null token in a numeric column — grouping runs over the same text
    /// and must leave it alone.</summary>
    [Fact]
    public Task A_null_in_a_numeric_column_still_reads_as_the_null_token() => WithGrouping(true, () =>
    {
        // `null` alone would bind as the params array itself, not as one null value in it.
        var rs = ResultsHarness.SingleColumn("amount", "numeric", typeof(decimal), false, new object?[] { null });
        var (window, view) = ResultsHarness.Show(rs);

        Assert.Equal(CellFormat.NullToken, ResultsHarness.CellText(ResultsHarness.RequireCell(view, rs.Rows[0], 0)).Text);
        window.Close();
    });

    /// <summary>The width half (#73, from the other direction): the commas widen the text, so the column has
    /// to have been measured against the grouped form. <c>HasCollapsed</c> is the renderer's own answer.
    /// </summary>
    [Fact]
    public Task A_grouped_value_is_measured_at_the_width_it_draws() => WithGrouping(true, () =>
    {
        var rs = ResultsHarness.SingleColumn("n", "int8", typeof(long), false, 8888888888L);
        var (window, view) = ResultsHarness.Show(rs);

        var text = ResultsHarness.CellText(ResultsHarness.RequireCell(view, rs.Rows[0], 0));
        Assert.Equal("8,888,888,888", text.Text);
        Assert.DoesNotContain(text.TextLayout.TextLines, line => line.HasCollapsed);
        window.Close();
    });

    /// <summary>
    /// The hole grouping opened, closed: a typed-back <c>1,234</c> is refused by the parser (deliberately —
    /// under a comma-decimal locale it means 1.234, so reading it as 1234 would be a thousandfold write), and
    /// without a mark it renders identically to the correctly grouped 1234 in the row beside it.
    /// <para>
    /// The cells are set before the window is shown, so the claim is about what the grid <i>builds</i> rather
    /// than about a restyle pass landing in time.
    /// </para>
    /// </summary>
    [Fact]
    public Task A_refused_numeric_edit_is_drawn_amber_not_as_a_correct_value() => WithGrouping(true, () =>
    {
        object?[] refused = [1, 7, "typed back what the grid drew"];
        object?[] ordinary = [1234, 7, "a value the column can take"];
        var rs = ResultsHarness.ForeignKeyResult([refused, ordinary], editable: true);
        rs.SetCell(refused, 0, "1,234");     // int column; TryParseNumber refuses the separator

        var (window, view) = ResultsHarness.Show(rs);
        var bad = ResultsHarness.CellText(ResultsHarness.RequireCell(view, refused, 0));
        var good = ResultsHarness.CellText(ResultsHarness.RequireCell(view, ordinary, 0));

        // Byte-for-byte the same text — the colour is the only thing telling them apart.
        Assert.Equal("1,234", bad.Text);
        Assert.Equal("1,234", good.Text);
        Assert.Equal(ColorOf(Tokens.Res("Warn.Amber")), ColorOf(bad.Foreground));
        Assert.NotEqual(ColorOf(bad.Foreground), ColorOf(good.Foreground));
        window.Close();
    });

    /// <summary>The other side: a value the column can take is not marked, however it was typed. Without
    /// this the mark could be "amber whenever a cell was touched" and the suite would not notice.</summary>
    [Fact]
    public Task An_acceptable_numeric_edit_is_not_marked() => WithGrouping(true, () =>
    {
        object?[] row = [1, 7, "note"];
        var rs = ResultsHarness.ForeignKeyResult([row], editable: true);
        rs.SetCell(row, 0, "1234");

        var (window, view) = ResultsHarness.Show(rs);
        var text = ResultsHarness.CellText(ResultsHarness.RequireCell(view, row, 0));

        Assert.Equal("1,234", text.Text);    // drawn grouped, like any other numeric value
        Assert.NotEqual(ColorOf(Tokens.Res("Warn.Amber")), ColorOf(text.Foreground));
        window.Close();
    });

    private static Avalonia.Media.Color ColorOf(Avalonia.Media.IBrush? brush)
        => Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(brush).Color;

    /// <summary>Run <paramref name="body"/> with the grouping setting pinned, and always put it back.</summary>
    private Task WithGrouping(bool enabled, Action body) => _ui.Run(() =>
    {
        var original = NumberGrouping.Enabled;
        NumberGrouping.Enabled = enabled;
        try { body(); }
        finally { NumberGrouping.Enabled = original; }
    });
}
