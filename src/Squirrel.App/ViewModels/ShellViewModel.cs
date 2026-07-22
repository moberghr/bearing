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
using Squirrel.App.Services;
using Squirrel.App.Workspace;
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
public sealed partial class ShellViewModel : ObservableObject
{
    // The shared workspace aggregate: services, current project, status sink, connection resolution.
    // The former fields below are now thin accessors into it (phase 0 of docs/mvvm-refactor-plan.md), so
    // every existing call site keeps working while ownership of the state moves to the context.
    private readonly WorkspaceContext _ctx;

    /// <summary>The connections concern (tree, pills, dialogs). The shell re-exposes its surface as thin
    /// delegates (see ShellViewModel.Connections.cs) so bindings and code-behind stay unchanged.</summary>
    private readonly ConnectionsViewModel _connections;

    /// <summary>The scripts concern (tree + folder/file CRUD). Same delegation facade (ShellViewModel.Scripts.cs).</summary>
    private readonly ScriptsViewModel _scripts;

    /// <summary>The workspace concern (editor tabs + lifecycle + tab-bridging). Same delegation facade (ShellViewModel.Tabs.cs).</summary>
    private readonly WorkspaceViewModel _workspace;

    /// <summary>The execution concern (run/page/count/FK-nav/inline-edit). Same delegation facade (ShellViewModel.Execution.cs).</summary>
    private readonly ExecutionViewModel _execution;

    private IProviderRegistry _providers => _ctx.Providers;
    private IProjectStore _projectStore => _ctx.ProjectStore;
    private ISessionStore _sessionStore => _ctx.SessionStore;
    private IQueryLog _queryLog => _ctx.QueryLog;
    private IRecentProjects _recentProjects => _ctx.RecentProjects;
    private IConnectionSessionManager _sessions => _ctx.Sessions;
    private ISchemaBrowser _schemaBrowser => _ctx.Schema;
    private ISecretStore? _secretStore { get => _ctx.Secrets; set => _ctx.Secrets = value; }
    private Project? _project { get => _ctx.Project; set => _ctx.Project = value; }

    public ShellViewModel(
        IProviderRegistry providers,
        IProjectStore projectStore,
        ISessionStore sessionStore,
        IQueryLog queryLog,
        IRecentProjects recentProjects,
        ISecretStore? secretStore = null,
        IDialogService? dialogs = null)
    {
        _ctx = new WorkspaceContext(providers, projectStore, sessionStore, queryLog, recentProjects, secretStore);
        _ctx.Status = text => StatusText = text;
        _connections = new ConnectionsViewModel(_ctx);
        _scripts = new ScriptsViewModel(_ctx, UpdateTitle);
        // The workspace owns the tabs; it calls back to the connections concern (connection-display /
        // script rename) and the shell (title / scripts refresh) rather than referencing those VMs.
        _workspace = new WorkspaceViewModel(_ctx, _scripts.RefreshScripts, UpdateTitle,
            _connections.ApplyConnectionDisplay, _scripts.RenameScriptAsync);
        _execution = new ExecutionViewModel(_ctx, dialogs);
        History = new HistoryPanelViewModel(SearchHistoryAsync, ColorForConnection);
    }

    // ---- Child view-models (the shell composes them; XAML/code-behind bind Vm.<child>.X) -------
    public ConnectionsViewModel Connections => _connections;
    public ScriptsViewModel Scripts => _scripts;
    public WorkspaceViewModel Workspace => _workspace;
    public ExecutionViewModel Execution => _execution;

    [ObservableProperty] private string _statusText = "Not connected.";

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
        => _connections.Connections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))?.EnvironmentColor;

    /// <summary>Search the query log (feeds the History panel VM); stays on the shell — it only touches the log.</summary>
    public Task<IReadOnlyList<QueryLogEntry>> SearchHistoryAsync(string? text, CancellationToken ct)
        => _queryLog.SearchAsync(new QueryLogQuery { Text = text }, ct);

    // Internal helpers used by the project/session partials; the real work lives in the child VMs.
    private void RefreshConnections() => _connections.RefreshConnections();
    private void RefreshScripts() => _scripts.RefreshScripts();
    private void ApplyConnectionDisplay(EditorTabViewModel tab) => _connections.ApplyConnectionDisplay(tab);

    public ObservableCollection<RecentProjectItem> RecentProjects { get; } = new();

    public string? ProjectDirectory => _project?.Directory;
    public string? ScriptsDirectory => _project?.ScriptsDirectory;
    public string? CurrentProjectName => _project?.Manifest.Name;

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
