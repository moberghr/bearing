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
using Bearing.App.Connections;
using Bearing.App.Formatting;
using Bearing.App.Results;
using Bearing.App.Services;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Bearing.Core.Schema;
using Bearing.Core.Workspace;
using Bearing.Sql;

namespace Bearing.App.ViewModels;

/// <summary>
/// Shell view-model: owns the open projects, their named connections, editor tabs, and query
/// execution + logging. Each tab targets its own connection; a <see cref="ConnectionSessionManager"/>
/// resolves that to a live, reusable session on first Run. Every project opened stays open —
/// switching saves sessions to disk and swaps which project's tabs are on screen, keeping the others'
/// tabs, results and pooled connections alive (see <see cref="OpenProjectAsync"/>).
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    // The shared workspace aggregate: services, current project, status sink, connection resolution.
    // The former per-concern fields are thin accessors into it, so every call site keeps working while
    // the context owns the state.
    private readonly WorkspaceContext _ctx;

    /// <summary>The connections concern (tree, pills, dialogs), exposed as <see cref="Connections"/>.</summary>
    private readonly ConnectionsViewModel _connections;

    /// <summary>The scripts concern (tree + folder/file CRUD), exposed as <see cref="Scripts"/>.</summary>
    private readonly ScriptsViewModel _scripts;

    /// <summary>The workspace concern (editor tabs + lifecycle + tab-bridging), exposed as <see cref="Workspace"/>.</summary>
    private readonly WorkspaceViewModel _workspace;

    /// <summary>The execution concern (run/page/count/FK-nav/inline-edit), exposed as <see cref="Execution"/>.</summary>
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
        IDialogService? dialogs = null,
        Connections.ICredentialPrompt? credentialPrompt = null,
        Connections.IEntraTokenProvider? entraTokens = null,
        Settings.SettingsService? settings = null)
    {
        _ctx = new WorkspaceContext(providers, projectStore, sessionStore, queryLog, recentProjects, secretStore,
            credentialPrompt: credentialPrompt, entraTokens: entraTokens, settings: settings);
        _ctx.Status = text => StatusText = text;
        EditorFontSize = _ctx.Settings.EditorFontSize;
        IsMenuVisible = _ctx.Settings.ShowMenuBar;
        _ctx.SettingsService.Changed += s =>
        {
            EditorFontSize = s.EditorFontSize;
            // Pinning shows the bar at once; unpinning takes it away again rather than leaving it stranded
            // on screen with nothing left that would hide it.
            OnPropertyChanged(nameof(IsMenuPinned));
            OnPropertyChanged(nameof(IsMenuTransient));
            IsMenuVisible = s.ShowMenuBar;
        };
        _connections = new ConnectionsViewModel(_ctx);
        _scripts = new ScriptsViewModel(_ctx, UpdateTitle);
        // The workspace owns the tabs and coordinates with the scripts concern (refresh tree / rename file)
        // and the connections concern (connection-display); it holds both directly (they're already built
        // and don't reference it back).
        _workspace = new WorkspaceViewModel(_ctx, _scripts, _connections, dialogs);
        _execution = new ExecutionViewModel(_ctx, dialogs);
        History = new HistoryPanelViewModel(SearchHistoryAsync, ColorForConnection);
    }

    // ---- Child view-models (the shell composes them; XAML/code-behind bind Vm.<child>.X) -------
    public ConnectionsViewModel Connections => _connections;
    public ScriptsViewModel Scripts => _scripts;
    public WorkspaceViewModel Workspace => _workspace;
    public ExecutionViewModel Execution => _execution;

    /// <summary>Owns the live preferences; the settings window writes through it and the shell mirrors
    /// the bits the XAML binds to (see <see cref="EditorFontSize"/>).</summary>
    public Settings.SettingsService SettingsService => _ctx.SettingsService;

    /// <summary>Editor point size, mirrored from settings so the editor can bind it and re-size live.</summary>
    [ObservableProperty] private double _editorFontSize = 14;

    [ObservableProperty] private string _statusText = "Not connected.";

    [ObservableProperty] private string _title = "Bearing";

    [ObservableProperty] private bool _sidePaneOpen = true;
    [ObservableProperty] private double _sidePaneWidth = 262;

    /// <summary>How a run's result sets are laid out in the dock (stacked vs tabbed). Persisted.</summary>
    [ObservableProperty] private Bearing.Core.Workspace.ResultsViewMode _resultsViewMode = Bearing.Core.Workspace.ResultsViewMode.Stacked;

    /// <summary>Which side panel the 262px column shows (driven by the left rail). The connection tree
    /// serves as both the Connections and Schema view, so it maps to <see cref="SidePanel.Schema"/>.</summary>
    [ObservableProperty] private SidePanel _activePanel = SidePanel.Schema;

    /// <summary>The inline history panel (day-grouped, filterable) shown when ActivePanel = History.</summary>
    public HistoryPanelViewModel History { get; }

    /// <summary>
    /// The update strip, or null when this build has no updater wired (tests, headless construction). Set by
    /// the composition root once the window exists, because applying an update means closing that window.
    /// </summary>
    [ObservableProperty] private UpdateViewModel? _updates;

    /// <summary>Whether the menu bar is on screen — pinned, or revealed by an Alt tap (design §).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMenuTransient))]
    private bool _isMenuVisible;

    /// <summary>Whether the menu bar is pinned open (Settings ▸ General). Kept separate from
    /// <see cref="IsMenuVisible"/>, which only says whether it is on screen: every auto-hide path asks this
    /// first, or using the menu would make it vanish.</summary>
    public bool IsMenuPinned => _ctx.Settings.ShowMenuBar;

    /// <summary>
    /// The menu bar is showing as a transient Alt reveal. That state <em>is</em> modal-ish — it owns Escape
    /// and suppresses global keys until it's dismissed. A pinned bar must do neither: it's furniture, and it
    /// would otherwise swallow every shortcut in the app for the whole session.
    /// </summary>
    public bool IsMenuTransient => IsMenuVisible && !IsMenuPinned;

    /// <summary>Show a side panel: switch to it <em>and</em> reveal the pane. Both halves are explicit on
    /// purpose. Revealing used to be a side effect of <see cref="OnActivePanelChanged"/>, but
    /// <c>[ObservableProperty]</c>'s setter short-circuits on an unchanged value — so "Show Scripts" while the
    /// pane was collapsed and Scripts was already active never ran the handler and did nothing at all.</summary>
    public void ShowPanel(SidePanel panel)
    {
        ActivePanel = panel;
        SidePaneOpen = true;
    }

    /// <summary>Activate a side panel, or toggle the pane shut when its tile is re-clicked while open.
    /// Deliberately different from <see cref="ShowPanel"/>: collapsing on re-activation is right for a rail
    /// tile and wrong for a command called "Show …".</summary>
    public void ActivateOrTogglePanel(SidePanel panel)
    {
        if (ActivePanel == panel && SidePaneOpen) { SidePaneOpen = false; return; }
        ShowPanel(panel);
    }

    partial void OnActivePanelChanged(SidePanel value)
    {
        // Reveal is *not* done here — see ShowPanel. This handler only reacts to the panel actually changing.
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

    /// <summary>Attach the secret store chosen at startup, and optionally the means to ask again later.</summary>
    /// <param name="reprobe">Re-runs the platform probe. Supplied by the composition root (§2.4) rather than
    /// reached for, so tests can drive the upgrade without a real keychain.</param>
    public void AttachSecretStore(ISecretStore secretStore, Func<CancellationToken, Task<ISecretStore>>? reprobe = null)
    {
        _secretStore = secretStore;
        _reprobeSecretStore = reprobe;
    }

    private Func<CancellationToken, Task<ISecretStore>>? _reprobeSecretStore;

    /// <summary>
    /// Ask again whether this machine has a usable keychain, and adopt it if so. The startup probe happens
    /// once, very early — if the keyring wasn't serving yet at that moment (a launch soon after login, a
    /// keyring daemon still settling), the app used to spend the entire session insisting there was none,
    /// refusing to save passwords, with no way to re-check short of a restart.
    /// <para>
    /// <b>Upgrades only.</b> A session already on a keychain never re-probes: a transient failure must not
    /// demote a working store and start refusing passwords mid-session (§1.1). So this is also a cheap no-op
    /// in the normal case, which is what makes it safe to call from the UI path.
    /// </para>
    /// </summary>
    /// <returns>True when the posture improved, so the caller can tell the user something changed.</returns>
    public async Task<bool> RefreshSecretStorageAsync(CancellationToken ct = default)
    {
        if (_reprobeSecretStore is null || _secretStore?.IsSecure == true) return false;

        ISecretStore candidate;
        try { candidate = await _reprobeSecretStore(ct).ConfigureAwait(true); }
        catch { return false; }   // best-effort, like every other probe on this path

        if (!candidate.IsSecure)
        {
            // Still nowhere to store a password — but the *reason* may have moved on since startup (a keyring
            // that wasn't serving then may report a dismissed unlock prompt now), and the reason is what the
            // dialog shows the user. Take the fresher one; it isn't an adoption, both stores keep nothing.
            if (candidate.UnavailableReason != _secretStore?.UnavailableReason)
            {
                _secretStore = candidate;
                OnPropertyChanged(nameof(SecretStorage));
            }
            return false;
        }

        _secretStore = candidate;
        OnPropertyChanged(nameof(SecretStorageSecure));
        OnPropertyChanged(nameof(SecretStorage));
        StatusText = SecretPosture();
        return true;
    }

    /// <summary>
    /// Bring a tab back on screen — the click target of a completion toast. A tab whose project isn't the
    /// active one is parked, so the project has to be switched to first; that is shell business (it moves
    /// the whole workspace), which is why this lives here rather than in the code-behind (§2.2).
    /// </summary>
    public async Task RevealTabAsync(EditorTabViewModel tab)
    {
        if (tab.ProjectDirectory is { } dir &&
            !string.Equals(dir, _project?.Directory, StringComparison.Ordinal))
            await OpenProjectAsync(dir);

        // Only select a tab that actually made it onto the strip: the switch may have failed, or the tab
        // may have been closed between the toast being posted and the user clicking it.
        if (_workspace.Tabs.Contains(tab)) _workspace.SelectedTab = tab;
    }

    /// <summary>
    /// Show the Scripts panel with <paramref name="absolutePath"/> selected and its folders expanded — the
    /// tab context menu's "Reveal in Scripts", and the answer to "which file is this tab?" now that a tab is
    /// named after its file. Coordination only (a panel plus the scripts concern), which is why it sits here
    /// rather than in the code-behind (§2.2); the tree walk itself is pure (<see cref="ScriptTreeReveal"/>).
    /// </summary>
    /// <returns>False when the file isn't in this project's scripts folder — reported, not silent.</returns>
    public bool RevealScript(string absolutePath)
    {
        ShowPanel(SidePanel.Scripts);
        if (_scripts.Reveal(absolutePath)) return true;
        StatusText = $"{Path.GetFileName(absolutePath)} isn't in this project's scripts folder.";
        return false;
    }

    /// <summary>True when secrets go to a real OS keychain; false when none could be reached, in which case
    /// nothing is stored at all.</summary>
    public bool SecretStorageSecure => _secretStore?.IsSecure == true;

    /// <summary>What the connection editor needs to know about where a password would end up: whether the
    /// store is a real keychain, whether it will accept a password at all, and — when it won't — why.</summary>
    public SecretStoragePosture SecretStorage => new(
        Secure: _secretStore?.IsSecure == true,
        CanStore: _secretStore?.CanStore == true,
        Reason: _secretStore?.UnavailableReason);

    /// <summary>Dispose all live connections (query sessions + schema-browser pools); safe to call fire-and-forget from the close path.</summary>
    public async ValueTask DisposeSessionsAsync()
    {
        await _sessions.DisposeAsync();
        await _schemaBrowser.DisposeAsync();
    }
}
