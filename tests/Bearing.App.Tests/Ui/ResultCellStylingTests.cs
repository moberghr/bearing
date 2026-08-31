using System.Threading.Tasks;
using Avalonia.Media;
using Bearing.App.Controls;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// How a realized results cell styles its value (#61). These assert on live cells rather than on a helper's
/// return value because that is exactly where the bug lived: three cell kinds each built their own TextBlock,
/// and the foreign-key one was the copy that forgot the NULL styling.
/// </summary>
[Collection(UiTestCollection.Name)]
public class ResultCellStylingTests
{
    private readonly UiTestSession _ui;

    public ResultCellStylingTests(UiTestSession ui) => _ui = ui;

    /// <summary>The regression: a NULL in a foreign-key column has to look like a NULL anywhere else.</summary>
    [Fact]
    public Task Null_in_a_foreign_key_column_is_dimmed_and_italic() => _ui.Run(() =>
    {
        object?[] nullFk = [2, null, "no customer"];
        var rs = ResultsHarness.ForeignKeyResult([[1, 42, "has customer"], nullFk]);
        var (window, view) = ResultsHarness.Show(rs);

        var text = ResultsHarness.CellText(ResultsHarness.RequireCell(view, nullFk, 1));

        Assert.Equal("(null)", text.Text);
        Assert.Equal(FontStyle.Italic, text.FontStyle);
        Assert.Equal(ColorOf(Tokens.NullBrush), ColorOf(text.Foreground));
        window.Close();
    });

    /// <summary>The same NULL in a plain column, so the two are measured against one standard rather than
    /// against each other. This half already passed; it is here to catch a shared helper regressing both.</summary>
    [Fact]
    public Task Null_in_a_plain_column_is_dimmed_and_italic() => _ui.Run(() =>
    {
        object?[] nullNote = [1, 42, null];
        var rs = ResultsHarness.ForeignKeyResult([nullNote]);
        var (window, view) = ResultsHarness.Show(rs);

        var text = ResultsHarness.CellText(ResultsHarness.RequireCell(view, nullNote, 2));

        Assert.Equal("(null)", text.Text);
        Assert.Equal(FontStyle.Italic, text.FontStyle);
        Assert.Equal(ColorOf(Tokens.NullBrush), ColorOf(text.Foreground));
        window.Close();
    });

    /// <summary>A foreign key that has a value keeps its ordinary upright primary text: the fix must not dim
    /// live values, nor restyle them as numeric code text.</summary>
    [Fact]
    public Task Foreign_key_with_a_value_stays_upright_primary_text() => _ui.Run(() =>
    {
        object?[] row = [1, 42, "has customer"];
        var rs = ResultsHarness.ForeignKeyResult([row]);
        var (window, view) = ResultsHarness.Show(rs);

        var text = ResultsHarness.CellText(ResultsHarness.RequireCell(view, row, 1));

        Assert.Equal("42", text.Text);
        Assert.Equal(FontStyle.Normal, text.FontStyle);
        Assert.Equal(ColorOf(Tokens.Res("Text.Primary")), ColorOf(text.Foreground));
        window.Close();
    });

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
}
