using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AvaloniaEdit.TextMate;
using Squirrel.App.Completion;
using Squirrel.App.Editing;
using Squirrel.App.ViewModels;
using Squirrel.Core.Data;
using Squirrel.Sql;
using TextMateSharp.Grammars;

namespace Squirrel.App.Views;

public partial class MainWindow : Window
{
    private readonly CompletionController _completion;
    private readonly StatementMargin _statementHighlight = new();
    private bool _loadingEditor;          // guards editor<->tab sync while swapping tabs
    private bool _suppressProjectChange;   // guards the project combo during programmatic updates

    public MainWindow()
    {
        InitializeComponent();
        InstallSqlHighlighting();

        _completion = new CompletionController(Editor, new CompletionEngine(), () => Vm?.SnapshotForSelectedTab());
        Editor.TextArea.LeftMargins.Add(_statementHighlight); // its own column, right of the line numbers

        Editor.TextChanged += (_, _) =>
        {
            if (!_loadingEditor && Vm?.SelectedTab is { } tab) tab.Text = Editor.Text;
            UpdateStatementHighlight();
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (!_loadingEditor && Vm?.SelectedTab is { } tab) tab.CaretOffset = Editor.CaretOffset;
            UpdateStatementHighlight();
        };
        Editor.TextArea.SelectionChanged += (_, _) => UpdateStatementHighlight();

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
        else if (e.PropertyName is nameof(MainWindowViewModel.Title) or nameof(MainWindowViewModel.ProjectDirectory))
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
        UpdateStatementHighlight();
    }

    /// <summary>Mark the statement Run will execute — the selection if any, else the statement at
    /// the caret — so the highlight always matches <see cref="RunAsync"/>.</summary>
    private void UpdateStatementHighlight()
    {
        if (!string.IsNullOrEmpty(Editor.SelectedText))
            _statementHighlight.SetSpan(-1, -1); // selection is its own indicator
        else if (Squirrel.Sql.StatementSplitter.StatementAt(Editor.Text, Editor.CaretOffset) is { } stmt)
            _statementHighlight.SetSpan(stmt.TrimmedStart, stmt.TrimmedEnd);
        else
            _statementHighlight.SetSpan(-1, -1);
    }

    /// <summary>Alt+Up / Alt+Down: move the caret to the previous / next runnable statement.</summary>
    private void MoveToAdjacentStatement(int direction)
    {
        var text = Editor.Text;
        var spans = Squirrel.Sql.StatementSplitter.Split(text)
            .Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
        if (spans.Count == 0) return;

        var current = Squirrel.Sql.StatementSplitter.StatementAt(text, Editor.CaretOffset);
        var idx = current is null ? 0 : spans.FindIndex(s => s.Start == current.Start);
        if (idx < 0) idx = 0;

        var target = System.Math.Clamp(idx + direction, 0, spans.Count - 1);
        Editor.CaretOffset = spans[target].TrimmedStart;
        Editor.TextArea.Caret.BringCaretToView();
    }

    private void SyncProjectCombo()
    {
        if (Vm?.ProjectDirectory is not { } dir) return;
        _suppressProjectChange = true;
        ProjectCombo.SelectedItem = Vm.RecentProjects.FirstOrDefault(r => r.Directory == dir);
        _suppressProjectChange = false;
    }

    // ---- tabs ----

    private void OnNewTabClick(object? sender, RoutedEventArgs e) => Vm?.NewTab();

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: EditorTabViewModel tab }) Vm?.CloseTab(tab);
    }

    private async void OnTabHeaderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: EditorTabViewModel tab }) await RenameTabAsync(tab);
    }

    private async Task RenameTabAsync(EditorTabViewModel tab)
    {
        if (Vm is null) return;
        var current = tab.IsScratch ? tab.DisplayName : tab.Header;
        var prompt = new TextPromptDialog(tab.IsScratch ? "Rename tab" : "Rename script file", current);
        var name = await prompt.ShowDialog<string?>(this);
        if (name is not null) await Vm.RenameTabAsync(tab, name);
    }

    // ---- side pane ----

    private void OnToggleSidePane(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.SidePaneOpen = !Vm.SidePaneOpen;
    }

    // ---- connections ----

    private async void OnAddConnectionClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dialog = new ConnectionDialog(null, null, (i, p, ct) => Vm.TestConnectionAsync(i, p, ct));
        var result = await dialog.ShowDialog<ConnectionDialogResult?>(this);
        if (result is { Delete: false }) await Vm.AddOrUpdateConnectionAsync(result.Connection, result.Password);
    }

    private async void OnEditConnectionClick(object? sender, RoutedEventArgs e) => await EditSelectedConnection();

    private async Task EditSelectedConnection()
    {
        if (Vm is null || ConnectionsList.SelectedItem is not ConnectionInfo existing) return;
        var password = await Vm.GetConnectionPasswordAsync(existing.Id);
        var dialog = new ConnectionDialog(existing, password, (i, p, ct) => Vm.TestConnectionAsync(i, p, ct));
        var result = await dialog.ShowDialog<ConnectionDialogResult?>(this);
        if (result is null) return;
        if (result.Delete) await Vm.DeleteConnectionAsync(existing.Id);
        else await Vm.AddOrUpdateConnectionAsync(result.Connection, result.Password);
    }

    private void OnSetTabConnectionClick(object? sender, RoutedEventArgs e) => AssignSelectedConnectionToTab();

    private void OnConnectionActivated(object? sender, TappedEventArgs e) => AssignSelectedConnectionToTab();

    private void AssignSelectedConnectionToTab()
    {
        if (Vm?.SelectedTab is { } tab && ConnectionsList.SelectedItem is ConnectionInfo conn)
            Vm.SetTabConnection(tab, conn.Id);
    }

    private async void OnDeleteConnectionClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && ConnectionsList.SelectedItem is ConnectionInfo conn)
            await Vm.DeleteConnectionAsync(conn.Id);
    }

    // ---- scripts ----

    private async void OnScriptActivated(object? sender, TappedEventArgs e) => await OpenSelectedScript();
    private async void OnOpenScriptClick(object? sender, RoutedEventArgs e) => await OpenSelectedScript();

    private async Task OpenSelectedScript()
    {
        if (Vm is not null && ScriptsList.SelectedItem is ScriptItem script)
        {
            await Vm.OpenScriptInNewTabAsync(script.FullPath);
            LoadEditorFromSelectedTab();
        }
    }

    private async void OnRenameScriptClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || ScriptsList.SelectedItem is not ScriptItem script) return;
        var prompt = new TextPromptDialog("Rename script file", script.Name);
        var name = await prompt.ShowDialog<string?>(this);
        if (name is not null) await Vm.RenameScriptAsync(script.FullPath, name);
    }

    // ---- projects ----

    private async void OnProjectSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressProjectChange || Vm is null) return;
        if (ProjectCombo.SelectedItem is RecentProjectItem item && item.Directory != Vm.ProjectDirectory)
            await Vm.OpenProjectAsync(item.Directory);
    }

    private async void OnRenameProjectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.CurrentProjectName is not { } current) return;
        var prompt = new TextPromptDialog("Project name", current);
        var name = await prompt.ShowDialog<string?>(this);
        if (name is not null) await Vm.RenameProjectAsync(name);
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
        {
            var prompt = new TextPromptDialog("Project name", new System.IO.DirectoryInfo(path).Name);
            var name = await prompt.ShowDialog<string?>(this);
            if (name is not null) await Vm.NewProjectAsync(path, name);
        }
    }

    // ---- run / open / save / history ----

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.IsBusy == true) { Vm.CancelExecution(); return; }
        await RunAsync();
    }

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
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (e.Key == Key.F5 || (e.Key == Key.Enter && ctrl)) { e.Handled = true; await RunAsync(); }
        else if (e.Key == Key.Escape && Vm?.IsBusy == true) { e.Handled = true; Vm.CancelExecution(); }
        else if (e.Key == Key.Up && alt) { e.Handled = true; MoveToAdjacentStatement(-1); }
        else if (e.Key == Key.Down && alt) { e.Handled = true; MoveToAdjacentStatement(+1); }
        else if (e.Key == Key.Space && ctrl) { e.Handled = true; _completion.TriggerExplicit(); }
        else if (e.Key == Key.S && ctrl) { e.Handled = true; await SaveAsync(); }
        else if (e.Key == Key.O && ctrl) { e.Handled = true; await OpenAsync(); }
        else if (e.Key == Key.T && ctrl) { e.Handled = true; Vm?.NewTab(); }
        else if (e.Key == Key.B && ctrl) { e.Handled = true; if (Vm is not null) Vm.SidePaneOpen = !Vm.SidePaneOpen; }
        else if (e.Key == Key.W && ctrl && Vm?.SelectedTab is { } tab) { e.Handled = true; Vm.CloseTab(tab); }
        else if (e.Key == Key.F2 && Vm?.SelectedTab is { } rt) { e.Handled = true; await RenameTabAsync(rt); }
    }

    private async Task RunAsync()
    {
        if (Vm is null) return;
        var selected = Editor.SelectedText;
        var sql = string.IsNullOrWhiteSpace(selected)
            ? Squirrel.Sql.StatementSplitter.StatementAt(Editor.Text, Editor.CaretOffset)?.Text ?? Editor.Text
            : selected;
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
