using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squirrel.App.ViewModels;
using Squirrel.Core.Logging;
using Xunit;

namespace Squirrel.App.Tests;

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
        Assert.Equal(1, vm.Groups[0].Rows.Count);        // today
        Assert.Equal(2, vm.Groups[1].Rows.Count);        // yesterday
        Assert.Equal(1, vm.Groups[2].Rows.Count);        // 5 days ago
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
        Assert.Equal("#C34043", row.QueryColorHex);
    }

    private static HistoryPanelViewModel Make(IReadOnlyList<QueryLogEntry> entries)
        => new(
            (_, _) => Task.FromResult(entries),
            name => name == "prod" ? "#E46876" : "#7AA89F");
}
