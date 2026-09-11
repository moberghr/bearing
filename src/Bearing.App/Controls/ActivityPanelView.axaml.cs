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
    private bool _highlighted;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_watched is not null) _watched.PropertyChanged -= OnVmPropertyChanged;
        _watched = Vm;
        if (_watched is not null) _watched.PropertyChanged += OnVmPropertyChanged;
        ShowSelectedSql();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActivityPanelViewModel.Selected)) ShowSelectedSql();
    }

    /// <summary>
    /// Put the selected backend's statement in the viewer, formatted and highlighted.
    /// <para>
    /// Formatted because what <c>pg_stat_activity</c> returns is whatever the client sent — one long line
    /// from an ORM, or a script's own indentation — and the question being asked of it is "what is this, and
    /// do I stop it". Asynchronously, because the formatter parses: a large statement takes long enough that
    /// doing it inline would stutter the panel's own refresh. The raw text goes up first so the pane is never
    /// blank, and a statement the formatter refuses simply stays as the server sent it.
    /// </para>
    /// </summary>
    private void ShowSelectedSql()
    {
        var sql = Vm?.Selected?.Backend.Query ?? "";
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
