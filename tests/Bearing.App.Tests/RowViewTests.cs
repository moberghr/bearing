using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Client-side filter and sort over the loaded rows (#106, design v4): the pure rules in <see cref="RowView"/>
/// and <see cref="ColumnFilter"/>, then how <see cref="ResultFilterViewModel"/> keeps the grid's view in step
/// with the rows underneath it.
/// </summary>
public class RowViewTests
{
    private static readonly Func<object?[], bool> NotNew = _ => false;
    private static readonly Dictionary<int, string> NoFilters = new();

    private static List<object?[]> Film() =>
    [
        [1, "ACADEMY DINOSAUR", 0.99m, null],
        [2, "ACE GOLDFINGER", 4.99m, "{\"genre\":\"Horror\"}"],
        [3, "ADAPTATION HOLES", 2.99m, "{\"genre\":\"Documentary\"}"],
        [4, "AFFAIR PREJUDICE", null, null],
        [5, "AGENT TRUMAN", 2.99m, "{\"genre\":\"Horror\"}"],
    ];

    private static int[] Ids(IEnumerable<object?[]> rows) => rows.Select(r => (int)r[0]!).ToArray();

    // ---- sort ---------------------------------------------------------------------------------------

    [Fact]
    public void Ascending_puts_nulls_last_and_keeps_ties_in_load_order()
    {
        var view = RowView.Apply(Film(), NotNew, null, NoFilters, new RowSort(2, SortDirection.Ascending));
        Assert.Equal([1, 3, 5, 2, 4], Ids(view));   // 3 and 5 tie at 2.99 and keep load order
    }

    /// <summary>Postgres's default: NULLS FIRST under DESC, so the local order is what the server would give.</summary>
    [Fact]
    public void Descending_puts_nulls_first_as_postgres_does()
    {
        var view = RowView.Apply(Film(), NotNew, null, NoFilters, new RowSort(2, SortDirection.Descending));
        Assert.Equal([4, 2, 3, 5, 1], Ids(view));
    }

    [Fact]
    public void Text_sorts_case_insensitive_and_numbers_in_text_sort_as_numbers()
    {
        List<object?[]> rows = [[1, "b"], [2, "A"], [3, "a"], [4, "B"]];
        Assert.Equal([2, 3, 1, 4], Ids(RowView.Apply(rows, NotNew, null, NoFilters, new RowSort(1, SortDirection.Ascending))));

        List<object?[]> numericText = [[1, "10"], [2, "9"], [3, "100"]];
        Assert.Equal([2, 1, 3], Ids(RowView.Apply(numericText, NotNew, null, NoFilters, new RowSort(1, SortDirection.Ascending))));
    }

    /// <summary>Text order is the invariant culture's, so two users sort the same result the same way. Lithuanian
    /// collation puts "y" between "i" and "j"; a current-culture compare sorted "y" first there.</summary>
    [Fact]
    public void Text_order_does_not_depend_on_the_machine_culture()
    {
        List<object?[]> rows = [[1, "z"], [2, "y"], [3, "j"]];
        var asc = new RowSort(1, SortDirection.Ascending);
        CultureScope.In("lt-LT", () => Assert.Equal([3, 2, 1], Ids(RowView.Apply(rows, NotNew, null, NoFilters, asc))));
        CultureScope.In("en-US", () => Assert.Equal([3, 2, 1], Ids(RowView.Apply(rows, NotNew, null, NoFilters, asc))));
    }

    /// <summary>
    /// A column mixing numbers and text (an edited cell, a varchar of codes) has one order whatever the rows'
    /// load order. Compared pair by pair — numbers by value, number against text as text — 9 &lt; 10, "10" &lt; "1a"
    /// and "1a" &lt; "9" made a cycle, and the sort's output depended on where it started. Numbers come first.
    /// </summary>
    [Fact]
    public void Numbers_sort_before_text_and_the_order_is_the_same_from_every_load_order()
    {
        object?[] values = [10, "9", "1a", 2.5m, "abc", "2", "B"];
        var expected = new object?[] { "2", 2.5m, "9", 10, "1a", "abc", "B" };
        var asc = new RowSort(0, SortDirection.Ascending);

        foreach (var order in Permutations(values))
        {
            var rows = order.Select(v => new[] { v }).ToList<object?[]>();
            Assert.Equal(expected, RowView.Apply(rows, NotNew, null, NoFilters, asc).Select(r => r[0]));
        }
    }

    [Fact]
    public void Comparing_is_a_total_order_across_kinds()
    {
        object?[] values = [3, "3.5", "x", true, false, new DateTime(2024, 1, 1), Guid.Empty, double.NaN, new byte[] { 1 }, "X"];
        foreach (var a in values)
        foreach (var b in values)
        {
            Assert.Equal(Math.Sign(RowView.CompareValues(a, b)), -Math.Sign(RowView.CompareValues(b, a)));
            foreach (var c in values)
                if (RowView.CompareValues(a, b) <= 0 && RowView.CompareValues(b, c) <= 0)
                    Assert.True(RowView.CompareValues(a, c) <= 0, $"{a} <= {b} <= {c} but not {a} <= {c}");
        }
    }

    private static IEnumerable<object?[]> Permutations(object?[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var tail in Permutations(rest)) yield return [items[i], .. tail];
        }
    }

    [Fact]
    public void The_cycle_is_ascending_descending_off_and_another_column_starts_ascending()
    {
        var asc = RowView.Next(null, 2);
        Assert.Equal(new RowSort(2, SortDirection.Ascending), asc);
        var desc = RowView.Next(asc, 2);
        Assert.Equal(new RowSort(2, SortDirection.Descending), desc);
        Assert.Null(RowView.Next(desc, 2));
        Assert.Equal(new RowSort(1, SortDirection.Ascending), RowView.Next(desc, 1));
    }

    [Theory]
    [InlineData("jsonb", false)]
    [InlineData("json", false)]
    [InlineData("xml", false)]
    [InlineData("text", true)]
    [InlineData("numeric", true)]
    public void Documents_are_not_sortable(string type, bool sortable)
        => Assert.Equal(sortable, RowView.IsSortable(new ColumnDescriptor("c", type, typeof(string))));

    // ---- filters ------------------------------------------------------------------------------------

    [Fact]
    public void The_quick_filter_matches_any_column_case_insensitively_json_as_its_text()
    {
        Assert.Equal([2, 5], Ids(RowView.Apply(Film(), NotNew, "horror", NoFilters, null)));
        Assert.Equal([2], Ids(RowView.Apply(Film(), NotNew, "goldf", NoFilters, null)));
        Assert.Equal([1, 2], Ids(RowView.Apply(Film(), NotNew, ".99", NoFilters, null).Where(r => (int)r[0]! < 3)));
    }

    [Theory]
    [InlineData("holes", new[] { 3 })]
    [InlineData("= ace goldfinger", new[] { 2 })]
    [InlineData("!= ace goldfinger", new[] { 1, 3, 4, 5 })]
    public void A_text_column_filter_is_contains_or_case_insensitive_equality(string expr, int[] ids)
        => Assert.Equal(ids, Ids(RowView.Apply(Film(), NotNew, null, new Dictionary<int, string> { [1] = expr }, null)));

    [Theory]
    [InlineData("< 3", new[] { 1, 3, 5 })]
    [InlineData(">= 2.99", new[] { 2, 3, 5 })]
    [InlineData("= 4.99", new[] { 2 })]
    [InlineData("!= 2.99", new[] { 1, 2 })]      // NULL matches no filter, != included
    [InlineData("> cheap", new int[0])]          // an operator against a non-number matches nothing
    public void An_operator_compares_numerically(string expr, int[] ids)
        => Assert.Equal(ids, Ids(RowView.Apply(Film(), NotNew, null, new Dictionary<int, string> { [2] = expr }, null)));

    [Fact]
    public void Column_filters_and_the_quick_filter_are_anded()
    {
        var filters = new Dictionary<int, string> { [2] = "< 4", [3] = "horror" };
        Assert.Equal([5], Ids(RowView.Apply(Film(), NotNew, null, filters, null)));
        Assert.Empty(RowView.Apply(Film(), NotNew, "goldfinger", filters, null));
    }

    [Fact]
    public void New_rows_are_always_shown_pinned_at_the_bottom_in_insertion_order()
    {
        var rows = Film();
        object?[] first = [null, null, null, null], second = [null, null, null, null];
        rows.Insert(0, first);
        rows.Insert(2, second);
        var isNew = (object?[] r) => ReferenceEquals(r, first) || ReferenceEquals(r, second);

        var view = RowView.Apply(rows, isNew, "horror", NoFilters, new RowSort(0, SortDirection.Descending));

        Assert.Equal(4, view.Count);
        Assert.Equal([5, 2], view.Take(2).Select(r => (int)r[0]!).ToArray());
        Assert.Same(first, view[2]);
        Assert.Same(second, view[3]);
    }

    // ---- the view model -----------------------------------------------------------------------------

    private static ResultSetViewModel Result(int rows = 5)
    {
        var columns = new List<ColumnDescriptor>
        {
            new("film_id", "int4", typeof(int)),
            new("title", "text", typeof(string)),
            new("rental_rate", "numeric", typeof(decimal)),
            new("metadata", "jsonb", typeof(string)),
        };
        var data = Film().Take(rows).ToList();
        var result = new QueryResult(columns, data, data.Count, TimeSpan.Zero, null, null, false);
        var editable = columns.Select((c, i) => new EditableColumn(i, c.Name, IsPrimaryKey: i == 0)).ToList();
        var rs = new ResultSetViewModel(result, "select * from film", pageable: true)
        {
            PrimaryKeyColumns = [0],
            EditTarget = new EditTarget("public", "film", editable),
        };
        rs.CaptureOriginals();
        return rs;
    }

    [Fact]
    public void Unfiltered_the_view_is_the_loaded_rows_and_follows_them_change_for_change()
    {
        var rs = Result();
        Assert.Equal(rs.Rows, rs.ViewRows);

        var resets = 0;
        rs.ViewRows.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
        };
        rs.AppendPage([[6, "ZORRO ARK", 1.99m, null]], hasMore: false);

        Assert.Equal(rs.Rows, rs.ViewRows);
        // A page arrives as one Reset (RowCollection, §5.8) and the view follows with one of its own — never one
        // per row. The scroll survives it: Ui/RowAppendTests.Appending_a_page_keeps_the_scroll_position.
        Assert.Equal(1, resets);
    }

    [Fact]
    public void Filtering_hides_rows_from_the_view_but_not_from_the_result()
    {
        var rs = Result();
        rs.Filter.QuickFilter = "horror";

        Assert.Equal([2, 5], Ids(rs.ViewRows));
        Assert.Equal(5, rs.Rows.Count);
        Assert.Equal("2 / 5 loaded", rs.Filter.MatchText);
        Assert.Equal("2 shown (filtered locally)", rs.Filter.FooterText);
    }

    [Fact]
    public void A_page_that_arrives_under_a_filter_and_sort_is_filtered_and_sorted_in_one_rebuild()
    {
        var rs = Result();
        rs.Filter.Sort = new RowSort(2, SortDirection.Descending);
        rs.Filter.SetColumnFilter(2, "> 1");
        var rebuilds = 0;
        rs.ViewRows.CollectionChanged += (_, _) => rebuilds++;

        rs.AppendPage([[6, "ZORRO ARK", 9.99m, null], [7, "CHEAP ONE", 0.5m, null]], hasMore: false);

        Assert.Equal([6, 2, 3, 5], Ids(rs.ViewRows));
        Assert.Equal(1, rebuilds);
    }

    [Fact]
    public void A_new_row_appears_at_the_bottom_under_a_filter_and_a_pending_change_still_counts_hidden_rows()
    {
        var rs = Result();
        rs.Filter.QuickFilter = "horror";
        var hidden = rs.Rows.Single(r => (int)r[0]! == 1);
        rs.SetCell(hidden, 1, "RENAMED");

        var added = rs.AddRow();

        Assert.Same(added, rs.ViewRows[^1]);
        Assert.Equal(2, rs.PendingCount);                  // the edit on a row the filter hides still counts
        Assert.DoesNotContain(hidden, rs.ViewRows);
    }

    [Fact]
    public void A_saved_row_swapped_for_its_committed_array_keeps_its_place_in_a_sorted_view()
    {
        var rs = Result();
        rs.Filter.Sort = new RowSort(2, SortDirection.Ascending);
        var before = Ids(rs.ViewRows);
        var old = rs.Rows.Single(r => (int)r[0]! == 3);
        object?[] committed = [3, "ADAPTATION HOLES", 2.99m, null];

        rs.ReplaceRow(old, committed);

        Assert.Equal(before, Ids(rs.ViewRows));
        Assert.Contains(committed, rs.ViewRows);
        Assert.DoesNotContain(old, rs.ViewRows);
    }

    [Fact]
    public void Changing_the_filter_or_sort_announces_it_so_the_grid_can_clear_its_selection()
    {
        var rs = Result();
        var changes = 0;
        rs.Filter.ViewChanged += () => changes++;

        rs.Filter.CycleSort(1);
        rs.Filter.QuickFilter = "a";
        rs.Filter.SetColumnFilter(2, "< 3");
        rs.AppendPage([[6, "X", 1m, null]], hasMore: false);   // rows underneath changing is not a filter change

        Assert.Equal(3, changes);
    }

    [Fact]
    public void A_jsonb_column_does_not_cycle_a_sort()
    {
        var rs = Result();
        rs.Filter.CycleSort(3);
        Assert.Null(rs.Filter.Sort);
    }

    [Fact]
    public void Escape_clears_text_then_closes_but_never_over_an_active_column_filter()
    {
        var rs = Result();
        var f = rs.Filter;
        f.Open();
        f.QuickFilter = "horror";

        Assert.True(f.Escape());
        Assert.Equal("", f.QuickFilter);
        Assert.True(f.IsFilterOpen);

        f.SetColumnFilter(2, "< 3");
        Assert.False(f.Escape());
        Assert.True(f.IsFilterOpen);              // active state is never hidden

        f.ClearAndClose();                         // ✕ is the way out, and it clears everything
        Assert.False(f.IsFilterOpen);
        Assert.False(f.IsFiltered);
        Assert.Equal(rs.Rows, rs.ViewRows);
    }

    [Fact]
    public void An_edit_on_the_first_row_of_a_descending_sort_updates_that_rows_key()
    {
        // The grid edits ViewRows[i]; the save keys off the row array itself. Under a sort, view index 0 is
        // not Rows[0] — the DML must still name the row that was edited, not the one loaded first.
        var rs = Result();
        rs.Filter.Sort = new RowSort(0, SortDirection.Descending);   // by id: 5, 4, 3, 2, 1
        var top = rs.ViewRows[0];
        Assert.Equal(5, (int)top[0]!);
        Assert.NotSame(rs.Rows[0], top);

        rs.SetCell(top, 1, "RENAMED");

        var change = Assert.Single(ResultEditModel.BuildPendingChanges(rs, rs.EditTarget!));
        Assert.Equal(ResultEditModel.ChangeKind.Update, change.Kind);
        Assert.Same(top, change.Row);
        Assert.Contains(change.Command.Parameters, p => Equals(p.Value, "RENAMED"));
        Assert.Contains(change.Command.Parameters, p => Equals(p.Value, 5));
        Assert.DoesNotContain(change.Command.Parameters, p => Equals(p.Value, 1));
    }

    [Fact]
    public void The_quick_filter_skips_hidden_columns_and_hiding_one_reapplies_it()
    {
        var rs = Result();
        rs.Filter.QuickFilter = "horror";                    // only the metadata column says it
        Assert.Equal([2, 5], Ids(rs.ViewRows));
        var changes = 0;
        rs.Filter.ViewChanged += () => changes++;

        rs.ColumnLayout.Hide(3);
        Assert.Empty(rs.ViewRows);
        Assert.Equal(1, changes);

        rs.ColumnLayout.Freeze(1);                          // not a visibility change: nothing rebuilt
        Assert.Equal(1, changes);

        rs.ColumnLayout.ShowAll();
        Assert.Equal([2, 5], Ids(rs.ViewRows));
    }

    [Fact]
    public void Unfiltered_a_page_raises_no_count_notifications()
    {
        // ⤓ all arrives one add per row; with no filter nothing shown reads the counts, and each notification
        // re-synced the filter bar. The counts are raised again by the rebuild that makes them visible.
        var rs = Result();
        var raised = 0;
        rs.Filter.PropertyChanged += (_, _) => raised++;

        rs.AppendPage([[6, "ZORRO ARK", 1.99m, null], [7, "CHEAP ONE", 0.5m, null]], hasMore: false);
        Assert.Equal(0, raised);

        rs.Filter.QuickFilter = "zorro";
        Assert.Equal("1 / 7 loaded", rs.Filter.MatchText);
    }

    [Fact]
    public void Clear_leaves_the_sort_alone_and_unsorted_unfiltered_returns_the_load_order()
    {
        var rs = Result();
        rs.Filter.Sort = new RowSort(2, SortDirection.Descending);
        rs.Filter.QuickFilter = "a";
        rs.Filter.ClearAndClose();
        Assert.NotNull(rs.Filter.Sort);
        Assert.Equal([4, 2, 3, 5, 1], Ids(rs.ViewRows));

        rs.Filter.Sort = null;
        Assert.Equal([1, 2, 3, 4, 5], Ids(rs.ViewRows));
    }

    [Fact]
    public void The_empty_state_is_filters_that_leave_no_loaded_row()
    {
        var rs = Result();
        rs.Filter.QuickFilter = "no such film";
        Assert.True(rs.Filter.IsEmptyResult);
        rs.AddRow();                                // a pending insert is shown, but it isn't a match
        Assert.True(rs.Filter.IsEmptyResult);
        rs.Filter.QuickFilter = "";
        Assert.False(rs.Filter.IsEmptyResult);
    }

    [Fact]
    public void The_header_menu_sorts_a_column_either_way_but_never_a_document()
    {
        var rs = Result();
        rs.Filter.SortBy(2, SortDirection.Descending);
        Assert.Equal(new RowSort(2, SortDirection.Descending), rs.Filter.Sort);
        rs.Filter.SortBy(3, SortDirection.Ascending);           // jsonb: refused, the sort stays
        Assert.Equal(new RowSort(2, SortDirection.Descending), rs.Filter.Sort);
    }

    // ---- typing on a large result waits for a pause ------------------------------------------------

    private sealed class ManualDebouncer
    {
        public Action? Pending { get; private set; }
        public int Cancelled { get; private set; }

        public IDisposable Schedule(TimeSpan _, Action work)
        {
            Pending = work;
            return new Cancel(this, work);
        }

        public void Fire() { var w = Pending; Pending = null; w?.Invoke(); }

        private sealed class Cancel(ManualDebouncer owner, Action work) : IDisposable
        {
            public void Dispose()
            {
                if (owner.Pending != work) return;
                owner.Pending = null;
                owner.Cancelled++;
            }
        }
    }

    /// <summary>12,500 rows × 4 columns — exactly the cell count where a typed filter starts to wait.</summary>
    private static ResultSetViewModel Large(ManualDebouncer debouncer)
    {
        var rs = Result(rows: 0);
        rs.AppendPage(Enumerable.Range(1, ResultFilterViewModel.DebounceCells / 4)
            .Select(i => new object?[] { i, i % 2 == 0 ? "EVEN" : "ODD", 1m, null }).ToList(), hasMore: false);
        rs.Filter.Debouncer = debouncer.Schedule;
        return rs;
    }

    [Fact]
    public void On_a_large_result_a_typed_filter_rebuilds_once_typing_pauses()
    {
        var debouncer = new ManualDebouncer();
        var rs = Large(debouncer);

        rs.Filter.QuickFilter = "o";
        rs.Filter.QuickFilter = "od";
        Assert.Equal(ResultFilterViewModel.DebounceCells / 4, rs.ViewRows.Count);   // nothing rebuilt yet
        Assert.Equal(1, debouncer.Cancelled);                                         // "o" was superseded

        debouncer.Fire();
        Assert.Equal(ResultFilterViewModel.DebounceCells / 8, rs.ViewRows.Count);
    }

    [Fact]
    public void A_sort_while_a_typed_filter_waits_applies_both_at_once()
    {
        var debouncer = new ManualDebouncer();
        var rs = Large(debouncer);

        rs.Filter.SetColumnFilter(1, "= even");
        rs.Filter.Sort = new RowSort(0, SortDirection.Descending);

        Assert.Null(debouncer.Pending);   // the sort's rebuild read the filter text; nothing left to do
        Assert.All(rs.ViewRows, r => Assert.Equal("EVEN", r[1]));
        Assert.Equal(ResultFilterViewModel.DebounceCells / 4, (int)rs.ViewRows[0][0]!);
    }

    [Fact]
    public void A_small_result_filters_on_every_keystroke_even_with_a_debouncer()
    {
        var debouncer = new ManualDebouncer();
        var rs = Result();
        rs.Filter.Debouncer = debouncer.Schedule;

        rs.Filter.QuickFilter = "horror";

        Assert.Null(debouncer.Pending);
        Assert.Equal([2, 5], Ids(rs.ViewRows));
    }

    // ---- the loaded-only notice ----------------------------------------------------------------------

    [Fact]
    public void A_sort_on_a_part_fetched_result_says_it_covers_the_loaded_rows_only()
    {
        var rs = Result();
        rs.HasMore = true;
        var sort = new RowSort(1, SortDirection.Ascending);

        CultureScope.In("en-US", () => Assert.Equal(
            "Sorted by “title” ascending over the loaded rows only (5 rows) — “all” fetches the rest.",
            LoadedOnlyNotice.For(rs, LoadedOnlyNotice.Sorted(rs, sort))));

        rs.HasMore = false;
        Assert.Null(LoadedOnlyNotice.For(rs, LoadedOnlyNotice.Sorted(rs, sort)));   // every row is here
    }
}

