using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Bearing.Core.Logging;

namespace Bearing.App.ViewModels;

/// <summary>Which history rows the filter pills show.</summary>
public enum HistoryFilter { All, Ok, Error }

/// <summary>
/// The inline History side-panel (design §4): a day-grouped, filterable view over the query log.
/// Search hits the FTS log (<see cref="ReloadAsync"/>); the ok/error pills re-group the cached rows
/// with no round-trip. Each row carries the connection's environment color for its leading dot.
/// </summary>
public sealed partial class HistoryPanelViewModel : ObservableObject
{
    private readonly Func<string?, CancellationToken, Task<IReadOnlyList<QueryLogEntry>>> _search;
    private readonly Func<string, string?> _colorForConnection;
    private IReadOnlyList<QueryLogEntry> _entries = Array.Empty<QueryLogEntry>();

    public HistoryPanelViewModel(
        Func<string?, CancellationToken, Task<IReadOnlyList<QueryLogEntry>>> search,
        Func<string, string?> colorForConnection)
    {
        _search = search;
        _colorForConnection = colorForConnection;
    }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private HistoryFilter _filter = HistoryFilter.All;
    [ObservableProperty] private HistoryRowViewModel? _selectedRow;
    [ObservableProperty] private string _status = "";

    /// <summary>Day-grouped, filtered rows (newest day first).</summary>
    public ObservableCollection<HistoryDayGroup> Groups { get; } = new();

    partial void OnFilterChanged(HistoryFilter value) => Regroup(DateTimeOffset.Now);

    /// <summary>Fetch matching history from the log and rebuild the grouped view.</summary>
    public async Task ReloadAsync(CancellationToken ct)
    {
        var text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText;
        try
        {
            _entries = await _search(text, ct);
            Regroup(DateTimeOffset.Now);
            Status = _entries.Count == 0 ? "No matching queries." : $"{_entries.Count} quer{(_entries.Count == 1 ? "y" : "ies")}.";
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
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var rows = _entries
            .Where(Matches)
            .Select(e => (entry: e, day: DateOnly.FromDateTime(e.ExecutedAt.LocalDateTime)))
            .GroupBy(x => x.day)
            .OrderByDescending(g => g.Key)
            .Select(g => new HistoryDayGroup(
                DayCaption(g.Key, today),
                g.Select(x => new HistoryRowViewModel(x.entry, _colorForConnection(x.entry.ConnectionName))).ToList()))
            .ToList();

        Groups.Clear();
        foreach (var g in rows) Groups.Add(g);

        // Row objects are rebuilt wholesale, so a selection made before this call now points at a row that
        // is in no list. Dropping it here is what collapses the preview after a reload or a filter switch;
        // the view no longer does it (the per-day lists are bound one-way precisely so they can't — #43),
        // and leaving it would keep a query on screen with nothing selected to explain where it came from.
        if (SelectedRow is not null && !Groups.Any(g => g.Rows.Contains(SelectedRow)))
            SelectedRow = null;
    }

    private bool Matches(QueryLogEntry e) => Filter switch
    {
        HistoryFilter.Ok => e.Success,
        HistoryFilter.Error => !e.Success,
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

/// <summary>A day's worth of history rows under a caption.</summary>
public sealed record HistoryDayGroup(string Caption, IReadOnlyList<HistoryRowViewModel> Rows);

/// <summary>Display projection of one logged query for the history panel.</summary>
public sealed class HistoryRowViewModel
{
    public HistoryRowViewModel(QueryLogEntry entry, string? connectionColor)
    {
        Sql = entry.SqlText;
        ConnectionColor = connectionColor;
        IsError = !entry.Success;
        Time = entry.ExecutedAt.LocalDateTime.ToString("HH:mm");
        Query = OneLine(entry.SqlText);
        Detail = $"{entry.ExecutedAt.LocalDateTime:dd.MM.yyyy HH:mm:ss} · {entry.ConnectionName} · {entry.RowCount} row(s) · {(long)entry.Duration.TotalMilliseconds} ms"
                 + (entry.Success ? "" : $"  ⚠ {entry.ErrorMessage}");
    }

    public string Sql { get; }
    public string? ConnectionColor { get; }
    public bool IsError { get; }
    public string Time { get; }
    public string Query { get; }
    public string Detail { get; }

    /// <summary>Error rows are prefixed with a cross in the list.</summary>
    public string DisplayQuery => IsError ? "✕ " + Query : Query;

    /// <summary>Row text color (via HexBrush): error red, else primary text.</summary>
    public string QueryColorHex => IsError ? "#D2555A" : "#D8DEE6";

    private static string OneLine(string sql)
    {
        var flat = string.Join(' ', sql.Split('\n', '\r').Select(s => s.Trim()).Where(s => s.Length > 0));
        return flat.Length > 120 ? flat[..120] + "…" : flat;
    }
}
