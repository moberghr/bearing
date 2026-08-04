using System;
using System.Collections.ObjectModel;
using System.Linq;
using Bearing.App.Connections;
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
        IEntraTokenProvider? entraTokens = null)
    {
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
        Sessions = new ConnectionSessionManager(providers, () => Credentials);
        Schema = new SchemaBrowser(providers, () => Credentials);
    }

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
