using System;
using System.Linq;
using Bearing.App.Editing;
using Bearing.App.Results;
using Bearing.App.Tests.Ui;
using Bearing.App.ViewModels;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Each inline zone is its own results context (docs/specs/2026-11-06-inline-results.md §4), and unsaved grid
/// edits are never dropped without asking (§7). The view-model halves, without a window.
/// </summary>
public class ResultContextTests
{
    private static ResultSetViewModel Editable()
    {
        var (rs, _) = ResultsHarness.WideEditableResult(columns: 2, rows: 3);
        return rs;
    }

    private static void Edit(ResultSetViewModel rs, int row = 0) => rs.SetCell(rs.Rows[row], 1, $"edited {Guid.NewGuid()}");

    [Fact]
    public void FK_navigation_in_a_zone_stacks_its_frame_and_Back_returns()
    {
        var run = InlineResultsTests.OneSet();
        var zone = new InlineResultZone(5, [run], caption: "2 result sets");
        var target = InlineResultsTests.OneSet();

        zone.Push([target]);
        Assert.Same(target, Assert.Single(zone.Sets));
        Assert.True(zone.CanGoBack);
        Assert.Null(zone.Caption);   // the batch caption names the run's sets, not the FK frame on top

        zone.GoBack();
        Assert.Same(run, Assert.Single(zone.Sets));
        Assert.False(zone.CanGoBack);
        Assert.Equal("2 result sets", zone.Caption);
    }

    [Fact]
    public void FK_navigation_in_a_zone_leaves_the_tabs_results_alone()
    {
        var tab = new EditorTabViewModel("t", "select 1;");
        var run = InlineResultsTests.OneSet();
        tab.SetRunResults([run]);
        var zone = new InlineResultZone(9, [run], caption: null);

        ((IResultFrames)zone).Push([InlineResultsTests.OneSet()]);

        Assert.Same(run, Assert.Single(tab.Results));
        Assert.False(tab.CanGoBack);
    }

    [Fact]
    public void The_placement_toggle_hands_the_pane_the_zones_frames()
    {
        var tab = new EditorTabViewModel("t", "select 1;");
        var run = Editable();
        var zone = new InlineResultZone(9, [run], caption: null);
        var fk = Editable();
        zone.Push([fk]);
        Edit(fk);

        Assert.True(tab.ShowZoneInPane(zone));

        Assert.Same(fk, Assert.Single(tab.Results));
        Assert.True(Assert.Single(tab.Results).HasPendingChanges);
        Assert.True(tab.CanGoBack);
        tab.GoBack();
        Assert.Same(run, Assert.Single(tab.Results));
    }

    [Fact]
    public void The_placement_toggle_is_refused_while_the_tab_runs()
    {
        var tab = new EditorTabViewModel("t", "select 1;");
        var before = tab.Results;
        tab.BeginRun();

        Assert.False(tab.ShowZoneInPane(new InlineResultZone(0, [InlineResultsTests.OneSet()], null)));
        Assert.Same(before, tab.Results);
        tab.EndRun();
    }

    [Fact]
    public void A_run_loses_the_panes_edits_and_the_replaced_zones_counting_a_shared_set_once()
    {
        const string text = "select 1;\nselect 2;\n";
        var tab = new EditorTabViewModel("t", text);
        var first = Editable();
        var second = Editable();
        Edit(first);
        Edit(second);
        Edit(second, row: 1);
        tab.SetRunResults([first]);   // the pane shows the first statement's set, which is also its zone's
        var all = InlineResultPlacement.Scope(PostgresDialect.Instance, text, 0, isWholeBuffer: true);
        tab.ApplyInlineRun(all, [new InlineResultZone(8, [first], null), new InlineResultZone(18, [second], null)]);

        var firstOnly = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 1;", 0, isWholeBuffer: false);
        var loss = PendingEditsGuard.ForRun(tab, firstOnly, zonesShown: true);
        Assert.Equal(1, loss.Changes);   // the shared set once; the second zone isn't inside what runs
        Assert.Equal(1, loss.Places);

        var everything = PendingEditsGuard.ForRun(tab, all, zonesShown: true);
        Assert.Equal(3, everything.Changes);
        Assert.Equal(2, everything.Places);
        Assert.Equal("3 unsaved changes in 2 results", everything.Text);
    }

    /// <summary>Inline mode: the last run's sets are both the pane's and their zone's. Running another
    /// statement replaces the pane but leaves that zone — edits and all — on screen, so nothing is lost and
    /// nothing may be reverted.</summary>
    [Fact]
    public void A_run_elsewhere_doesnt_count_the_panes_sets_a_surviving_zone_still_shows()
    {
        const string text = "select 1;\nselect 2;\n";
        var tab = new EditorTabViewModel("t", text);
        var first = Editable();
        Edit(first);
        tab.SetRunResults([first]);
        var one = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 1;", 0, isWholeBuffer: false);
        tab.ApplyInlineRun(one, [new InlineResultZone(8, [first], null)]);
        var second = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 2;", 10, isWholeBuffer: false);

        Assert.True(PendingEditsGuard.ForRun(tab, second, zonesShown: true).IsEmpty);
        // Pane mode: the zone isn't on screen, so the pane dropping the set is a loss.
        Assert.Equal(1, PendingEditsGuard.ForRun(tab, second, zonesShown: false).Changes);
    }

    [Fact]
    public void Discarding_reverts_the_edits_except_the_ones_still_shown()
    {
        var gone = Editable();
        var shown = Editable();
        Edit(gone);
        Edit(shown);
        var zone = new InlineResultZone(0, [gone, shown], null);
        var tab = new EditorTabViewModel("t", "select 1;");
        var loss = PendingEditsGuard.ForZone(tab, zone);

        loss.Discard(keep: [shown]);

        Assert.False(gone.HasPendingChanges);
        Assert.True(shown.HasPendingChanges);
    }

    [Fact]
    public void A_zone_on_a_Back_stack_still_counts()
    {
        var zone = new InlineResultZone(0, [Editable()], null);
        Edit(zone.Sets[0]);
        zone.Push([InlineResultsTests.OneSet()]);

        Assert.Equal(1, PendingEditsGuard.ForZone(new EditorTabViewModel("t", ""), zone).Changes);
    }

    /// <summary>Closing a zone whose sets the pane also shows loses nothing: the pane keeps them.</summary>
    [Fact]
    public void Closing_a_zone_doesnt_count_sets_the_pane_still_holds()
    {
        var tab = new EditorTabViewModel("t", "select 1;");
        var set = Editable();
        Edit(set);
        tab.SetRunResults([set]);
        var zone = new InlineResultZone(8, [set], null);
        tab.ApplyInlineRun(InlineResultPlacement.Scope(PostgresDialect.Instance, "select 1;", 0, false), [zone]);

        Assert.True(PendingEditsGuard.ForZone(tab, zone).IsEmpty);
    }

    /// <summary>A zone's placement toggle replaces the pane's frames: edits only the pane holds (an FK frame
    /// followed there) are a loss to ask about; the pane's run sets, which a zone also holds, are not.</summary>
    [Fact]
    public void Handing_the_pane_a_zone_counts_only_edits_no_zone_holds()
    {
        var tab = new EditorTabViewModel("t", "select 1;");
        var run = Editable();
        Edit(run);
        tab.SetRunResults([run]);
        var zone = new InlineResultZone(8, [run], null);
        tab.ApplyInlineRun(InlineResultPlacement.Scope(PostgresDialect.Instance, "select 1;", 0, false), [zone]);
        Assert.True(PendingEditsGuard.ForPaneTakeover(tab).IsEmpty);

        var fk = Editable();
        Edit(fk);
        tab.PushResults([fk]);

        var loss = PendingEditsGuard.ForPaneTakeover(tab);
        Assert.Equal(1, loss.Changes);
        Assert.Same(fk, Assert.Single(loss.Sets));
    }

    /// <summary>A Run over a multi-statement selection puts all its sets in one zone; re-running from the zone
    /// covers every statement again, not just the one it hangs under.</summary>
    [Fact]
    public void A_zone_from_a_multi_statement_selection_re_runs_all_its_statements()
    {
        const string text = "select 0;\nselect 1;\nselect 2;\nselect 3;\n";
        var selStart = text.IndexOf("select 1");
        var selection = text[selStart..(text.IndexOf("select 3") + "select 3;".Length)];
        var scope = InlineResultPlacement.Scope(PostgresDialect.Instance, selection, selStart, isWholeBuffer: false);
        var zone = Assert.Single(InlineResultPlacement.Assign(scope,
            [InlineResultsTests.OneSet(), InlineResultsTests.OneSet(), InlineResultsTests.OneSet()]));
        Assert.Equal(3, zone.StatementCount);

        var span = InlineResultPlacement.ZoneSpan(PostgresDialect.Instance, text, zone.Offset, zone.StatementCount);

        Assert.NotNull(span);
        var rerun = text[span!.Value.Start..span.Value.End];
        Assert.Contains("select 1", rerun);
        Assert.Contains("select 3", rerun);
        Assert.DoesNotContain("select 0", rerun);
    }

    [Fact]
    public void A_single_statement_zone_re_runs_just_its_statement()
    {
        const string text = "select 1;select 2;\n";
        var span = InlineResultPlacement.ZoneSpan(PostgresDialect.Instance, text, "select 1;".Length, 1);

        Assert.NotNull(span);
        Assert.Equal("select 1;", text[span!.Value.Start..span.Value.End].Trim());
    }

    [Fact]
    public void New_text_in_the_tab_keeps_only_the_zones_holding_edits()
    {
        var tab = new EditorTabViewModel("t", "select 1;\nselect 2;\n");
        var clean = new InlineResultZone(8, [InlineResultsTests.OneSet()], null);
        var dirty = new InlineResultZone(18, [Editable()], null);
        Edit(dirty.Sets[0]);
        var all = InlineResultPlacement.Scope(PostgresDialect.Instance, tab.Text, 0, isWholeBuffer: true);
        tab.ApplyInlineRun(all, [clean, dirty]);
        tab.Text = "x";

        tab.ClearInlineZones();

        var kept = Assert.Single(tab.InlineZones);
        Assert.Same(dirty, kept);
        Assert.True(kept.StatementRemoved);
        Assert.Equal(1, kept.Offset);   // clamped into the new text
        Assert.Equal(1, tab.PendingEditCount);
    }

    [Fact]
    public void Sizing_fits_the_rows_up_to_the_cap()
    {
        const double row = 26;
        var cap = InlineGridSizing.DefaultCap(row);
        var three = InlineGridSizing.GridHeight(3, row, cap);
        var hundred = InlineGridSizing.GridHeight(100, row, cap);

        Assert.Equal(InlineGridSizing.HeaderHeight + 3 * row + InlineGridSizing.ScrollbarReserve, three);
        Assert.Equal(InlineGridSizing.HeaderHeight + cap + InlineGridSizing.ScrollbarReserve, hundred);
        Assert.Equal(InlineGridSizing.GridHeight(1, row, cap), InlineGridSizing.GridHeight(0, row, cap));   // a blank row under the header
        Assert.Equal(2 * row, InlineGridSizing.ClampCap(1, row));
        Assert.Equal(60 * row, InlineGridSizing.ClampCap(1e6, row));
    }

    [Fact]
    public void An_unattributed_batch_zone_is_marked_as_the_whole_buffers()
    {
        const string text = "select 1;\nselect 2;\n";
        var all = InlineResultPlacement.Scope(PostgresDialect.Instance, text, 0, isWholeBuffer: true);
        var zones = InlineResultPlacement.Assign(all, [InlineResultsTests.OneSet(), InlineResultsTests.OneSet(), InlineResultsTests.OneSet()]);

        Assert.True(Assert.Single(zones).FromWholeBuffer);

        var one = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 1;", 0, isWholeBuffer: false);
        Assert.False(Assert.Single(InlineResultPlacement.Assign(one, [InlineResultsTests.OneSet()])).FromWholeBuffer);
    }
}

/// <summary>The editable fixture <see cref="ResultContextTests"/> uses, for the coordinator's tests.</summary>
internal static class ResultContextTestsAccess
{
    public static ResultSetViewModel Editable()
    {
        var (rs, _) = ResultsHarness.WideEditableResult(columns: 2, rows: 3);
        return rs;
    }
}
