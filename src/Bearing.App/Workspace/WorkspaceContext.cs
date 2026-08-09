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
    /// <summary>The open project (manifest + directory), or null before one is loaded.</summary>
    public Project? Project { get; set; }

    /// <summary>Connection new tabs fall back to when there is no active tab to inherit from.</summary>
    public Guid? DefaultConnectionId { get; set; }

    /// <summary>Whether the selected tab currently has a live session (not bound; drives status/logic).</summary>
    public bool IsConnected { get; set; }

    // ---- editor tabs (owned here so every concern shares one tab list / selection) --------------
    /// <summary>The open editor tabs. Bound (via the workspace VM); mutated by tab lifecycle operations.</summary>
    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();

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
    /// <summary>The saved connection with the given id, or null.</summary>
    public ConnectionInfo? FindConnection(Guid id)
        => Project?.Manifest.Connections.FirstOrDefault(c => c.Id == id);

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
