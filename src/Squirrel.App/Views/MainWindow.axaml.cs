using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AvaloniaEdit.TextMate;
using Squirrel.App.Completion;
using Squirrel.App.ViewModels;
using Squirrel.Core.Data;
using Squirrel.Sql;
using TextMateSharp.Grammars;

namespace Squirrel.App.Views;

public partial class MainWindow : Window
{
    private readonly CompletionController _completion;

    public MainWindow()
    {
        InitializeComponent();
        InstallSqlHighlighting();

        _completion = new CompletionController(Editor, new CompletionEngine(), () => Vm?.CurrentSnapshot);

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
        else if (e.PropertyName == nameof(MainWindowViewModel.Sql) && Vm is not null && Editor.Text != Vm.Sql)
            Editor.Text = Vm.Sql; // session restore populated the buffer
    }

    /// <summary>Current editor buffer + caret, for session save on close.</summary>
    internal string CurrentSql => Editor.Text;
    internal int CurrentCaret => Editor.CaretOffset;

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
        else if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _completion.TriggerExplicit();
        }
        else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            await SaveAsync();
        }
        else if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            await OpenAsync();
        }
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await OpenAsync();
    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await SaveAsync();

    private void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var history = new HistoryWindow(
            (text, ct) => Vm.SearchHistoryAsync(text, ct),
            sql => Editor.Text = sql);
        history.Show(this);
    }

    private async Task OpenAsync()
    {
        if (Vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SQL script",
            AllowMultiple = false,
            FileTypeFilter = new[] { SqlFileType },
            SuggestedStartLocation = await StartFolder(),
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await Vm.LoadScriptAsync(path);
    }

    private async Task SaveAsync()
    {
        if (Vm is null) return;

        if (Vm.CurrentScriptPath is { } existing)
        {
            await Vm.SaveScriptAsync(existing, Editor.Text);
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save SQL script",
            DefaultExtension = "sql",
            SuggestedFileName = "query.sql",
            FileTypeChoices = new[] { SqlFileType },
            SuggestedStartLocation = await StartFolder(),
        });
        if (file?.TryGetLocalPath() is { } path)
            await Vm.SaveScriptAsync(path, Editor.Text);
    }

    private async Task<IStorageFolder?> StartFolder()
        => Vm?.ScriptsDirectory is { } dir ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null;

    private static readonly FilePickerFileType SqlFileType = new("SQL scripts")
    {
        Patterns = new[] { "*.sql" },
    };

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
