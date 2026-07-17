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
    private bool _loadingEditor;          // guards editor<->tab sync while swapping tabs
    private bool _suppressProjectChange;   // guards the project combo during programmatic updates

    public MainWindow()
    {
        InitializeComponent();
        InstallSqlHighlighting();

        _completion = new CompletionController(Editor, new CompletionEngine(), () => Vm?.CurrentSnapshot);

        Editor.TextChanged += (_, _) =>
        {
            if (!_loadingEditor && Vm?.SelectedTab is { } tab) tab.Text = Editor.Text;
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (!_loadingEditor && Vm?.SelectedTab is { } tab) tab.CaretOffset = Editor.CaretOffset;
        };

        DataContextChanged += (_, _) => HookViewModel();
        Loaded += (_, _) => HookViewModel();
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void HookViewModel()
    {
        if (Vm is null) return;
        Vm.PropertyChanged -= OnViewModelPropertyChanged;
        Vm.PropertyChanged += OnViewModelPropertyChanged;
        LoadEditorFromSelectedTab();
        SyncProjectCombo();
    }

    private void InstallSqlHighlighting()
    {
        var options = new RegistryOptions(ThemeName.DarkPlus);
        var installation = Editor.InstallTextMate(options);
        var sql = options.GetLanguageByExtension(".sql");
        if (sql is not null)
            installation.SetGrammar(options.GetScopeByLanguageId(sql.Id));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedTab))
            LoadEditorFromSelectedTab();
        else if (e.PropertyName == nameof(MainWindowViewModel.Title))
            SyncProjectCombo();
    }

    private void LoadEditorFromSelectedTab()
    {
        var tab = Vm?.SelectedTab;
        _loadingEditor = true;
        Editor.Text = tab?.Text ?? "";
        if (tab is not null)
            Editor.CaretOffset = System.Math.Clamp(tab.CaretOffset, 0, Editor.Text.Length);
        _loadingEditor = false;
        RebuildResultsGrid(tab?.LastResult);
    }

    private void SyncProjectCombo()
    {
        if (Vm?.ProjectDirectory is not { } dir) return;
        _suppressProjectChange = true;
        ProjectCombo.SelectedItem = dir;
        _suppressProjectChange = false;
    }

    // ---- tabs ----

    private void OnNewTabClick(object? sender, RoutedEventArgs e) => Vm?.NewTab();

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: EditorTabViewModel tab }) Vm?.CloseTab(tab);
    }

    // ---- projects ----

    private async void OnProjectSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressProjectChange || Vm is null) return;
        if (ProjectCombo.SelectedItem is string dir && dir != Vm.ProjectDirectory)
            await Vm.OpenProjectAsync(dir);
    }

    private async void OnOpenProjectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Squirrel project folder",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            await Vm.OpenProjectAsync(path);
    }

    private async void OnNewProjectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an empty folder for the new project",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            await Vm.NewProjectAsync(path, new System.IO.DirectoryInfo(path).Name);
    }

    // ---- connection / run / scripts / history ----

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) await Vm.ConnectAsync();
    }

    private async void OnRunClick(object? sender, RoutedEventArgs e) => await RunAsync();

    private void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var history = new HistoryWindow((text, ct) => Vm.SearchHistoryAsync(text, ct), sql => Editor.Text = sql);
        history.Show(this);
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.F5) { e.Handled = true; await RunAsync(); }
        else if (e.Key == Key.Space && ctrl) { e.Handled = true; _completion.TriggerExplicit(); }
        else if (e.Key == Key.S && ctrl) { e.Handled = true; await SaveAsync(); }
        else if (e.Key == Key.O && ctrl) { e.Handled = true; await OpenAsync(); }
        else if (e.Key == Key.T && ctrl) { e.Handled = true; Vm?.NewTab(); }
        else if (e.Key == Key.W && ctrl && Vm?.SelectedTab is { } tab) { e.Handled = true; Vm.CloseTab(tab); }
    }

    private async Task RunAsync()
    {
        if (Vm is null) return;
        var selected = Editor.SelectedText;
        var sql = string.IsNullOrWhiteSpace(selected) ? Editor.Text : selected;
        await Vm.ExecuteAsync(sql);
        RebuildResultsGrid(Vm.SelectedTab?.LastResult);
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await OpenAsync();
    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await SaveAsync();

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
        {
            await Vm.LoadScriptIntoSelectedAsync(path);
            LoadEditorFromSelectedTab();
        }
    }

    private async Task SaveAsync()
    {
        if (Vm is null) return;
        if (Vm.SelectedTab?.ScriptPath is { } existing)
        {
            await Vm.SaveSelectedScriptAsync(existing, Editor.Text);
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
            await Vm.SaveSelectedScriptAsync(path, Editor.Text);
    }

    private async Task<IStorageFolder?> StartFolder()
        => Vm?.ScriptsDirectory is { } dir ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null;

    private static readonly FilePickerFileType SqlFileType = new("SQL scripts") { Patterns = new[] { "*.sql" } };

    /// <summary>Flush the live editor text/caret into the selected tab (called before close/save session).</summary>
    internal void FlushActiveEditor()
    {
        if (Vm?.SelectedTab is { } tab)
        {
            tab.Text = Editor.Text;
            tab.CaretOffset = Editor.CaretOffset;
        }
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
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = result.Columns[i].Name,
                Binding = new Binding($"[{i}]"),
            });

        ResultsGrid.ItemsSource = result.Rows;
    }
}
