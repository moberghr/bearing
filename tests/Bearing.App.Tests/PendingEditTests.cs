using System;
using System.Linq;
using Bearing.App.Formatting;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Pending inline-edit bookkeeping: what a delete mark does to an edit that was already pending, and which
/// assignments a save actually generates. Pure view-model + <see cref="ResultEditModel"/> — no grid, no DB.
/// </summary>
public class PendingEditTests
{
    private static readonly EditTarget Target = new("public", "t",
    [
        new EditableColumn(0, "id", IsPrimaryKey: true),
        new EditableColumn(1, "name", IsPrimaryKey: false),
        new EditableColumn(2, "qty", IsPrimaryKey: false),
    ]);

    /// <summary>A result set of (id int, name text, qty int) with one row, originals captured.</summary>
    private static ResultSetViewModel OneRow(params object?[] values)
    {
        var columns = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), 1, 1),
            new ColumnDescriptor("name", "text", typeof(string), 1, 2),
            new ColumnDescriptor("qty", "int4", typeof(int), 1, 3),
        };
        var result = new QueryResult(columns, new[] { values }, 1, TimeSpan.Zero, null, null, false);
        var rs = new ResultSetViewModel(result, "select * from t", pageable: true) { EditTarget = Target };
        rs.CaptureOriginals();
        return rs;
    }

    [Fact]
    public void Un_marking_a_delete_restores_the_edit_it_superseded()
    {
        var rs = OneRow(1, "one", 5);
        var row = rs.Rows[0];

        rs.SetCell(row, 1, "edited");
        Assert.True(rs.IsRowEdited(row));

        // Marking for deletion sets the edit aside — a delete supersedes it at save time.
        rs.ToggleDelete(row);
        Assert.True(rs.IsRowDeleted(row));
        Assert.False(rs.IsRowEdited(row));
        Assert.Equal(1, rs.PendingCount);

        // Un-marking must give it back: the grid still shows "edited", so losing the mark meant showing a
        // change that would never be saved.
        rs.ToggleDelete(row);
        Assert.False(rs.IsRowDeleted(row));
        Assert.True(rs.IsRowEdited(row));
        Assert.Equal(1, rs.PendingCount);

        var changes = ResultEditModel.BuildPendingChanges(rs, Target);
        var update = Assert.Single(changes);
        Assert.Equal(ResultEditModel.ChangeKind.Update, update.Kind);
        Assert.Contains("name", update.Command.Sql);
    }

    [Fact]
    public void Reverting_rolls_back_an_edit_that_is_parked_under_a_delete_mark()
    {
        var rs = OneRow(1, "one", 5);
        var row = rs.Rows[0];

        rs.SetCell(row, 1, "edited");
        rs.ToggleDelete(row);          // parks the edit
        rs.RevertPending();

        Assert.Equal("one", row[1]);   // the cell is back to its original value …
        Assert.False(rs.HasPendingChanges);
        Assert.False(rs.IsRowDeleted(row));
        Assert.False(rs.IsRowEdited(row));

        // … and a later delete-and-undelete can't resurrect the discarded edit.
        rs.ToggleDelete(row);
        rs.ToggleDelete(row);
        Assert.False(rs.HasPendingChanges);
    }

    [Fact]
    public void A_cell_typed_back_to_its_original_value_generates_no_assignment()
    {
        var rs = OneRow(1, "one", 5);
        var row = rs.Rows[0];

        // The grid hands back strings: "5" is what typing over an int cell produces. Coerced, it equals the
        // original 5 — so it must not become an UPDATE assignment (it used to, on every touched cell).
        rs.SetCell(row, 2, "5");
        Assert.True(rs.IsRowEdited(row));   // the row is still *marked* — we only skip the no-op SQL

        Assert.Empty(ResultEditModel.BuildPendingChanges(rs, Target));
    }

    [Fact]
    public void The_null_token_saves_as_a_null_assignment_not_as_the_text()
    {
        var rs = OneRow(1, "one", 5);
        var row = rs.Rows[0];

        // What Set NULL (#33) writes, and what typing "(null)" into the cell has always written — one value
        // path, so the token can never end up in the database as the literal text.
        rs.SetCell(row, 1, CellFormat.NullToken);
        var change = Assert.Single(ResultEditModel.BuildPendingChanges(rs, Target));

        Assert.Equal(ResultEditModel.ChangeKind.Update, change.Kind);
        Assert.Contains("name", change.Command.Sql);
        Assert.Contains(change.Command.Parameters, p => p.Value is null);
        Assert.DoesNotContain(change.Command.Parameters, p => Equals(p.Value, CellFormat.NullToken));
    }

    [Fact]
    public void The_null_token_on_a_new_row_inserts_an_explicit_null_where_a_blank_cell_takes_the_default()
    {
        var rs = OneRow(1, "one", 5);

        var blank = rs.AddRow();
        rs.SetCell(blank, 1, "two");           // qty left untouched → not in the INSERT, so the default fills it
        var fromDefault = Assert.Single(ResultEditModel.BuildPendingChanges(rs, Target));
        Assert.Equal(ResultEditModel.ChangeKind.Insert, fromDefault.Kind);
        Assert.DoesNotContain("qty", fromDefault.Command.Sql);

        rs.SetCell(blank, 2, CellFormat.NullToken);   // Set NULL on the same cell → explicitly NULL instead
        var explicitNull = Assert.Single(ResultEditModel.BuildPendingChanges(rs, Target));
        Assert.Contains("qty", explicitNull.Command.Sql);
        Assert.Contains(explicitNull.Command.Parameters, p => p.Value is null);
    }

    [Fact]
    public void A_real_change_still_generates_exactly_the_changed_assignment()
    {
        var rs = OneRow(1, "one", 5);
        var row = rs.Rows[0];

        rs.SetCell(row, 2, "6");      // genuinely different
        var change = Assert.Single(ResultEditModel.BuildPendingChanges(rs, Target));

        Assert.Equal(ResultEditModel.ChangeKind.Update, change.Kind);
        Assert.Contains("qty", change.Command.Sql);
        Assert.DoesNotContain("name", change.Command.Sql);   // untouched column stays out of the SET list
        Assert.Contains(change.Command.Parameters, p => Equals(p.Value, 6));  // coerced to the column type
    }
}
