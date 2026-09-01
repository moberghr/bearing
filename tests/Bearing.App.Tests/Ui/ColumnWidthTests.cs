using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Bearing.App.Results;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// A column that opens wide enough to render the value it was sized for (#73). Reported as
/// <c>110122</c> showing as <c>1101…</c>: the arithmetic reserved the header's 1px column divider but not
/// the cell's, and multiplied a character count by an average advance, so a column landed with zero slack
/// and ellipsized itself.
/// <para>
/// Asserted on the realized cell's own text layout rather than on the arithmetic, because the arithmetic
/// agreeing with itself is exactly how this shipped: <see cref="ColumnWidths"/> was unit-tested throughout.
/// <c>HasCollapsed</c> is the renderer's own answer to "did I have to trim this".
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class ColumnWidthTests
{
    private readonly UiTestSession _ui;

    public ColumnWidthTests(UiTestSession ui) => _ui = ui;

    /// <summary>The exact report: a six-digit id under a two-character header, with the PK badge.</summary>
    [Fact]
    public Task A_six_digit_id_under_a_short_header_is_not_ellipsized() => _ui.Run(() =>
        AssertRenders("id", "int4", typeof(int), primaryKey: true, 110122));

    /// <summary>Same column without the badge, so a regression can be attributed to the value side rather
    /// than to the header's badge arithmetic.</summary>
    [Fact]
    public Task A_six_digit_value_with_no_badge_is_not_ellipsized() => _ui.Run(() =>
        AssertRenders("n", "int4", typeof(int), primaryKey: false, 110122));

    /// <summary>Digit widths differ per face; 8 is typically the widest. Several lengths, so the guard is not
    /// one lucky string.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(88)]
    [InlineData(888)]
    [InlineData(8888)]
    [InlineData(88888)]
    [InlineData(888888)]
    [InlineData(8888888)]
    public Task Numeric_values_of_any_length_are_not_ellipsized(int value) => _ui.Run(() =>
        AssertRenders("id", "int4", typeof(int), primaryKey: true, value));

    /// <summary>Text, too — the same arithmetic sizes every non-FK, non-bool column.</summary>
    [Theory]
    [InlineData("W")]
    [InlineData("WWWWWW")]
    [InlineData("mixed Case 12")]
    public Task Text_values_are_not_ellipsized(string value) => _ui.Run(() =>
        AssertRenders("name", "text", typeof(string), primaryKey: false, value));

    /// <summary>A multiline value whose first line is short: the column sizes to that first line, but the
    /// cell still grows the inspect affordance, so the column has to have reserved room for it. Without that
    /// reserve the glyph and its margin left the value about five pixels — the same clipping class as the
    /// reported case, on the path that produced it (found in review of the fix).
    /// <para>
    /// Asserted as room-for-the-first-line rather than with <c>HasCollapsed</c>: a TextBlock holding
    /// multiline text reports its line collapsed no matter how wide the column is, because the lines below
    /// are hidden either way.
    /// </para></summary>
    [Fact]
    public Task A_multiline_value_with_a_short_first_line_still_shows_that_line() => _ui.Run(() =>
        AssertHasRoomFor("abc", "payload", "abc" + NewLine + new string('x', 300)));

    /// <summary>Same reserve, reached the other way: a single-line value past the inline threshold, which
    /// also grows the glyph. Past the width cap it is allowed to trim, so what is asserted is that the value
    /// still gets the column rather than a sliver beside the glyph.</summary>
    [Fact]
    public Task A_long_single_line_value_reserves_its_inspect_glyph() => _ui.Run(() =>
    {
        var rs = ResultsHarness.SingleColumn("payload", "text", typeof(string), false, new string('x', 80));
        var (window, view) = ResultsHarness.Show(rs);

        var cell = ResultsHarness.RequireCell(view, rs.Rows[0], 0);
        var text = ResultsHarness.CellText(cell);
        Assert.True(text.Bounds.Width > cell.Bounds.Width / 2,
            $"the value got {text.Bounds.Width:0.##}px of a {cell.Bounds.Width:0.##}px cell — "
            + "the inspect glyph is eating the column");
        window.Close();
    });

    /// <summary>The sizes text is measured at have to be the sizes it is drawn at, or #73 comes straight
    /// back. Headers and cells are deliberately different numbers — the Fluent theme gives them 12 and 15 —
    /// and the review of this fix caught headers being unified onto the cell size, which would have grown
    /// every header row and widened every column with a real name.</summary>
    [Fact]
    public Task Headers_and_cells_render_at_the_sizes_they_are_measured_at() => _ui.Run(() =>
    {
        var rs = ResultsHarness.SingleColumn("release_year", "int4", typeof(int), false, 1998);
        var (window, view) = ResultsHarness.Show(rs);

        var cellText = ResultsHarness.CellText(ResultsHarness.RequireCell(view, rs.Rows[0], 0));
        Assert.Equal(ResultGridChrome.CellFontSize, cellText.FontSize);

        var header = view.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .SelectMany(h => h.GetVisualDescendants().OfType<TextBlock>())
            .First(t => t.Text == "release_year");
        Assert.Equal(ResultGridChrome.HeaderFontSize, header.FontSize);
        window.Close();
    });

    /// <summary>
    /// What the theme would give if we did not pin, measured on a grid the app's chrome has <b>not</b>
    /// touched. Cells come out at 15 and headers at 12 — which is the whole reason both are pinned: the grid
    /// was rendering cells a size larger than the 13 this file has asked for since #30, while measuring every
    /// column for the smaller one (#73).
    /// <para>
    /// Asserting the styled cell against the constant instead would be true of any constant. This pins the
    /// number we are diverging <i>from</i>, so a theme change is a visible failure rather than a silent
    /// resize.
    /// </para>
    /// </summary>
    [Fact]
    public Task The_theme_would_size_cells_and_headers_differently() => _ui.Run(() =>
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            ItemsSource = new[] { new object?[] { 1 } },
        };
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "id",
            CellTemplate = new FuncDataTemplate<object?[]>((_, _) => new TextBlock { Text = "1" }),
        });
        var window = new Window { Width = 400, Height = 200, Content = grid };
        window.Show();
        window.UpdateLayout();

        var cell = grid.GetVisualDescendants().OfType<DataGridCell>().First();
        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .First(h => h.Content as string == "id");

        // The theme's own numbers, stated literally: 15 for a cell, 12 for a header.
        Assert.Equal(15d, cell.FontSize);
        Assert.Equal(12d, header.FontSize);
        // …and the cell size is the one we deliberately override; the header size we adopt.
        Assert.NotEqual(ResultGridChrome.CellFontSize, cell.FontSize);
        Assert.Equal(ResultGridChrome.HeaderFontSize, header.FontSize);
        window.Close();
    });

    /// <summary>A long column name is not trimmed either. The header side had the same class of bug as the
    /// values: measured at Normal weight while drawn SemiBold, with only a couple of pixels of slack — a
    /// narrower rerun of #73, and nothing asserted it (found in review).</summary>
    [Theory]
    [InlineData("id")]
    [InlineData("release_year")]
    [InlineData("original_language_id")]
    public Task A_column_name_is_not_trimmed_by_its_own_header(string name) => _ui.Run(() =>
    {
        var rs = ResultsHarness.SingleColumn(name, "int4", typeof(int), primaryKey: false, 1);
        var (window, view) = ResultsHarness.Show(rs);

        var header = view.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .SelectMany(h => h.GetVisualDescendants().OfType<TextBlock>())
            .First(t => t.Text == name);
        Assert.False(header.TextLayout.TextLines.Any(l => l.HasCollapsed),
            $"the header '{name}' was trimmed to fit its own column: it needs "
            + $"{header.DesiredSize.Width:0.##}px and was arranged {header.Bounds.Width:0.##}px");
        window.Close();
    });

    /// <summary>The row-number gutter is pinned at 46px and inherits the grid-level font, so that font is not
    /// free to grow: five digits have to fit. Caught in review — bumping the shared constant to the cell size
    /// would have clipped row numbers past 9,999.</summary>
    [Fact]
    public Task Five_digit_row_numbers_fit_the_gutter() => _ui.Run(() =>
        Assert.True(
            ResultGridChrome.MeasureText("99999", ResultGridChrome.FontSize) < ResultGridChrome.GutterWidth,
            $"five digits measure {ResultGridChrome.MeasureText("99999", ResultGridChrome.FontSize)}px "
            + $"in a {ResultGridChrome.GutterWidth}px gutter"));

    /// <summary>The cell arranges its text with room for <paramref name="visible"/> at the grid's own font.</summary>
    private static void AssertHasRoomFor(string visible, string column, object? value)
    {
        var rs = ResultsHarness.SingleColumn(column, "text", typeof(string), false, value);
        var (window, view) = ResultsHarness.Show(rs);

        var text = ResultsHarness.CellText(ResultsHarness.RequireCell(view, rs.Rows[0], 0));
        var needed = ResultGridChrome.MeasureText(visible, ResultGridChrome.CellFontSize);
        Assert.True(text.Bounds.Width >= needed,
            $"'{visible}' needs {needed:0.##}px and the cell arranged {text.Bounds.Width:0.##}px");
        window.Close();
    }

    private static readonly string NewLine = ((char)10).ToString();

    /// <summary>A value past the width cap is *meant* to ellipsize — the column stops at
    /// <see cref="ColumnWidths.Max"/> so it can't push its neighbours off screen. Without this the fix could
    /// be "make every column enormous" and the suite would not notice.</summary>
    [Fact]
    public Task A_value_past_the_cap_still_ellipsizes() => _ui.Run(() =>
    {
        var rs = ResultsHarness.SingleColumn("note", "text", typeof(string), false, new string('W', 400));
        var (window, view) = ResultsHarness.Show(rs);

        Assert.True(Collapsed(view, rs), "a 400-character value should trim, not widen the column past the cap");
        window.Close();
    });

    private static void AssertRenders(string name, string dataType, Type clrType, bool primaryKey, object? value)
    {
        var rs = ResultsHarness.SingleColumn(name, dataType, clrType, primaryKey, value);
        var (window, view) = ResultsHarness.Show(rs);

        var text = ResultsHarness.CellText(ResultsHarness.RequireCell(view, rs.Rows[0], 0));
        Assert.False(Collapsed(view, rs),
            $"'{text.Text}' was trimmed to fit its own column: text needs "
            + $"{text.DesiredSize.Width:0.##}px, the cell arranged {text.Bounds.Width:0.##}px");
        window.Close();
    }

    /// <summary>Whether the renderer had to collapse the cell's line — its own answer, not a width
    /// comparison we might get wrong the same way the sizing did.</summary>
    private static bool Collapsed(Control view, Bearing.App.ViewModels.ResultSetViewModel rs)
    {
        var text = ResultsHarness.CellText(ResultsHarness.RequireCell(view, rs.Rows[0], 0));
        return text.TextLayout.TextLines.Any(line => line.HasCollapsed);
    }
}
