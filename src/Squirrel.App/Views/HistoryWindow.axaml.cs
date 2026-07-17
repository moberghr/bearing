using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.TextMate;
using Squirrel.Core.Logging;
using TextMateSharp.Grammars;

namespace Squirrel.App.Views;

public partial class HistoryWindow : Window
{
    private readonly Func<string?, CancellationToken, Task<IReadOnlyList<QueryLogEntry>>> _search;
    private readonly Action<string> _onPick;

    // Parameterless ctor for the XAML designer/loader.
    public HistoryWindow() : this((_, _) => Task.FromResult<IReadOnlyList<QueryLogEntry>>(Array.Empty<QueryLogEntry>()), _ => { }) { }

    public HistoryWindow(
        Func<string?, CancellationToken, Task<IReadOnlyList<QueryLogEntry>>> search,
        Action<string> onPick)
    {
        InitializeComponent();
        _search = search;
        _onPick = onPick;
        InstallSqlHighlighting();
        Loaded += async (_, _) => await RunSearch();
    }

    private void InstallSqlHighlighting()
    {
        var options = new RegistryOptions(ThemeName.DarkPlus);
        var installation = PreviewEditor.InstallTextMate(options);
        var sql = options.GetLanguageByExtension(".sql");
        if (sql is not null)
            installation.SetGrammar(options.GetScopeByLanguageId(sql.Id));
    }

    private async void OnSearchClick(object? sender, RoutedEventArgs e) => await RunSearch();

    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await RunSearch(); }
    }

    private async Task RunSearch()
    {
        var text = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text;
        try
        {
            var entries = await _search(text, CancellationToken.None);
            HistoryGrid.ItemsSource = entries.Select(HistoryRow.From).ToList();
            DetailsText.Text = entries.Count == 0 ? "No matching queries." : $"{entries.Count} result(s). Select one to preview.";
            PreviewEditor.Text = "";
            LoadButton.IsEnabled = false;
        }
        catch (Exception ex)
        {
            DetailsText.Text = "Search error: " + ex.Message;
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is HistoryRow row)
        {
            PreviewEditor.Text = row.Sql;
            DetailsText.Text = $"{row.ExecutedAt} · {row.ConnectionName} · {row.RowCount} row(s) · {row.DurationMs} ms · {row.Ok}"
                               + (string.IsNullOrEmpty(row.Error) ? "" : $"  ⚠ {row.Error}");
            LoadButton.IsEnabled = row.Sql.Length > 0;
        }
        else
        {
            PreviewEditor.Text = "";
            LoadButton.IsEnabled = false;
        }
    }

    private void OnLoadClick(object? sender, RoutedEventArgs e) => LoadSelected();
    private void OnRowActivated(object? sender, TappedEventArgs e) => LoadSelected();

    private void LoadSelected()
    {
        if (HistoryGrid.SelectedItem is HistoryRow { Sql.Length: > 0 } row)
        {
            _onPick(row.Sql);
            Close();
        }
    }

    /// <summary>Flat, display-ready projection of a log entry for the grid + preview.</summary>
    public sealed record HistoryRow(string ExecutedAt, string ConnectionName, long RowCount,
        long DurationMs, string Ok, string Preview, string Sql, string? Error)
    {
        public static HistoryRow From(QueryLogEntry e) => new(
            e.ExecutedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            e.ConnectionName,
            e.RowCount,
            (long)e.Duration.TotalMilliseconds,
            e.Success ? "✓" : "✗",
            OneLine(e.SqlText),
            e.SqlText,
            e.ErrorMessage);

        private static string OneLine(string sql)
        {
            var flat = string.Join(' ', sql.Split('\n', '\r').Select(s => s.Trim()).Where(s => s.Length > 0));
            return flat.Length > 200 ? flat[..200] + "…" : flat;
        }
    }
}
