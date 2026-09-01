using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.ViewModels;
using Bearing.Core.Logging;
using Xunit;

namespace Bearing.App.Tests;

public class HistoryPanelTests
{
    private static QueryLogEntry Entry(DateTimeOffset at, bool ok, string conn = "local", string sql = "select 1")
        => new()
        {
            ExecutedAt = at,
            ProviderId = "postgres",
            ConnectionName = conn,
            Database = "pagila",
            SqlText = sql,
            Success = ok,
            ErrorMessage = ok ? null : "boom",
        };

    [Fact]
    public void DayCaption_labels_today_yesterday_and_dates()
    {
        var today = new DateOnly(2026, 7, 18);
        Assert.Equal("TODAY", HistoryPanelViewModel.DayCaption(today, today));
        Assert.Equal("YESTERDAY", HistoryPanelViewModel.DayCaption(today.AddDays(-1), today));
        Assert.Equal("16.07.2026", HistoryPanelViewModel.DayCaption(new DateOnly(2026, 7, 16), today));
    }

    [Fact]
    public async Task Reload_groups_by_day_newest_first()
    {
        var now = DateTimeOffset.Now;
        var entries = new[]
        {
            Entry(now, ok: true),
            Entry(now.AddDays(-1), ok: false),
            Entry(now.AddDays(-1), ok: true),
            Entry(now.AddDays(-5), ok: true),
        };
        var vm = Make(entries);

        await vm.ReloadAsync(CancellationToken.None);

        Assert.Equal(3, vm.Groups.Count);                // 3 distinct days
        Assert.Single(vm.Groups[0].Rows);                // today
        Assert.Equal(2, vm.Groups[1].Rows.Count);        // yesterday
        Assert.Single(vm.Groups[2].Rows);                // 5 days ago
    }

    [Fact]
    public async Task Filter_pills_narrow_to_ok_or_error()
    {
        var now = DateTimeOffset.Now;
        var vm = Make(new[] { Entry(now, true), Entry(now, false), Entry(now, true) });
        await vm.ReloadAsync(CancellationToken.None);

        vm.Filter = HistoryFilter.Error;
        Assert.Equal(1, vm.Groups.Sum(g => g.Rows.Count));
        Assert.True(vm.Groups.SelectMany(g => g.Rows).Single().IsError);

        vm.Filter = HistoryFilter.Ok;
        Assert.Equal(2, vm.Groups.Sum(g => g.Rows.Count));

        vm.Filter = HistoryFilter.All;
        Assert.Equal(3, vm.Groups.Sum(g => g.Rows.Count));
    }

    [Fact]
    public async Task Row_carries_connection_color_and_error_marker()
    {
        var vm = Make(new[] { Entry(DateTimeOffset.Now, ok: false, conn: "prod") });
        await vm.ReloadAsync(CancellationToken.None);

        var row = vm.Groups.SelectMany(g => g.Rows).Single();
        Assert.Equal("#E46876", row.ConnectionColor);
        Assert.StartsWith("✕", row.DisplayQuery);
        Assert.Equal("#D2555A", row.QueryColorHex);   // Error.Red
    }

    [Fact]
    public async Task A_reload_keeps_the_selection_on_the_same_logged_row()
    {
        // Rows are rebuilt wholesale, so the projection the user selected is gone after a reload and the
        // view-model has to re-find it by the entry behind it. This used to drop the selection instead,
        // which was harmless while reloading only happened on a panel switch — but since #78 a reload
        // happens every time a query lands in the log, and dropping it would clear the user's selection and
        // collapse the preview it drives, under them, mid-session.
        var now = DateTimeOffset.Now;
        var vm = Make(new[] { Entry(now, ok: true), Entry(now.AddDays(-1), ok: true) });
        await vm.ReloadAsync(CancellationToken.None);
        var yesterday = vm.Groups[1].Rows[0];
        vm.SelectedRow = yesterday;

        await vm.ReloadAsync(CancellationToken.None);

        Assert.NotNull(vm.SelectedRow);
        Assert.NotSame(yesterday, vm.SelectedRow);                       // a fresh projection…
        Assert.Same(yesterday.Entry, vm.SelectedRow!.Entry);             // …of the same logged row
        Assert.Contains(vm.SelectedRow, vm.Groups.SelectMany(g => g.Rows));
    }

    [Fact]
    public async Task A_new_query_arrives_without_disturbing_the_selection()
    {
        // The #78 case end to end: a run finishes, the panel reloads, the new row is on screen and the row
        // the user was reading is still selected.
        var now = DateTimeOffset.Now;
        var entries = new List<QueryLogEntry> { Entry(now.AddMinutes(-5), ok: true) };
        var vm = new HistoryPanelViewModel(
            (_, _) => Task.FromResult<IReadOnlyList<QueryLogEntry>>(entries.ToList()),
            _ => "#7AA89F");

        await vm.ReloadAsync(CancellationToken.None);
        var reading = vm.Groups[0].Rows[0];
        vm.SelectedRow = reading;

        entries.Insert(0, Entry(now, ok: true) with { SqlText = "select just_ran" });
        await vm.ReloadAsync(CancellationToken.None);

        Assert.Contains(vm.Groups.SelectMany(g => g.Rows), r => r.Sql == "select just_ran");
        Assert.Same(reading.Entry, vm.SelectedRow!.Entry);
    }

    [Fact]
    public async Task A_selection_that_is_filtered_away_is_dropped()
    {
        // The other half: when the row really is gone — a filter switch, a search that no longer matches it
        // — there is nothing to re-find, and the preview closes rather than showing a query that is no
        // longer in the list.
        var now = DateTimeOffset.Now;
        var vm = Make(new[] { Entry(now, ok: true), Entry(now.AddMinutes(-1), ok: false) });
        await vm.ReloadAsync(CancellationToken.None);
        vm.SelectedRow = vm.Groups.SelectMany(g => g.Rows).Single(r => !r.IsError);

        vm.Filter = HistoryFilter.Error;

        Assert.Null(vm.SelectedRow);
    }

    [Fact]
    public async Task A_superseded_reload_does_not_overwrite_a_newer_one()
    {
        // Running a script fires one refresh per statement, and unordered concurrent searches could settle
        // the panel on an older answer than one already applied. The caller cancels the previous reload, so
        // a cancelled one must land nothing at all — not its rows, and not an error in the status line.
        var now = DateTimeOffset.Now;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = new[] { Entry(now.AddMinutes(-5), ok: true) };
        var fresh = new[] { Entry(now, ok: true), Entry(now.AddMinutes(-5), ok: true) };
        var first = true;

        var vm = new HistoryPanelViewModel(
            async (_, ct) =>
            {
                if (!first) return fresh;
                first = false;
                await gate.Task;                                     // the slow, superseded search
                ct.ThrowIfCancellationRequested();
                return (IReadOnlyList<QueryLogEntry>)stale;
            },
            _ => "#7AA89F");

        using var superseded = new CancellationTokenSource();
        var slow = vm.ReloadAsync(superseded.Token);
        await vm.ReloadAsync(CancellationToken.None);                 // the newer one wins
        Assert.Equal(2, vm.Groups.SelectMany(g => g.Rows).Count());

        superseded.Cancel();
        gate.SetResult();
        await slow;

        Assert.Equal(2, vm.Groups.SelectMany(g => g.Rows).Count());
        Assert.DoesNotContain("error", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    private static HistoryPanelViewModel Make(IReadOnlyList<QueryLogEntry> entries)
        => new(
            (_, _) => Task.FromResult(entries),
            name => name == "prod" ? "#E46876" : "#7AA89F");
}
