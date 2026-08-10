using System;
using System.Collections.ObjectModel;
using System.Linq;
using Bearing.App.Connections;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Logging;
using Bearing.Core.Workspace;
using Bearing.Persistence;

namespace Bearing.App.Workspace;

/// <summary>
/// The shared aggregate every workspace concern coordinates through: the injected stores, the live
/// connection/schema services, the current project, a status sink, and connection resolution. It is the
/// single owner of state that would otherwise force the concern view-models to depend on each other —
/// the shell and every child VM (connections/scripts/workspace/execution) coordinate through it.
/// </summary>
public sealed class WorkspaceContext
{
    public WorkspaceContext(
        IProviderRegistry providers,
        IProjectStore projectStore,
        ISessionStore sessionStore,
        IQueryLog queryLog,
        IRecentProjects recentProjects,
        ISecretStore? secrets = null,
        IScriptStore? scriptStore = null,
        ICredentialPrompt? credentialPrompt = null,
        IEntraTokenProvider? entraTokens = null,
        SettingsService? settings = null)
    {
        SettingsService = settings ?? SettingsService.InMemory();
        Providers = providers;
        ProjectStore = projectStore;
        SessionStore = sessionStore;
        QueryLog = queryLog;
        RecentProjects = recentProjects;
        Secrets = secrets;
        ScriptStore = scriptStore ?? new FileScriptStore();
        // Credential resolution reads the secret store lazily (via () => Secrets) so a late
        // AttachSecretStore still applies; prompted passwords / Entra tokens are cached in-memory here.
        Credentials = new CredentialResolver(() => Secrets, credentialPrompt, entraTokens ?? new EntraTokenProvider());
        var sessions = new ConnectionSessionManager(providers, () => Credentials, IdleTimeout(Settings));
        Sessions = sessions;
        Schema = new SchemaBrowser(providers, () => Credentials);

        // The idle sweep is the one service that caches a setting rather than reading it per use, so it
        // has to be told when the setting changes.
        SettingsService.Changed += s => sessions.IdleTimeout = IdleTimeout(s);
    }

    private static TimeSpan IdleTimeout(AppSettings s)
        => TimeSpan.FromMinutes(Math.Max(1, s.ConnectionIdleTimeoutMinutes));

    // ---- services -----------------------------------------------------------------------------
    public IProviderRegistry Providers { get; }
    public IProjectStore ProjectStore { get; }
    public ISessionStore SessionStore { get; }
    public IQueryLog QueryLog { get; }
    public IRecentProjects RecentProjects { get; }
    public ISecretStore? Secrets { get; set; }
    public IScriptStore ScriptStore { get; }
    /// <summary>Resolves the secret each connection authenticates with (stored / prompt / Entra token) and
    /// caches prompted/token credentials in memory. Also used by the execution path's refresh-and-retry.</summary>
    public CredentialResolver Credentials { get; }
    public IConnectionSessionManager Sessions { get; }
    public ISchemaBrowser Schema { get; }

    /// <summary>Owns the live user preferences: edits, persistence, and the change broadcast. Defaults
    /// backed by nothing when none were supplied, so headless/test construction behaves like a fresh
    /// install.</summary>
    public SettingsService SettingsService { get; }

    /// <summary>The preferences in force right now. A property, not a snapshot — every read goes through
    /// the service, so a setting changed in the settings window takes effect at the next read with no
    /// subscription needed. Only cache this if you also subscribe to the service's Changed event.</summary>
    public AppSettings Settings => SettingsService.Current;

    /// <summary>Writes buffers to disk without an explicit Save. Lives here so the execution concern can
    /// signal a run (the <c>OnExecute</c> mode) without depending on the workspace view-model.</summary>
    public TabAutosave? Autosave { get; set; }

    // ---- shared state -------------------------------------------------------------------------
    /// <summary>The <b>active</b> project (manifest + directory), or null before one is loaded. Other
    /// projects opened this session stay alive alongside it — see <see cref="OpenProjects"/>.</summary>
    public Project? Project { get; set; }

    /// <summary>Connection new tabs fall back to when there is no active tab to inherit from.</summary>
    public Guid? DefaultConnectionId { get; set; }

    /// <summary>Whether the selected tab currently has a live session (not bound; drives status/logic).</summary>
    public bool IsConnected { get; set; }

    // ---- open projects ------------------------------------------------------------------------
    // Every project opened this session stays open. Switching is a view change: the outgoing project's
    // tabs are parked on its ProjectWorkspace and the incoming project's are unparked, so tab view-models
    // — and with them results, in-flight queries and pooled connections — are never rebuilt.
    private readonly Dictionary<string, ProjectWorkspace> _open = new(StringComparer.Ordinal);

    /// <summary>Every project opened this session, active one included.</summary>
    public IReadOnlyCollection<ProjectWorkspace> OpenProjects => _open.Values;

    /// <summary>The parked state for <paramref name="project"/>, created on first open. Re-opening a
    /// directory that is already registered keeps its tabs but adopts the freshly-loaded
    /// <see cref="Project"/> instance, so the registry and <see cref="Project"/> never disagree about
    /// which manifest is live.</summary>
    public ProjectWorkspace GetOrAdd(Project project)
    {
        var key = Key(project.Directory);
        if (_open.TryGetValue(key, out var existing))
        {
            if (!ReferenceEquals(existing.Project, project)) existing.Project = project;
            return existing;
        }
        var created = new ProjectWorkspace { Project = project };
        _open[key] = created;
        return created;
    }

    /// <summary>The already-open project rooted at <paramref name="directory"/>, or null.</summary>
    public ProjectWorkspace? Find(string directory)
        => _open.TryGetValue(Key(directory), out var w) ? w : null;

    /// <summary>The project a tab belongs to — the one whose scratch folder, scripts folder and session
    /// file own it, which is <b>not</b> necessarily the active project once the tab has been parked.</summary>
    public Project? ProjectOf(EditorTabViewModel tab)
        => tab.ProjectDirectory is { } dir ? Find(dir)?.Project : null;

    // Paths are the registry key, so they must be normalized the same way everywhere — a relative
    // directory handed to Find must land on the entry a full path created.
    private static string Key(string directory)
    {
        try { return Path.GetFullPath(directory); }
        catch { return directory; }   // malformed path: key it verbatim rather than throwing on lookup
    }

    /// <summary>Park the active project: stash its tabs and layout, then clear the visible tab list. The
    /// tab view-models stay alive on the <see cref="ProjectWorkspace"/> — nothing is disposed or cancelled.</summary>
    public void Park(bool sidePaneOpen, double sidePaneWidth, ResultsViewMode resultsViewMode)
    {
        if (Project is null) return;
        var workspace = GetOrAdd(Project);
        workspace.SelectedIndex = SelectedTab is { } sel ? Math.Max(0, Tabs.IndexOf(sel)) : 0;
        workspace.DefaultConnectionId = DefaultConnectionId;
        workspace.SidePaneOpen = sidePaneOpen;
        workspace.SidePaneWidth = sidePaneWidth;
        workspace.ResultsViewMode = resultsViewMode;
        workspace.ParkedTabs.Clear();
        workspace.ParkedTabs.AddRange(Tabs);
        SelectedTab = null;
        Tabs.Clear();
    }

    /// <summary>Unpark a project: put its tabs back on screen and re-select the one that was active. The
    /// inverse of <see cref="Park"/>; the caller has already parked whatever was showing.</summary>
    public void Restore(ProjectWorkspace workspace)
    {
        Project = workspace.Project;
        foreach (var tab in workspace.ParkedTabs) Tabs.Add(tab);
        workspace.ParkedTabs.Clear();
        SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(workspace.SelectedIndex, 0, Tabs.Count - 1)];
    }

    // ---- editor tabs (owned here so every concern shares one tab list / selection) --------------
    /// <summary>The <b>active project's</b> editor tabs. Bound (via the workspace VM) as the tab strip;
    /// mutated by tab lifecycle operations. Tabs belonging to other open projects are not in here — use
    /// <see cref="AllTabs"/> for anything that must see every live tab (autosave, quit guard, a run
    /// finishing on a tab the user has switched away from).</summary>
    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();

    /// <summary>Every live editor tab across every open project. Exact union: parked lists are empty for
    /// whichever project is active, so no tab appears twice.</summary>
    public IEnumerable<EditorTabViewModel> AllTabs
        => Tabs.Concat(_open.Values.SelectMany(w => w.ParkedTabs));

    private EditorTabViewModel? _selectedTab;

    /// <summary>The active editor tab. Setting it raises <see cref="SelectedTabChanged"/> so the workspace
    /// VM re-notifies its binding and the connections concern re-warms / re-derives its pickers.</summary>
    public EditorTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (ReferenceEquals(_selectedTab, value)) return;
            _selectedTab = value;
            SelectedTabChanged?.Invoke();
        }
    }

    /// <summary>Raised whenever <see cref="SelectedTab"/> changes (plain C# event; see plan decision 1).</summary>
    public event Action? SelectedTabChanged;

    // ---- status sink --------------------------------------------------------------------------
    /// <summary>Where <see cref="SetStatus"/> forwards — wired by the shell to its bound StatusText.</summary>
    public Action<string>? Status { get; set; }

    /// <summary>Post a status-bar message (used by every concern; single sink → one bound property).</summary>
    public void SetStatus(string text) => Status?.Invoke(text);

    // ---- connection resolution ----------------------------------------------------------------
    /// <summary>
    /// The saved connection with the given id, or null. Searches the active project first, then every
    /// other open project: a parked tab must still resolve its connection (for its header display, and
    /// for a query that is still running on it) while another project is on screen. Connection ids are
    /// Guids, so a cross-project hit is never an accident.
    /// </summary>
    public ConnectionInfo? FindConnection(Guid id)
    {
        if (Project?.Manifest.Connections.FirstOrDefault(c => c.Id == id) is { } active) return active;
        foreach (var workspace in _open.Values)
        {
            if (ReferenceEquals(workspace.Project, Project)) continue;
            if (workspace.Project.Manifest.Connections.FirstOrDefault(c => c.Id == id) is { } other) return other;
        }
        return null;
    }

    /// <summary>The connection a tab actually runs against: its saved connection with the tab's active
    /// database substituted in (the toolbar Database pill can target another DB on the same server).
    /// Keeps the connection <c>Id</c> so the password (secret keyed by Id) is reused on connect.</summary>
    public ConnectionInfo? EffectiveConnection(EditorTabViewModel tab)
    {
        if (tab.ConnectionId is not { } id || FindConnection(id) is not { } info) return null;
        return tab.DatabaseName is { } db && !string.Equals(db, info.Database, StringComparison.Ordinal)
            ? info with { Database = db }
            : info;
    }
}
