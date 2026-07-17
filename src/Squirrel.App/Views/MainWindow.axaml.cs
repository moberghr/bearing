using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.TextMate;
using Squirrel.App.ViewModels;
using Squirrel.Core.Data;
using TextMateSharp.Grammars;

namespace Squirrel.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        InstallSqlHighlighting();

        DataContextChanged += (_, _) => SyncEditorFromViewModel();
        Loaded += (_, _) => SyncEditorFromViewModel();
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void InstallSqlHighlighting()
    {
        var options = new RegistryOptions(ThemeName.DarkPlus);
        var installation = Editor.InstallTextMate(options);
        var sql = options.GetLanguageByExtension(".sql");
        if (sql is not null)
            installation.SetGrammar(options.GetScopeByLanguageId(sql.Id));
    }

    private void SyncEditorFromViewModel()
    {
        if (Vm is null) return;
        if (string.IsNullOrEmpty(Editor.Text) && !string.IsNullOrEmpty(Vm.Sql))
            Editor.Text = Vm.Sql;

        Vm.PropertyChanged -= OnViewModelPropertyChanged;
        Vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.LastResult))
            RebuildResultsGrid(Vm?.LastResult);
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) await Vm.ConnectAsync();
    }

    private async void OnRunClick(object? sender, RoutedEventArgs e) => await RunAsync();

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            await RunAsync();
        }
    }

    private async System.Threading.Tasks.Task RunAsync()
    {
        if (Vm is null) return;
        // Run the selection if there is one, otherwise the whole buffer.
        var selected = Editor.SelectedText;
        var sql = string.IsNullOrWhiteSpace(selected) ? Editor.Text : selected;
        await Vm.ExecuteAsync(sql);
    }

    internal void RebuildResultsGrid(QueryResult? result)
    {
        ResultsGrid.Columns.Clear();
        if (result is null || !result.Success)
        {
            ResultsGrid.ItemsSource = null;
            return;
        }

        for (var i = 0; i < result.Columns.Count; i++)
        {
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = result.Columns[i].Name,
                Binding = new Binding($"[{i}]"),
            });
        }

        ResultsGrid.ItemsSource = result.Rows;
    }
}
