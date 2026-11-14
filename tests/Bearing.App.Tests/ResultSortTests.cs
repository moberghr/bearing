using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Client-side sort of a result's loaded rows (#106): the value order, the recoverable query order, paging
/// under a sort, and the pending edits that must travel with their rows. Pure — no grid.
/// </summary>
public class ResultSortTests
{
    private static readonly EditTarget Target = new("public", "t",
    [
        new EditableColumn(0, "id", IsPrimaryKey: true),
        new EditableColumn(1, "name", IsPrimaryKey: false),
        new EditableColumn(2, "qty", IsPrimaryKey: false),
    ]);

    /// <summary>(id int, name text, qty int), editable, in the order given — the query's order.</summary>
    private static ResultSetViewModel Result(bool hasMore = false, params object?[][] rows)
    {
        var columns = new[]
        {
            new ColumnDescriptor("id", "int4", typeof(int), 1, 1),
            new ColumnDescriptor("name", "text", typeof(string), 1, 2),
            new ColumnDescriptor("qty", "int4", typeof(int), 1, 3),
        };
        var result = new QueryResult(columns, rows, rows.Length, TimeSpan.Zero, null, null, hasMore);
        var rs = new ResultSetViewModel(result, "select * from t", pageable: true) { EditTarget = Target };
        rs.CaptureOriginals();
        return rs;
    }

    private static ResultSetViewModel Sample() => Result(false,
        [1, "pear", 30],
        [2, "Apple", null],
        [3, "fig", 10],
        [4, "apple", 20]);

    private static int[] Ids(ResultSetViewModel rs) => rs.Rows.Select(r => (int)r[0]!).ToArray();

    [Fact]
    public void Ascending_orders_numbers_by_value_with_nulls_last()
    {
        var rs = Sample();
        rs.SortBy(2, SortDirection.Ascending);
        Assert.Equal([3, 4, 1, 2], Ids(rs));
    }

    /// <summary>Descending is the reverse of ascending, so NULLs come first — what Postgres does for
    /// <c>ORDER BY qty DESC</c>.</summary>
    [Fact]
    public void Descending_puts_nulls_first()
    {
        var rs = Sample();
        rs.SortBy(2, SortDirection.Descending);
        Assert.Equal([2, 1, 4, 3], Ids(rs));
    }

    /// <summary>Text ignores case — "apple" sits with "Apple", not after "pear" — and only values that
    /// differ by case alone fall back to an ordinal tiebreak, so the order is the same on every machine.</summary>
    [Fact]
    public void Text_is_case_insensitive()
    {
        var rs = Sample();
        rs.SortBy(1, SortDirection.Ascending);
        Assert.Equal([2, 4, 3, 1], Ids(rs));
    }

    /// <summary>The query's order is often meaningful — an ORDER BY the user wrote — so clearing restores
    /// exactly it, not some re-derivation of it.</summary>
    [Fact]
    public void Clearing_restores_the_query_order()
    {
        var rs = Sample();
        rs.SortBy(2, SortDirection.Descending);
        rs.SortBy(2, SortDirection.None);

        Assert.Equal([1, 2, 3, 4], Ids(rs));
        Assert.False(rs.Sort.IsSorted);
        Assert.Equal(SortDirection.None, rs.Sort.DirectionOf(2));
    }

    [Fact]
    public void One_click_cycles_ascending_descending_then_back()
    {
        Assert.Equal(SortDirection.Ascending, ResultSort.Next(SortDirection.None));
        Assert.Equal(SortDirection.Descending, ResultSort.Next(SortDirection.Ascending));
        Assert.Equal(SortDirection.None, ResultSort.Next(SortDirection.Descending));
    }

    /// <summary>Sorting another column moves the sort; there is one per result.</summary>
    [Fact]
    public void Sorting_a_second_column_replaces_the_first()
    {
        var rs = Sample();
        rs.SortBy(2, SortDirection.Ascending);
        rs.SortBy(0, SortDirection.Descending);

        Assert.Equal(0, rs.Sort.Column);
        Assert.Equal(SortDirection.None, rs.Sort.DirectionOf(2));
        Assert.Equal([4, 3, 2, 1], Ids(rs));
    }

    /// <summary>The issue's own requirement: the edit model is keyed by row, so a sort must not lose or
    /// misattribute a pending edit — the edited row moves, its edit goes with it, and the save still writes it.</summary>
    [Fact]
    public void Pending_edits_and_deletes_travel_with_their_rows()
    {
        var rs = Sample();
        var pear = rs.Rows[0];
        var fig = rs.Rows[2];
        rs.SetCell(pear, 1, "plum");
        rs.ToggleDelete(fig);

        rs.SortBy(0, SortDirection.Descending);

        Assert.True(rs.IsRowEdited(pear));
        Assert.Equal("plum", pear[1]);
        Assert.Equal(1, rs.OriginalOf(pear)![0]);
        Assert.True(rs.IsRowDeleted(fig));
        Assert.Equal(2, rs.PendingCount);

        // And reverting after a sort rolls the right row back.
        rs.RevertPending();
        Assert.Equal("pear", pear[1]);
    }

    /// <summary>One reset, not a move per row — the grid re-reads once.</summary>
    [Fact]
    public void A_sort_raises_one_reset()
    {
        var rs = Sample();
        var changes = new List<NotifyCollectionChangedAction>();
        rs.Rows.CollectionChanged += (_, e) => changes.Add(e.Action);

        rs.SortBy(2, SortDirection.Ascending);

        Assert.Equal([NotifyCollectionChangedAction.Reset], changes);
    }

    /// <summary>A page fetched while sorted lands where the sort puts it, not under a header that claims the
    /// grid is ordered.</summary>
    [Fact]
    public void A_page_loaded_under_a_sort_is_placed_in_order()
    {
        var rs = Result(true, [1, "a", 30], [2, "b", 10]);
        rs.SortBy(2, SortDirection.Ascending);

        rs.AppendPage([[3, "c", 20], [4, "d", 5], [5, "e", 40]], hasMore: false);

        Assert.Equal([4, 2, 3, 1, 5], Ids(rs));
        // Still recoverable: the page arrived after the first rows, and clearing says so.
        rs.SortBy(2, SortDirection.None);
        Assert.Equal([1, 2, 3, 4, 5], Ids(rs));
    }

    /// <summary>The same placement through the merge path a large fetch-all takes, so the two can't disagree.</summary>
    [Fact]
    public void A_large_page_is_merged_into_the_same_order()
    {
        var first = Enumerable.Range(0, 2_000).Select(i => new object?[] { i, $"n{i}", (i * 7919) % 2_000 }).ToArray();
        var rs = Result(true, first);
        rs.SortBy(2, SortDirection.Ascending);

        var page = Enumerable.Range(2_000, 2_000).Select(i => new object?[] { i, $"n{i}", (i * 7919) % 2_000 }).ToArray();
        Assert.True((long)page.Length * rs.Rows.Count > ResultSort.InsertBudget);
        rs.AppendPage(page, hasMore: false);

        var expected = first.Concat(page)
            .OrderBy(r => (int)r[2]!).ThenBy(r => (int)r[0]!)
            .Select(r => (int)r[0]!).ToArray();
        Assert.Equal(expected, Ids(rs));
    }

    /// <summary>A new row appears at the bottom, where the user is looking for it, even under a sort.</summary>
    [Fact]
    public void A_new_row_is_added_at_the_bottom_while_sorted()
    {
        var rs = Sample();
        rs.SortBy(0, SortDirection.Descending);

        var added = rs.AddRow();

        Assert.Same(added, rs.Rows[^1]);
    }

    /// <summary>A saved row's array is swapped for the committed one; the new array keeps the old one's place
    /// in the query's order, so clearing the sort still puts it back where the query had it.</summary>
    [Fact]
    public void A_replaced_row_keeps_its_place_in_the_query_order()
    {
        var rs = Sample();
        var fig = rs.Rows[2];
        rs.SortBy(1, SortDirection.Descending);

        rs.ReplaceRow(fig, [3, "fig", 11]);
        rs.SortBy(1, SortDirection.None);

        Assert.Equal([1, 2, 3, 4], Ids(rs));
    }

    [Fact]
    public void Values_compare_across_numeric_types_and_never_throw_on_a_mix()
    {
        Assert.True(ResultSort.CompareValues(2, 10L) < 0);
        Assert.True(ResultSort.CompareValues(2.5m, 2) > 0);
        Assert.True(ResultSort.CompareValues(1.5d, 2m) < 0);
        Assert.True(ResultSort.CompareValues(new DateTime(2024, 1, 1), new DateTime(2023, 1, 1)) > 0);
        Assert.True(ResultSort.CompareValues(false, true) < 0);
        Assert.True(ResultSort.CompareValues(new byte[] { 1, 2 }, new byte[] { 1, 3 }) < 0);
        Assert.Equal(0, ResultSort.CompareValues(null, DBNull.Value));
        // An edited numeric cell holds the user's raw text; comparing it to a number still answers.
        _ = ResultSort.CompareValues("42", 7);
    }

    /// <summary>On a part-fetched result the status says the sort covers the loaded rows only; on a complete
    /// one, and once the sort is cleared, there is nothing to warn about.</summary>
    [Fact]
    public void Only_a_partly_fetched_sort_is_reported_as_partial()
    {
        var partial = Result(true, [1, "a", 30], [2, "b", 10]);
        partial.SortBy(1, SortDirection.Descending);
        Assert.Equal(
            "Sorted by “name” descending over the loaded rows only (2 rows) — ⤓ all fetches the rest.",
            ResultSort.PartialNotice(partial));

        partial.SortBy(1, SortDirection.None);
        Assert.Null(ResultSort.PartialNotice(partial));

        var complete = Sample();
        complete.SortBy(1, SortDirection.Ascending);
        Assert.Null(ResultSort.PartialNotice(complete));
    }
}
