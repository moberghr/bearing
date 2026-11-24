using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Bearing.Core.Logging;
using Bearing.Sql;

namespace Bearing.App.ViewModels;

/// <summary>Which history rows the filter pills show.</summary>
public enum HistoryFilter
{
    All,
    Ok,
    Error,

    /// <summary>Bookmarked rows only (#111). Asked of the store rather than filtered from the cached
    /// screenful, so a query starred months ago is found.</summary>
    Starred,
}

/// <summary>
/// The inline History side-panel (design §4): a day-grouped, filterable view over the query log.
/// Search hits the FTS log (<see cref="ReloadAsync"/>); the ok/error pills re-group the cached rows
/// with no round-trip. Each row carries the connection's environment color for its leading dot.
/// </summary>
public sealed partial class HistoryPanelViewModel : ObservableObject
{
    private readonly Func<HistorySearch, CancellationToken, Task<IReadOnlyList<QueryLogEntry>>> _search;
    private readonly Func<string, string?> _colorForConnection;
    private readonly Func<long, bool, CancellationToken, Task>? _setStarred;
    private readonly Func<IReadOnlyList<HistoryConnection>> _connections;
    // Recorded names met in the log that no connection of the project answers to (deleted or renamed since).
    // Remembered for the session: once a connection is picked, the loaded rows hold that one name only.
    private readonly HashSet<string> _otherNames = new(StringComparer.Ordinal);
    private IReadOnlyList<QueryLogEntry> _entries = Array.Empty<QueryLogEntry>();
    private CancellationTokenSource? _filterReload;

    /// <param name="search">The log search — see <see cref="HistorySearch"/>.</param>
    /// <param name="setStarred">Stars or unstars a logged row by id (#111); null leaves the star inert.</param>
    /// <param name="connections">The project's connections as they are now, for the connection filter and for
    /// naming a group after a connection renamed since its rows were logged. Null: none.</param>
    public HistoryPanelViewModel(
        Func<HistorySearch, CancellationToken, Task<IReadOnlyList<QueryLogEntry>>> search,
        Func<string, string?> colorForConnection,
        Func<long, bool, CancellationToken, Task>? setStarred = null,
        Func<IReadOnlyList<HistoryConnection>>? connections = null)
    {
        _search = search;
        _colorForConnection = colorForConnection;
        _setStarred = setStarred;
        _connections = connections ?? (() => Array.Empty<HistoryConnection>());
    }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private HistoryFilter _filter = HistoryFilter.All;
    [ObservableProperty] private HistoryGrouping _grouping = HistoryGrouping.Day;

    /// <summary>The connection the list is narrowed to; <see cref="HistoryConnectionOption.All"/> for none.
    /// Neither this nor <see cref="Grouping"/> is saved: both start over with the app.</summary>
    [ObservableProperty] private HistoryConnectionOption? _selectedConnection = HistoryConnectionOption.All;

    /// <summary>What the connection filter offers: all, the project's connections, then any other name the
    /// loaded rows were logged under. Patched in place, never cleared — a cleared list makes the bound
    /// dropdown write its own lost selection back.</summary>
    public ObservableCollection<HistoryConnectionOption> ConnectionOptions { get; } = [HistoryConnectionOption.All];
    [ObservableProperty] private HistoryRowViewModel? _selectedRow;
    [ObservableProperty] private string _status = "";

    /// <summary>The filtered rows under their captions: a day each (newest first), or a connection each
    /// (<see cref="Grouping"/>).</summary>
    public ObservableCollection<HistoryGroup> Groups { get; } = new();

    /// <summary>The ok/error pills re-group the cached rows; entering or leaving <see cref="HistoryFilter.Starred"/>
    /// changes what the store is asked for, so that one reloads.</summary>
    partial void OnFilterChanged(HistoryFilter oldValue, HistoryFilter newValue)
    {
        if (oldValue != HistoryFilter.Starred && newValue != HistoryFilter.Starred)
        {
            Regroup(DateTimeOffset.Now);
            return;
        }
        StartOwnReload();
    }

    /// <summary>Asked of the store rather than of the rows already loaded, as the Starred pill is: the panel
    /// holds the newest screenful, and a quiet connection may have nothing in it.</summary>
    partial void OnSelectedConnectionChanged(HistoryConnectionOption? value)
    {
        // A dropdown can write null while its items move; that is not the user choosing anything.
        if (value is null)
        {
            SelectedConnection = HistoryConnectionOption.All;
            return;
        }
        // The dropdown is in the middle of committing this selection, and a search that answers at once
        // regroups before it has finished: the options must not move under it (it then reads an index the
        // list no longer has). The next reload brings them up to date.
        _pickingConnection = true;
        try { StartOwnReload(); }
        finally { _pickingConnection = false; }
    }

    private bool _pickingConnection;

    partial void OnGroupingChanged(HistoryGrouping value) => Regroup(DateTimeOffset.Now);

    private void StartOwnReload()
    {
        var superseded = _filterReload;
        _filterReload = new CancellationTokenSource();
        superseded?.Cancel();
        superseded?.Dispose();
        _ = ReloadAsync(_filterReload.Token);
    }

    /// <summary>
    /// Star or unstar a row (#111). Drawn at once and written behind; a write that fails puts the star back and
    /// says so, rather than leaving a bookmark on screen that the log doesn't hold — the one place a wrong
    /// star costs something is retention, which would then prune a row the user thought was kept.
    /// </summary>
    public async Task ToggleStarAsync(HistoryRowViewModel row)
    {
        if (_setStarred is null) return;
        if (row.Entry.Id == 0)
        {
            Status = "That query isn't in the log yet — try again in a moment.";
            return;
        }
        var starred = !row.IsStarred;
        Apply(row, starred);
        try
        {
            await _setStarred(row.Entry.Id, starred, CancellationToken.None);
            Status = starred ? "Starred — kept past history retention." : "Unstarred.";
        }
        catch (Exception ex)
        {
            Apply(row, !starred);
            Status = "Couldn't " + (starred ? "star" : "unstar") + " that query: " + ex.Message;
            return;
        }
        if (Filter == HistoryFilter.Starred && !starred) Regroup(DateTimeOffset.Now);
    }

    private void Apply(HistoryRowViewModel row, bool starred)
    {
        row.IsStarred = starred;
        _entries = _entries.Select(e => e.Id == row.Entry.Id ? e with { IsStarred = starred } : e).ToList();
    }

    /// <summary>Fetch matching history from the log and rebuild the grouped view.</summary>
    public async Task ReloadAsync(CancellationToken ct)
    {
        var text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText;
        var starredOnly = Filter == HistoryFilter.Starred;
        var connection = SelectedConnection?.Connection;
        try
        {
            var found = await _search(new HistorySearch(text, starredOnly, connection), ct);
            // A superseded reload must not land: the caller cancels the previous one for each new query in
            // a run, and applying a stale answer after a newer one would leave the panel behind the log.
            ct.ThrowIfCancellationRequested();
            // The shell's refresh and the filter's own reload hold separate tokens, so neither cancels the
            // other. An answer asked under the other side of the Starred pill is for a view no longer shown —
            // starred-only rows under "All", or the newest screenful under "Starred" — and the filter change
            // has started a reload of its own.
            if ((Filter == HistoryFilter.Starred) != starredOnly) return;
            // The same for the connection filter: an answer for the connection just left.
            if (!Equals(SelectedConnection?.Connection, connection)) return;
            _entries = found;
            Regroup(DateTimeOffset.Now);
            Status = _entries.Count == 0
                ? Filter == HistoryFilter.Starred ? "No starred queries — ☆ a row to keep it." : "No matching queries."
                : $"{_entries.Count} quer{(_entries.Count == 1 ? "y" : "ies")}.";
        }
        catch (OperationCanceledException)
        {
            // Superseded, not failed — the newer reload owns the panel and will set the status.
        }
        catch (Exception ex)
        {
            Status = "History error: " + ex.Message;
        }
    }

    /// <summary>Re-filter and re-group the cached entries. Public + <paramref name="now"/>-injected so
    /// the day bucketing (TODAY/YESTERDAY/date) is unit-testable.</summary>
    public void Regroup(DateTimeOffset now)
    {
        RefreshConnectionOptions();
        var rows = Grouping == HistoryGrouping.Connection ? ByConnection() : ByDay(now);

        var selected = SelectedRow?.Entry;

        Groups.Clear();
        foreach (var g in rows) Groups.Add(g);

        // Row objects are rebuilt wholesale, so a selection made before this call points at a row that is in
        // no list. It is re-found by the entry behind it, not dropped: since #78 this runs every time a query
        // lands in the log, so dropping it would clear the user's selection — and collapse the preview it
        // drives — under them, every time a query finished. When the entry really is gone (a filter switch,
        // a search that no longer matches it) there is nothing to re-find and the preview does close, which
        // is the behaviour this replaced and still the right one there.
        SelectedRow = selected is null
            ? null
            : Groups.SelectMany(g => g.Rows).FirstOrDefault(r => IsSameEntry(r.Entry, selected));
    }

    private List<HistoryGroup> ByDay(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        return _entries
            .Where(Matches)
            .Select(e => (entry: e, day: DateOnly.FromDateTime(e.ExecutedAt.LocalDateTime)))
            .GroupBy(x => x.day)
            .OrderByDescending(g => g.Key)
            .Select(g => new HistoryGroup(
                DayCaption(g.Key, today),
                g.Select(x => new HistoryRowViewModel(x.entry, _colorForConnection(x.entry.ConnectionName))).ToList()))
            .ToList();
    }

    /// <summary>
    /// One group per connection, the one used most recently first, its rows newest first. A row belongs to
    /// the project connection it was logged against — by id, so a connection renamed since keeps one group
    /// under its current name — and otherwise to the name it was logged under. Rows carry their date here:
    /// there is no day caption above them. Built from the rows loaded, so a connection with nothing in the
    /// newest screenful has no group until it is picked in the filter.
    /// </summary>
    private List<HistoryGroup> ByConnection()
    {
        var known = _connections();
        return _entries
            .Where(Matches)
            .Select(e => (entry: e, name: Resolve(known, e)?.Name ?? e.ConnectionName))
            .GroupBy(x => x.name, StringComparer.Ordinal)
            .OrderByDescending(g => g.Max(x => x.entry.ExecutedAt))
            .Select(g => new HistoryGroup(
                g.Key,
                g.OrderByDescending(x => x.entry.ExecutedAt)
                    .Select(x => new HistoryRowViewModel(
                        x.entry, _colorForConnection(x.entry.ConnectionName), withDate: true))
                    .ToList(),
                _colorForConnection(g.Key)))
            .ToList();
    }

    /// <summary>The project connection a logged row ran against: by id where the row has one that still
    /// exists, else by the name it was logged under. Null when the project has no such connection.</summary>
    private static HistoryConnection? Resolve(IReadOnlyList<HistoryConnection> known, QueryLogEntry entry)
        => (entry.ConnectionId is { } id ? known.FirstOrDefault(c => c.Id == id) : null)
           ?? known.FirstOrDefault(c => string.Equals(c.Name, entry.ConnectionName, StringComparison.Ordinal));

    /// <summary>Bring <see cref="ConnectionOptions"/> up to date with the project and the rows loaded. The
    /// selected option always stays in the list, even when its connection has since gone.</summary>
    private void RefreshConnectionOptions()
    {
        if (_pickingConnection) return;
        var known = _connections();
        foreach (var entry in _entries)
        {
            if (Resolve(known, entry) is null) _otherNames.Add(entry.ConnectionName);
        }
        _otherNames.RemoveWhere(name => known.Any(c => string.Equals(c.Name, name, StringComparison.Ordinal)));

        var desired = new List<HistoryConnectionOption> { HistoryConnectionOption.All };
        desired.AddRange(known
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new HistoryConnectionOption(c.Name, c)));
        desired.AddRange(_otherNames
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new HistoryConnectionOption(name, new HistoryConnection(name))));
        if (SelectedConnection is { } selected && !desired.Contains(selected)) desired.Add(selected);
        // The sync goes by reference, and these are rebuilt each time: hand it the option already listed
        // wherever one is equal, so a list that has not changed is not touched at all.
        var listed = desired.Select(d => ConnectionOptions.FirstOrDefault(o => o.Equals(d)) ?? d).ToList();
        TabViewSync.Apply(ConnectionOptions, listed);
    }

    /// <summary>Whether two projections are of the same logged row. By <see cref="QueryLogEntry.Id"/> where
    /// the log assigned one; a not-yet-persisted entry has Id 0, so those fall back to the fields that
    /// identify a single execution.</summary>
    private static bool IsSameEntry(QueryLogEntry a, QueryLogEntry b)
        => a.Id != 0 || b.Id != 0
            ? a.Id == b.Id
            : a.ExecutedAt == b.ExecutedAt && a.SqlText == b.SqlText;

    private bool Matches(QueryLogEntry e) => Filter switch
    {
        HistoryFilter.Ok => e.Success,
        HistoryFilter.Error => !e.Success,
        HistoryFilter.Starred => e.IsStarred,
        _ => true,
    };

    /// <summary>Relative day label: TODAY / YESTERDAY / dd.MM.yyyy.</summary>
    public static string DayCaption(DateOnly day, DateOnly today)
    {
        if (day == today) return "TODAY";
        if (day == today.AddDays(-1)) return "YESTERDAY";
        return day.ToString("dd.MM.yyyy");
    }
}

/// <summary>History rows under a caption — a day's worth, or a connection's, whose environment colour is
/// then <paramref name="Color"/> (null for a day, and for a connection the project no longer has).</summary>
public sealed record HistoryGroup(string Caption, IReadOnlyList<HistoryRowViewModel> Rows, string? Color = null)
{
    public bool HasColor => Color is not null;
}

/// <summary>How the history list is captioned.</summary>
public enum HistoryGrouping
{
    Day,
    Connection,
}

/// <summary>A connection history can be narrowed to. <paramref name="Id"/> is null for one known only by
/// the name its rows were logged under.</summary>
public sealed record HistoryConnection(string Name, Guid? Id = null);

/// <summary>An entry of the connection filter; <see cref="All"/> has no connection.</summary>
public sealed record HistoryConnectionOption(string Label, HistoryConnection? Connection)
{
    public static HistoryConnectionOption All { get; } = new("All connections", null);

    public override string ToString() => Label;
}

/// <summary>What the panel asks the log for: free text (null for none), starred rows only, and the
/// connection to narrow to (null for all).</summary>
public sealed record HistorySearch(string? Text, bool StarredOnly, HistoryConnection? Connection);

/// <summary>Display projection of one logged query for the history panel.</summary>
public sealed partial class HistoryRowViewModel : ObservableObject
{
    /// <param name="withDate">Show the date beside the time — for a list with no day caption above the row.</param>
    public HistoryRowViewModel(QueryLogEntry entry, string? connectionColor, bool withDate = false)
    {
        Entry = entry;
        _isStarred = entry.IsStarred;
        Sql = entry.SqlText;
        OpenText = ScriptDirectives.Render(entry.Parameters.Select(p => (p.Name, p.Value, p.Kind))) + entry.SqlText;
        ParametersText = string.Join(" · ", entry.Parameters.Select(p =>
            $"{p.Name} = {ScriptDirectives.FormatValue(p.Value, p.Kind) ?? "(multi-line text)"}"));
        ConnectionColor = connectionColor;
        IsError = !entry.Success;
        Time = entry.ExecutedAt.LocalDateTime.ToString(withDate ? "dd.MM HH:mm" : "HH:mm");
        Query = OneLine(entry.SqlText);
        Detail = $"{entry.ExecutedAt.LocalDateTime:dd.MM.yyyy HH:mm:ss} · {entry.ConnectionName} · {entry.RowCount} row(s) · {(long)entry.Duration.TotalMilliseconds} ms"
                 + (entry.Origin is null ? "" : $" · {entry.Origin}")
                 + (entry.Success ? "" : $"  ⚠ {entry.ErrorMessage}");
    }

    /// <summary>
    /// Which host ran this, or null for Bearing itself — the row-level answer to "did I run this?".
    /// <para>
    /// Marked in the list rather than only in the detail line, because the question it answers is one you
    /// scan for rather than look up: a history where an agent's statements are indistinguishable from your
    /// own is the gap §1.11's record exists to close. Null is the overwhelming majority, so the mark is on
    /// the exception.
    /// </para>
    /// </summary>
    public string? Origin => Entry.Origin;

    public bool IsExternal => Entry.Origin is not null;

    /// <summary>The logged row behind this projection. Kept so a rebuild can find the row the user had
    /// selected — the projections themselves are new objects every time (see <c>Regroup</c>).</summary>
    public QueryLogEntry Entry { get; }

    public string Sql { get; }

    /// <summary>
    /// What opening this row puts in a new tab, and what the preview shows (#103): the SQL as it was written,
    /// placeholders kept, with a <c>@set</c> line per value it ran with above it — so running it again needs
    /// no prompt, and changing a value is editing a line. Just the SQL when it had no values, or when the log
    /// dropped them because literals are redacted (that run then asks again).
    /// </summary>
    public string OpenText { get; }

    /// <summary>The values beside the query in the list — <c>id = 42 · since = '2005-06-01'</c> — or empty.</summary>
    public string ParametersText { get; }

    public bool HasParameters => ParametersText.Length > 0;

    /// <summary>Bookmarked (#111). Observable because the star flips in place, ahead of the log write.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarGlyph), nameof(StarTip))]
    private bool _isStarred;

    public string StarGlyph => IsStarred ? "★" : "☆";

    public string StarTip => IsStarred
        ? "Starred: kept past history retention. Click to unstar."
        : "Star this query to keep it past history retention.";

    public string? ConnectionColor { get; }
    public bool IsError { get; }
    public string Time { get; }
    public string Query { get; }
    public string Detail { get; }

    /// <summary>
    /// Error rows are prefixed with a cross in the list, and a row something other than Bearing ran carries
    /// its origin. Both are text rather than colour, because the list is already colour-coded by connection
    /// and a second hue would be one too many to tell apart at a glance.
    /// </summary>
    public string DisplayQuery
        => (IsError ? "✕ " : "") + (IsExternal ? $"[{Origin}] " : "") + Query;

    /// <summary>Row text colour as a token key (via TokenBrush): error red, else primary text.</summary>
    public string QueryColorKey => IsError ? "Error.Red" : "Text.Primary";

    private static string OneLine(string sql)
    {
        var flat = string.Join(' ', sql.Split('\n', '\r').Select(s => s.Trim()).Where(s => s.Length > 0));
        return flat.Length > 120 ? flat[..120] + "…" : flat;
    }
}
