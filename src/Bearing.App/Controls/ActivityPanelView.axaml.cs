using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Bearing.App.Editing;
using Bearing.App.Services;
using Bearing.Sql;
using Bearing.App.ViewModels;

namespace Bearing.App.Controls;

/// <summary>
/// The server activity panel's view (#101). Wiring only: the two row actions and the refresh button forward
/// to the view model, which owns every decision and every word (§2.3).
/// </summary>
public partial class ActivityPanelView : UserControl
{
    public ActivityPanelView()
    {
        // InitializeComponent, not AvaloniaXamlLoader.Load: only the generated one assigns the x:Name
        // fields, and the chrome below needs BackendSql to exist.
        InitializeComponent();
        // Cheap chrome now; the TextMate registry is only touched when a backend is first selected — it must
        // not be on the window's construction path, and a panel that is never opened must not pay for it.
        SqlViewer.ApplyChrome(BackendSql, wordWrap: true);
        DataContextChanged += OnDataContextChanged;
    }

    private ActivityPanelViewModel? Vm => DataContext as ActivityPanelViewModel;

    private ActivityPanelViewModel? _watched;

    /// <summary>
    /// The selected row itself, watched alongside the view model.
    /// <para>
    /// A poll that finds the same session keeps the row object and gives it the new reading
    /// (<c>ActivityPanelViewModel.Apply</c>), so <c>Selected</c> does not change when a backend moves on to
    /// another statement — the row does. Without this the pane would still be showing the previous statement
    /// while the list row beside it showed the new one.
    /// </para>
    /// </summary>
    private BackendRowViewModel? _watchedRow;

    private bool _highlighted;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_watched is not null) _watched.PropertyChanged -= OnVmPropertyChanged;
        _watched = Vm;
        if (_watched is not null) _watched.PropertyChanged += OnVmPropertyChanged;
        WatchRow();
        // Forced: a different view model's selection can carry the same pid and the same statement, and the
        // pane must show the new panel's row rather than keep the old one's text on a matching key.
        ShowSelectedSql(force: true);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActivityPanelViewModel.Selected)) return;
        WatchRow();
        ShowSelectedSql();
    }

    private void WatchRow()
    {
        if (_watchedRow is not null) _watchedRow.PropertyChanged -= OnRowPropertyChanged;
        _watchedRow = Vm?.Selected;
        if (_watchedRow is not null) _watchedRow.PropertyChanged += OnRowPropertyChanged;
    }

    /// <summary>The selected backend has been re-read. Only the statement can change what the pane holds, and
    /// <see cref="ShowSelectedSql"/> decides whether it actually did.</summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BackendRowViewModel.Query)) ShowSelectedSql();
    }

    /// <summary>
    /// What is currently in the viewer: which backend it came from, and the statement itself. Null when the
    /// pane is empty. See <see cref="ShowSelectedSql"/> for why it is remembered.
    /// </summary>
    private (int Pid, DateTime? Since, string? Sql)? _shown;

    /// <summary>
    /// Put the selected backend's statement in the viewer, formatted and highlighted.
    /// <para>
    /// Formatted because what <c>pg_stat_activity</c> returns is whatever the client sent — one long line
    /// from an ORM, or a script's own indentation — and the question being asked of it is "what is this, and
    /// do I stop it". Asynchronously, because the formatter parses: a large statement takes long enough that
    /// doing it inline would stutter the panel's own refresh. The raw text goes up first so the pane is never
    /// blank, and a statement the formatter refuses simply stays as the server sent it.
    /// </para>
    /// <para>
    /// <b>Nothing is rewritten while the same statement is selected.</b> The panel rebuilds every row on each
    /// poll, so <c>Selected</c> is a new object every 2.5 seconds and this runs again — and assigning
    /// <c>Text</c> resets the caret, the selection and the scroll offset. The user reading a long statement,
    /// or part-way through selecting a clause out of it, lost it twice a second-and-a-half. The key is the
    /// backend's identity (a pid alone is reused) plus the statement, so a row that starts running something
    /// else still refreshes.
    /// </para>
    /// </summary>
    private void ShowSelectedSql(bool force = false)
    {
        var selected = Vm?.Selected;
        var key = selected is null
            ? ((int, DateTime?, string?)?)null
            : (selected.Pid, selected.Backend.BackendStart?.UtcDateTime, selected.Backend.Query);
        if (!force && _shown == key) return;
        _shown = key;

        var sql = selected?.Backend.Query ?? "";
        BackendSql.Text = sql;
        if (sql.Length == 0) return;

        if (!_highlighted)
        {
            _highlighted = true;
            EditorChrome.InstallSqlHighlighting(BackendSql);
        }

        CrashReporter.Observe(FormatAsync(sql), "activity.format-sql");
    }

    private async Task FormatAsync(string sql)
    {
        var formatted = await SqlFormat.FormatAsync(sql);
        if (formatted.Refused) return;
        // The selection can have moved on while the parse was running; the pane must show the row that is
        // selected now, not the one that was.
        if (Vm?.Selected?.Backend.Query == sql) BackendSql.Text = formatted.Text;
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) CrashReporter.Observe(vm.RefreshAsync(), "activity.refresh");
    }

    /// <summary>
    /// The row a context-menu item was opened on. Read from the list's selection rather than the menu item's
    /// DataContext: the menu is declared on the ListBox, so its items inherit the panel's context and not the
    /// row's. Avalonia selects a row on right-click, so the selection is the row that was aimed at.
    /// </summary>
    private BackendRowViewModel? Row => Vm?.Selected;

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && Row is { } row) CrashReporter.Observe(vm.CancelBackendAsync(row), "activity.cancel");
    }

    private void OnTerminateClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && Row is { } row) CrashReporter.Observe(vm.TerminateBackendAsync(row), "activity.terminate");
    }
}
