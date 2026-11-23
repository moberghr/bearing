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
        Assert.Equal("Error.Red", row.QueryColorKey);
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

    /// <summary>
    /// A history where an agent's statements are indistinguishable from your own is the gap §1.11's record
    /// exists to close, so the mark is in the list line rather than only in the detail — it is a thing you
    /// scan for, not one you look up.
    /// </summary>
    [Fact]
    public void A_row_something_else_ran_is_marked_in_the_list()
    {
        var mine = new HistoryRowViewModel(Entry(DateTimeOffset.Now, ok: true), null);
        var agents = new HistoryRowViewModel(
            Entry(DateTimeOffset.Now, ok: true) with { Origin = QueryOrigin.Cli }, null);

        Assert.False(mine.IsExternal);
        Assert.Equal("select 1", mine.DisplayQuery);

        Assert.True(agents.IsExternal);
        Assert.Equal("[cli] select 1", agents.DisplayQuery);
        Assert.Contains("cli", agents.Detail);
    }

    /// <summary>Both marks survive together: a refused statement an agent sent is the row most worth
    /// seeing, and it is an error *and* external.</summary>
    [Fact]
    public void A_failed_external_row_carries_both_marks()
    {
        var row = new HistoryRowViewModel(
            Entry(DateTimeOffset.Now, ok: false) with { Origin = QueryOrigin.Cli }, null);

        Assert.Equal("✕ [cli] select 1", row.DisplayQuery);
    }

    /// <summary>
    /// #103: a row that ran with <c>:name</c> values shows them beside the query, and opening it gives the
    /// query as written with a <c>@set</c> line per value above it — so it runs again without a prompt.
    /// </summary>
    [Fact]
    public void A_row_with_values_shows_them_and_opens_with_set_lines_above_the_query()
    {
        var entry = Entry(DateTimeOffset.Now, ok: true, sql: "select * from rental where customer_id = :id and d > :since") with
        {
            Parameters =
            [
                new QueryLogParameter("id", "42", Bearing.Core.Data.QueryParameterKind.Number),
                new QueryLogParameter("since", "2005-06-01", Bearing.Core.Data.QueryParameterKind.Text),
            ],
        };

        var row = new HistoryRowViewModel(entry, null);

        Assert.Equal("id = 42 · since = '2005-06-01'", row.ParametersText);
        Assert.True(row.HasParameters);
        Assert.Equal(
            "@set id = 42\n@set since = '2005-06-01'\nselect * from rental where customer_id = :id and d > :since",
            row.OpenText);
        Assert.Equal("select * from rental where customer_id = :id and d > :since", row.Query);   // the list line
    }

    /// <summary>Without stored values — none given, or dropped because literals are redacted — the row opens
    /// as plain SQL, and running it asks.</summary>
    [Fact]
    public void A_row_without_values_opens_as_its_sql()
    {
        var row = new HistoryRowViewModel(Entry(DateTimeOffset.Now, ok: true, sql: "select :id"), null);

        Assert.False(row.HasParameters);
        Assert.Equal("select :id", row.OpenText);
    }

    // ---- bookmarks (#111) ----------------------------------------------------------------------------

    /// <summary>A store stand-in that answers the starred filter from its own set, the way the log does,
    /// and can be told to refuse a write.</summary>
    private sealed class StarStore
    {
        public List<QueryLogEntry> Entries { get; } = [];
        public HashSet<long> Starred { get; } = [];
        public bool Fail { get; set; }
        public List<bool> StarredOnlyAsked { get; } = [];

        public Task<IReadOnlyList<QueryLogEntry>> Search(HistorySearch search, CancellationToken ct)
        {
            var starredOnly = search.StarredOnly;
            StarredOnlyAsked.Add(starredOnly);
            var rows = Entries.Select(e => e with { IsStarred = Starred.Contains(e.Id) })
                .Where(e => !starredOnly || e.IsStarred).ToList();
            return Task.FromResult<IReadOnlyList<QueryLogEntry>>(rows);
        }

        public Task Set(long id, bool starred, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("database is locked");
            if (starred) Starred.Add(id); else Starred.Remove(id);
            return Task.CompletedTask;
        }
    }

    private static (HistoryPanelViewModel Vm, StarStore Store) Starrable()
    {
        var store = new StarStore();
        var now = DateTimeOffset.Now;
        store.Entries.Add(Entry(now, ok: true, sql: "select a") with { Id = 1 });
        store.Entries.Add(Entry(now.AddMinutes(-1), ok: true, sql: "select b") with { Id = 2 });
        return (new HistoryPanelViewModel(store.Search, _ => null, store.Set), store);
    }

    [Fact]
    public async Task Starring_a_row_writes_it_to_the_log_and_marks_the_row()
    {
        var (vm, store) = Starrable();
        await vm.ReloadAsync(CancellationToken.None);
        var row = vm.Groups.SelectMany(g => g.Rows).Single(r => r.Sql == "select a");
        Assert.Equal("☆", row.StarGlyph);

        await vm.ToggleStarAsync(row);

        Assert.True(row.IsStarred);
        Assert.Equal("★", row.StarGlyph);
        Assert.Equal([1L], store.Starred);
    }

    [Fact]
    public async Task A_star_the_log_refused_is_taken_back_and_said()
    {
        // A star on screen that the log doesn't hold would let retention prune a row the user thinks is kept.
        var (vm, store) = Starrable();
        await vm.ReloadAsync(CancellationToken.None);
        var row = vm.Groups.SelectMany(g => g.Rows).First();
        store.Fail = true;

        await vm.ToggleStarAsync(row);

        Assert.False(row.IsStarred);
        Assert.Empty(store.Starred);
        Assert.Contains("Couldn't star", vm.Status);
    }

    [Fact]
    public async Task The_starred_filter_asks_the_log_rather_than_the_cached_rows()
    {
        var (vm, store) = Starrable();
        store.Starred.Add(2);
        await vm.ReloadAsync(CancellationToken.None);
        store.StarredOnlyAsked.Clear();

        vm.Filter = HistoryFilter.Starred;
        await Task.Yield();

        Assert.Equal([true], store.StarredOnlyAsked);
        Assert.Equal("select b", vm.Groups.SelectMany(g => g.Rows).Single().Sql);

        // …and leaving it asks for everything again.
        vm.Filter = HistoryFilter.All;
        await Task.Yield();
        Assert.Equal([true, false], store.StarredOnlyAsked);
        Assert.Equal(2, vm.Groups.Sum(g => g.Rows.Count));
    }

    [Fact]
    public async Task A_starred_only_answer_landing_after_leaving_the_starred_filter_is_dropped()
    {
        // The shell's per-statement refresh and the filter's own reload hold separate tokens. A starred-only
        // search started before the user clicked "All" can land after the "All" one; it must not leave
        // starred rows under the "All" pill.
        var now = DateTimeOffset.Now;
        var all = new[] { Entry(now, ok: true, sql: "select a") with { Id = 1 },
                          Entry(now.AddMinutes(-1), ok: true, sql: "select b") with { Id = 2, IsStarred = true } };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new HistoryPanelViewModel(
            async (search, _) =>
            {
                var starredOnly = search.StarredOnly;
                if (!starredOnly) return all;
                await gate.Task;                                      // the slow starred-only search
                return all.Where(e => e.IsStarred).ToList();
            },
            _ => null);
        vm.Filter = HistoryFilter.Starred;                            // starts a starred-only reload (held)
        var shellRefresh = vm.ReloadAsync(CancellationToken.None);    // a second one, as a new log row would

        vm.Filter = HistoryFilter.All;                                // the "All" reload lands first
        await Task.Yield();
        Assert.Equal(2, vm.Groups.Sum(g => g.Rows.Count));

        gate.SetResult();                                             // then the stale starred-only answers
        await shellRefresh;

        Assert.Equal(2, vm.Groups.Sum(g => g.Rows.Count));
        Assert.Equal("2 queries.", vm.Status);
    }

    [Fact]
    public async Task Unstarring_in_the_starred_view_drops_the_row()
    {
        var (vm, store) = Starrable();
        store.Starred.Add(1);
        vm.Filter = HistoryFilter.Starred;
        await vm.ReloadAsync(CancellationToken.None);
        var row = vm.Groups.SelectMany(g => g.Rows).Single();

        await vm.ToggleStarAsync(row);

        Assert.Empty(vm.Groups.SelectMany(g => g.Rows));
        Assert.Empty(store.Starred);
    }

    [Fact]
    public async Task A_row_not_yet_in_the_log_cannot_be_starred()
    {
        var store = new StarStore();
        store.Entries.Add(Entry(DateTimeOffset.Now, ok: true));   // Id 0: not persisted yet
        var vm = new HistoryPanelViewModel(store.Search, _ => null, store.Set);
        await vm.ReloadAsync(CancellationToken.None);

        await vm.ToggleStarAsync(vm.Groups[0].Rows[0]);

        Assert.False(vm.Groups[0].Rows[0].IsStarred);
        Assert.Empty(store.Starred);
    }

    // ---- connection filter and grouping ----------------------------------------------------------

    private static readonly Guid ProdId = Guid.NewGuid();
    private static readonly Guid LocalId = Guid.NewGuid();

    /// <summary>A store that answers like the log does for a connection (recorded name or id) and records
    /// what it was asked.</summary>
    private sealed class ConnectionStore
    {
        public List<QueryLogEntry> Entries { get; } = [];
        public List<HistoryConnection?> Asked { get; } = [];

        public Task<IReadOnlyList<QueryLogEntry>> Search(HistorySearch search, CancellationToken ct)
        {
            Asked.Add(search.Connection);
            var rows = Entries
                .Where(e => search.Connection is not { } c
                            || e.ConnectionName == c.Name || (c.Id is { } id && e.ConnectionId == id))
                .ToList();
            return Task.FromResult<IReadOnlyList<QueryLogEntry>>(rows);
        }
    }

    private static (HistoryPanelViewModel Vm, ConnectionStore Store) WithConnections(params QueryLogEntry[] entries)
    {
        var store = new ConnectionStore();
        store.Entries.AddRange(entries);
        var vm = new HistoryPanelViewModel(
            store.Search,
            name => name == "prod" ? "#E46876" : null,
            connections: () => [new HistoryConnection("prod", ProdId), new HistoryConnection("local", LocalId)]);
        return (vm, store);
    }

    [Fact]
    public async Task Grouping_by_connection_puts_the_most_recently_used_first_and_its_rows_newest_first()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var (vm, _) = WithConnections(
            Entry(now.AddDays(-3), ok: true, conn: "prod", sql: "select old_prod"),
            Entry(now, ok: true, conn: "local", sql: "select new_local"),
            Entry(now.AddDays(-1), ok: true, conn: "prod", sql: "select new_prod"));
        await vm.ReloadAsync(CancellationToken.None);

        vm.Grouping = HistoryGrouping.Connection;

        Assert.Equal(["local", "prod"], vm.Groups.Select(g => g.Caption));
        Assert.Equal(["select new_prod", "select old_prod"], vm.Groups[1].Rows.Select(r => r.Sql));
        Assert.Equal("#E46876", vm.Groups[1].Color);
        Assert.False(vm.Groups[0].HasColor);   // no colour known for it: no dot, rather than an empty one
        // No day caption above these rows, so each says its own date.
        var row = vm.Groups[1].Rows[0];
        Assert.Equal(row.Entry.ExecutedAt.LocalDateTime.ToString("dd.MM HH:mm"), row.Time);
    }

    [Fact]
    public async Task Grouping_by_day_is_the_default_and_its_rows_show_the_time_alone()
    {
        var now = DateTimeOffset.Now;
        var (vm, _) = WithConnections(Entry(now, ok: true, conn: "prod"));

        await vm.ReloadAsync(CancellationToken.None);

        Assert.Equal(HistoryGrouping.Day, vm.Grouping);
        Assert.Equal("TODAY", Assert.Single(vm.Groups).Caption);
        Assert.Null(vm.Groups[0].Color);
        Assert.Equal(now.LocalDateTime.ToString("HH:mm"), vm.Groups[0].Rows[0].Time);
    }

    [Fact]
    public async Task A_connection_renamed_since_keeps_one_group_under_its_current_name()
    {
        var now = DateTimeOffset.Now;
        var (vm, _) = WithConnections(
            Entry(now, ok: true, conn: "prod"),
            Entry(now.AddHours(-1), ok: true, conn: "production (old name)") with { ConnectionId = ProdId });
        await vm.ReloadAsync(CancellationToken.None);

        vm.Grouping = HistoryGrouping.Connection;

        var group = Assert.Single(vm.Groups);
        Assert.Equal("prod", group.Caption);
        Assert.Equal(2, group.Rows.Count);
    }

    [Fact]
    public async Task The_pills_still_narrow_a_list_grouped_by_connection()
    {
        var now = DateTimeOffset.Now;
        var (vm, _) = WithConnections(
            Entry(now, ok: true, conn: "prod"),
            Entry(now.AddMinutes(-1), ok: false, conn: "local"));
        await vm.ReloadAsync(CancellationToken.None);
        vm.Grouping = HistoryGrouping.Connection;

        vm.Filter = HistoryFilter.Error;

        Assert.Equal("local", Assert.Single(vm.Groups).Caption);
    }

    [Fact]
    public async Task Changing_the_grouping_keeps_the_selected_row()
    {
        var now = DateTimeOffset.Now;
        var (vm, _) = WithConnections(
            Entry(now, ok: true, conn: "prod", sql: "select a") with { Id = 1 },
            Entry(now.AddDays(-1), ok: true, conn: "local", sql: "select b") with { Id = 2 });
        await vm.ReloadAsync(CancellationToken.None);
        vm.SelectedRow = vm.Groups[1].Rows[0];

        vm.Grouping = HistoryGrouping.Connection;

        Assert.Equal("select b", vm.SelectedRow?.Sql);
        Assert.Contains(vm.SelectedRow, vm.Groups.SelectMany(g => g.Rows));
    }

    [Fact]
    public async Task The_connection_filter_offers_all_then_the_projects_connections_then_other_logged_names()
    {
        var now = DateTimeOffset.Now;
        var (vm, _) = WithConnections(
            Entry(now, ok: true, conn: "prod"),
            Entry(now, ok: true, conn: "deleted since"));

        await vm.ReloadAsync(CancellationToken.None);

        Assert.Equal(["All connections", "local", "prod", "deleted since"], vm.ConnectionOptions.Select(o => o.Label));
        Assert.Same(HistoryConnectionOption.All, vm.SelectedConnection);
        Assert.Equal(new HistoryConnection("prod", ProdId), vm.ConnectionOptions[2].Connection);
        Assert.Null(vm.ConnectionOptions[3].Connection!.Id);   // known by its recorded name only

        // A reload that changes nothing leaves the list alone: no change events for the bound dropdown, whose
        // selection a rebuilt list would disturb.
        var changes = 0;
        vm.ConnectionOptions.CollectionChanged += (_, _) => changes++;
        await vm.ReloadAsync(CancellationToken.None);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task Picking_a_connection_asks_the_log_for_it_rather_than_filtering_the_rows_loaded()
    {
        var now = DateTimeOffset.Now;
        var (vm, store) = WithConnections(
            Entry(now, ok: true, conn: "prod", sql: "select on_prod"),
            Entry(now, ok: true, conn: "local", sql: "select on_local"));
        await vm.ReloadAsync(CancellationToken.None);

        vm.SelectedConnection = vm.ConnectionOptions.Single(o => o.Label == "prod");

        Assert.Equal(new HistoryConnection("prod", ProdId), store.Asked[^1]);
        Assert.Equal(["select on_prod"], vm.Groups.SelectMany(g => g.Rows).Select(r => r.Sql));
        Assert.Equal("1 query.", vm.Status);

        vm.SelectedConnection = HistoryConnectionOption.All;

        Assert.Null(store.Asked[^1]);
        Assert.Equal(2, vm.Groups.SelectMany(g => g.Rows).Count());
    }

    [Fact]
    public async Task The_options_survive_a_filter_that_leaves_one_connections_rows_loaded()
    {
        // Once "prod" is picked the loaded rows name nothing else, and the list must not shrink to match:
        // the other names are how the user gets back out.
        var now = DateTimeOffset.Now;
        var (vm, _) = WithConnections(
            Entry(now, ok: true, conn: "prod"),
            Entry(now, ok: true, conn: "deleted since"));
        await vm.ReloadAsync(CancellationToken.None);
        var picked = vm.ConnectionOptions.Single(o => o.Label == "deleted since");

        vm.SelectedConnection = picked;
        vm.SelectedConnection = vm.ConnectionOptions.Single(o => o.Label == "prod");

        Assert.Equal(["All connections", "local", "prod", "deleted since"], vm.ConnectionOptions.Select(o => o.Label));
        Assert.Same(picked, vm.ConnectionOptions[3]);   // patched in place: the same options, never rebuilt
    }

    [Fact]
    public async Task A_dropdown_writing_null_falls_back_to_all_connections()
    {
        var (vm, store) = WithConnections(Entry(DateTimeOffset.Now, ok: true, conn: "prod"));
        await vm.ReloadAsync(CancellationToken.None);
        vm.SelectedConnection = vm.ConnectionOptions.Single(o => o.Label == "prod");

        vm.SelectedConnection = null;

        Assert.Same(HistoryConnectionOption.All, vm.SelectedConnection);
        Assert.Null(store.Asked[^1]);
    }

    [Fact]
    public async Task An_answer_for_the_connection_just_left_does_not_land()
    {
        var now = DateTimeOffset.Now;
        var prodRows = new[] { Entry(now, ok: true, conn: "prod", sql: "select on_prod") };
        var all = new[] { prodRows[0], Entry(now, ok: true, conn: "local", sql: "select on_local") };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new HistoryPanelViewModel(
            async (search, _) =>
            {
                if (search.Connection is null) return all;
                await gate.Task;                                   // the slow search for one connection
                return prodRows;
            },
            _ => null,
            connections: () => [new HistoryConnection("prod", ProdId)]);
        await vm.ReloadAsync(CancellationToken.None);
        vm.SelectedConnection = vm.ConnectionOptions.Single(o => o.Label == "prod");
        var slow = vm.ReloadAsync(CancellationToken.None);         // the shell's refresh, on its own token

        vm.SelectedConnection = HistoryConnectionOption.All;
        gate.SetResult();
        await slow;

        Assert.Equal(2, vm.Groups.SelectMany(g => g.Rows).Count());
    }
}
