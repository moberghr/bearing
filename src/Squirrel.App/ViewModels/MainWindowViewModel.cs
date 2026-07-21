using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Squirrel.App.Connections;
using Squirrel.App.Formatting;
using Squirrel.App.Results;
using Squirrel.Core.Data;
using Squirrel.Core.Logging;
using Squirrel.Core.Schema;
using Squirrel.Core.Workspace;
using Squirrel.Sql;

namespace Squirrel.App.ViewModels;

/// <summary>
/// Shell view-model: owns the open project, its named connections, editor tabs, and query
/// execution + logging. Each tab targets its own connection; a <see cref="ConnectionSessionManager"/>
/// resolves that to a live, reusable session on first Run. Switching projects saves the current
/// session, disposes live connections, and restores the target project's tabs.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IProviderRegistry _providers;
    private readonly IProjectStore _projectStore;
    private readonly ISessionStore _sessionStore;
    private readonly IQueryLog _queryLog;
    private readonly IRecentProjects _recentProjects;
    private readonly IConnectionSessionManager _sessions;
    private readonly ISchemaBrowser _schemaBrowser;
    private ISecretStore? _secretStore;

    private Project? _project;
    private int _scratchCounter;

    public MainWindowViewModel(
        IProviderRegistry providers,
        IProjectStore projectStore,
        ISessionStore sessionStore,
        IQueryLog queryLog,
        IRecentProjects recentProjects,
        ISecretStore? secretStore = null)
    {
        _providers = providers;
        _projectStore = projectStore;
        _sessionStore = sessionStore;
        _queryLog = queryLog;
        _recentProjects = recentProjects;
        _secretStore = secretStore;
        _sessions = new ConnectionSessionManager(providers, () => _secretStore);
        _schemaBrowser = new SchemaBrowser(providers, () => _secretStore);
        History = new HistoryPanelViewModel(SearchHistoryAsync, ColorForConnection);
    }

    [ObservableProperty] private string _statusText = "Not connected.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunButtonText))]
    private bool _isBusy;

    [ObservableProperty] private bool _isConnected;

    /// <summary>The Run button doubles as Cancel while a query is in flight.</summary>
    public string RunButtonText => IsBusy ? "Cancel (Esc)" : "Run (Ctrl+Enter)";

    private CancellationTokenSource? _executionCts;
    [ObservableProperty] private string _title = "Squirrel";

    [ObservableProperty] private bool _sidePaneOpen = true;
    [ObservableProperty] private double _sidePaneWidth = 262;

    /// <summary>How a run's result sets are laid out in the dock (stacked vs tabbed). Persisted.</summary>
    [ObservableProperty] private Squirrel.Core.Workspace.ResultsViewMode _resultsViewMode = Squirrel.Core.Workspace.ResultsViewMode.Stacked;

    /// <summary>Which side panel the 262px column shows (driven by the left rail). The connection tree
    /// serves as both the Connections and Schema view, so it maps to <see cref="SidePanel.Schema"/>.</summary>
    [ObservableProperty] private SidePanel _activePanel = SidePanel.Schema;

    /// <summary>The inline history panel (day-grouped, filterable) shown when ActivePanel = History.</summary>
    public HistoryPanelViewModel History { get; }

    /// <summary>The Alt-toggled menu bar (File/Edit/View/Query/Help); hidden by default (design §).</summary>
    [ObservableProperty] private bool _isMenuVisible;

    /// <summary>Activate a side panel, or toggle the pane shut when its tile is re-clicked while open.</summary>
    public void ActivateOrTogglePanel(SidePanel panel)
    {
        if (ActivePanel == panel && SidePaneOpen) { SidePaneOpen = false; return; }
        ActivePanel = panel;
        SidePaneOpen = true;
    }

    partial void OnActivePanelChanged(SidePanel value)
    {
        SidePaneOpen = true; // selecting a rail tile always reveals the panel
        if (value == SidePanel.History) _ = History.ReloadAsync(CancellationToken.None);
    }

    /// <summary>Environment color of a connection by display name (for the history dot); null if unknown.</summary>
    private string? ColorForConnection(string name)
        => Connections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))?.EnvironmentColor;

    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();
    [ObservableProperty] private EditorTabViewModel? _selectedTab;

    /// <summary>The project's named connections (mirror of the manifest), shown in the side pane.</summary>
    public ObservableCollection<ConnectionInfo> Connections { get; } = new();

    /// <summary>Root nodes of the schema browser tree — one server node per connection.</summary>
    public ObservableCollection<ServerNodeViewModel> ServerNodes { get; } = new();

    /// <summary>Saved scripts under the project's scripts/ folder, shown in the side pane.</summary>
    public ObservableCollection<ScriptItem> Scripts { get; } = new();

    public ObservableCollection<RecentProjectItem> RecentProjects { get; } = new();

    /// <summary>Connection new tabs fall back to when there is no active tab to inherit from.</summary>
    public Guid? DefaultConnectionId { get; private set; }

    public string? ProjectDirectory => _project?.Directory;
    public string? ScriptsDirectory => _project?.ScriptsDirectory;
    public string? CurrentProjectName => _project?.Manifest.Name;

    /// <summary>Environment hex of the selected tab's connection (null = untagged/none). Drives the
    /// app-wide <c>ConnectionBrush</c>; the view recolors the accent when this changes.</summary>
    public string? ActiveConnectionColor => SelectedTab?.ConnectionColor;

    /// <summary>Two-way binding target for the per-tab connection picker.</summary>
    public ConnectionInfo? SelectedTabConnection
    {
        get => SelectedTab?.ConnectionId is { } id ? FindConnection(id) : null;
        set { if (SelectedTab is { } tab) SetTabConnection(tab, value?.Id); }
    }

    public void AttachSecretStore(ISecretStore secretStore) => _secretStore = secretStore;

    /// <summary>True when secrets go to a real OS keychain; false when they fall back to a plaintext-equivalent file.</summary>
    public bool SecretStorageSecure => _secretStore?.IsSecure == true;

    /// <summary>Dispose all live connections (query sessions + schema-browser pools); safe to call fire-and-forget from the close path.</summary>
    public async ValueTask DisposeSessionsAsync()
    {
        await _sessions.DisposeAsync();
        await _schemaBrowser.DisposeAsync();
    }
}
