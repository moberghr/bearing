using Bearing.App.Results;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The checkbox column's value handling. Two consumers have to agree on it — the CheckBox the mouse clicks
/// and the Enter/F2 toggle the keyboard now gets (#9) — and a disagreement would show as a cell whose visual
/// and stored value drift apart, which is invisible until the save writes the wrong one.
/// </summary>
public class BoolCellValueTests
{
    [Fact]
    public void A_bool_cell_reads_as_itself()
    {
        Assert.True(BoolCellValue.Read([true], 0));
        Assert.False(BoolCellValue.Read([false], 0));
    }

    [Fact]
    public void Null_missing_and_non_bool_cells_all_read_as_indeterminate()
    {
        Assert.Null(BoolCellValue.Read([null], 0));
        Assert.Null(BoolCellValue.Read([true], 5));   // out of range
        Assert.Null(BoolCellValue.Read(null, 0));
        Assert.Null(BoolCellValue.Read([42], 0));
    }

    [Fact]
    public void A_cell_still_holding_raw_text_reads_as_the_bool_it_will_be_coerced_to()
    {
        // A paste writes the clipboard's text straight into the row (coercion happens at save time), so the
        // checkbox has to make sense of "true" before that.
        Assert.True(BoolCellValue.Read(["true"], 0));
        Assert.False(BoolCellValue.Read(["False"], 0));
        Assert.Null(BoolCellValue.Read(["yes"], 0));  // not parseable → indeterminate, not false
    }

    [Fact]
    public void Toggling_walks_the_same_three_state_ring_as_the_checkbox()
    {
        Assert.True(BoolCellValue.Next(false));
        Assert.Null(BoolCellValue.Next(true));
        Assert.False(BoolCellValue.Next(null));
    }
}
