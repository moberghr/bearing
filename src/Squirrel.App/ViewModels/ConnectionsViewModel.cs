using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Squirrel.App.Connections;
using Squirrel.App.Workspace;
using Squirrel.Core.Data;

namespace Squirrel.App.ViewModels;

/// <summary>
/// The connections concern: the project's named connections, the schema-browser tree, per-tab connection
/// and database selection, and the add/edit/delete/refresh/test/warm operations. Extracted from the shell
/// (docs/mvvm-refactor-plan.md phase 1); coordinates through <see cref="WorkspaceContext"/>, reading the
/// selected tab / tab list from it (moved into the context in phase 4). Subscribes to the context's
/// <see cref="WorkspaceContext.SelectedTabChanged"/> so its pickers re-derive on a tab switch. The shell
/// re-exposes this VM's surface as thin delegates so existing bindings and code-behind stay unchanged.
/// </summary>
public sealed partial class ConnectionsViewModel : ObservableObject
{
    private readonly WorkspaceContext _ctx;

    public ConnectionsViewModel(WorkspaceContext ctx)
    {
        _ctx = ctx;
        _ctx.SelectedTabChanged += OnSelectedTabChanged;
    }

    private EditorTabViewModel? Selected => _ctx.SelectedTab;
    private ObservableCollection<EditorTabViewModel> Tabs => _ctx.Tabs;

    /// <summary>The project's named connections (mirror of the manifest), shown in the side pane.</summary>
    public ObservableCollection<ConnectionInfo> Connections { get; } = new();

    /// <summary>Root nodes of the schema browser tree — one server node per connection.</summary>
    public ObservableCollection<ServerNodeViewModel> ServerNodes { get; } = new();

    /// <summary>Databases available on the selected tab's server (populates the Database pill).</summary>
    public ObservableCollection<string> TabDatabases { get; } = new();

    /// <summary>Environment hex of the selected tab's connection (null = untagged/none). Drives the accent.</summary>
    public string? ActiveConnectionColor => Selected?.ConnectionColor;

    /// <summary>Two-way binding target for the per-tab connection picker.</summary>
    public ConnectionInfo? SelectedTabConnection
    {
        get => Selected?.ConnectionId is { } id ? _ctx.FindConnection(id) : null;
        set { if (Selected is { } tab) SetTabConnection(tab, value?.Id); }
    }

    /// <summary>Two-way binding target for the Database pill. Falls back to the connection's own database
    /// so the pill shows the DB actually in use even when no explicit override has been chosen.</summary>
    public string? SelectedTabDatabase
    {
        get
        {
            if (Selected is not { } tab) return null;
            return tab.DatabaseName ?? (tab.ConnectionId is { } id ? _ctx.FindConnection(id)?.Database : null);
        }
        set { if (Selected is { } tab && value is not null) SetTabDatabase(tab, value); }
    }

    /// <summary>Called by the shell when the selected tab changes: re-notify the derived pickers, refresh
    /// the connected flag + database list, and warm the connection.</summary>
    public void OnSelectedTabChanged()
    {
        OnPropertyChanged(nameof(SelectedTabConnection));
        OnPropertyChanged(nameof(ActiveConnectionColor));
        OnPropertyChanged(nameof(SelectedTabDatabase));
        var tab = Selected;
        _ctx.IsConnected = tab?.ConnectionId is { } id && _ctx.Sessions.TryGet(id) is not null;
        RefreshTabDatabases(tab);
        WarmConnection(tab);
    }

    public void RefreshConnections()
    {
        Connections.Clear();
        ServerNodes.Clear();
        if (_ctx.Project is null) return;
        foreach (var c in _ctx.Project.Manifest.Connections)
        {
            Connections.Add(c);
            ServerNodes.Add(new ServerNodeViewModel(c, _ctx.Schema));
        }
    }

    public void ApplyConnectionDisplay(EditorTabViewModel tab)
    {
        var info = tab.ConnectionId is { } id ? _ctx.FindConnection(id) : null;
        tab.ConnectionDisplay = info?.Name;
        tab.ConnectionColor = info?.EnvironmentColor;
        tab.DatabaseName ??= info?.Database; // default the active DB to the connection's own
    }

    public void SetTabConnection(EditorTabViewModel tab, Guid? id)
    {
        tab.ConnectionId = id;
        tab.DatabaseName = null;            // reset to the new connection's default DB
        ApplyConnectionDisplay(tab);
        if (ReferenceEquals(tab, Selected))
        {
            OnPropertyChanged(nameof(SelectedTabConnection));
            OnPropertyChanged(nameof(ActiveConnectionColor));
            OnPropertyChanged(nameof(SelectedTabDatabase));
            _ctx.IsConnected = id is { } cid && _ctx.Sessions.TryGet(cid) is not null;
            RefreshTabDatabases(tab);
            WarmConnection(tab);
        }
    }

    /// <summary>Point a tab at another database on its server. Reuses the connection's credentials;
    /// the session manager disposes the old DB's session and connects the new one on next use.</summary>
    public void SetTabDatabase(EditorTabViewModel tab, string database)
    {
        if (string.Equals(tab.DatabaseName, database, StringComparison.Ordinal)) return;
        tab.DatabaseName = database;
        if (ReferenceEquals(tab, Selected))
        {
            OnPropertyChanged(nameof(SelectedTabDatabase));
            _ctx.IsConnected = false;
            WarmConnection(tab);
        }
    }

    /// <summary>Load the server's database list into <see cref="TabDatabases"/> for the given tab.</summary>
    private async void RefreshTabDatabases(EditorTabViewModel? tab)
    {
        TabDatabases.Clear();
        if (tab?.ConnectionId is not { } id || _ctx.FindConnection(id) is not { } info)
        {
            OnPropertyChanged(nameof(SelectedTabDatabase));
            return;
        }
        var current = tab.DatabaseName ?? info.Database;
        // Show the tab's current DB immediately (never leave the pill empty while offline), and re-notify
        // so the ComboBox selects it now that the item exists.
        TabDatabases.Add(current);
        OnPropertyChanged(nameof(SelectedTabDatabase));
        try
        {
            var dbs = await _ctx.Schema.GetDatabasesAsync(info, CancellationToken.None);
            if (!ReferenceEquals(tab, Selected)) return;
            TabDatabases.Clear();
            foreach (var d in dbs) TabDatabases.Add(d);
            if (!TabDatabases.Contains(current)) TabDatabases.Insert(0, current); // keep the selection valid
            OnPropertyChanged(nameof(SelectedTabDatabase));
        }
        catch { /* offline — keep the single current-DB entry */ }
    }

    /// <summary>Fetch the stored password for the connection editor's edit mode (null if none).</summary>
    public async Task<string?> GetConnectionPasswordAsync(Guid id)
        => _ctx.Secrets is null ? null : await _ctx.Secrets.GetPasswordAsync(id, CancellationToken.None);

    /// <summary>Add or replace a connection in the manifest and its password in the secret store.</summary>
    public async Task AddOrUpdateConnectionAsync(ConnectionInfo conn, string? password)
    {
        if (_ctx.Project is null) return;
        var list = _ctx.Project.Manifest.Connections;
        var idx = list.FindIndex(c => c.Id == conn.Id);
        var networkChanged = true;
        if (idx >= 0) { networkChanged = !SameNetwork(list[idx], conn); list[idx] = conn; }
        else list.Add(conn);

        try
        {
            if (_ctx.Secrets is not null && password is not null)
            {
                if (password.Length == 0) await _ctx.Secrets.DeleteAsync(conn.Id, CancellationToken.None);
                else await _ctx.Secrets.SetPasswordAsync(conn.Id, password, CancellationToken.None);
            }
            await _ctx.ProjectStore.SaveAsync(_ctx.Project, CancellationToken.None);
        }
        catch (Exception ex) { _ctx.SetStatus($"Saved connection but secret/store failed: {ex.Message}"); }

        if (networkChanged) await _ctx.Sessions.EvictAsync(conn.Id);
        _ctx.DefaultConnectionId ??= conn.Id;
        RefreshConnections();
        foreach (var t in Tabs) if (t.ConnectionId == conn.Id) ApplyConnectionDisplay(t);
        OnPropertyChanged(nameof(SelectedTabConnection));
        _ctx.SetStatus($"Saved connection '{conn.Name}'.");
    }

    public async Task DeleteConnectionAsync(Guid id)
    {
        if (_ctx.Project is null) return;
        var removed = _ctx.Project.Manifest.Connections.FirstOrDefault(c => c.Id == id);
        _ctx.Project.Manifest.Connections.RemoveAll(c => c.Id == id);

        try
        {
            if (_ctx.Secrets is not null) await _ctx.Secrets.DeleteAsync(id, CancellationToken.None);
            await _ctx.ProjectStore.SaveAsync(_ctx.Project, CancellationToken.None);
        }
        catch (Exception ex) { _ctx.SetStatus($"Deleted connection but store failed: {ex.Message}"); }

        await _ctx.Sessions.EvictAsync(id);
        foreach (var t in Tabs) if (t.ConnectionId == id) { t.ConnectionId = null; ApplyConnectionDisplay(t); }
        if (_ctx.DefaultConnectionId == id) _ctx.DefaultConnectionId = null;
        RefreshConnections();
        OnPropertyChanged(nameof(SelectedTabConnection));
        _ctx.SetStatus(removed is null ? "Connection deleted." : $"Deleted connection '{removed.Name}'.");
    }

    /// <summary>
    /// Refresh all cached metadata for a connection: drop the schema-browser's per-database readers,
    /// evict the live session so completion + editability reload its snapshot, reload the tree node,
    /// and re-warm the selected tab if it targets this connection.
    /// </summary>
    public async Task RefreshServerMetadataAsync(Guid connectionId)
    {
        await _ctx.Schema.InvalidateAsync(connectionId);
        await _ctx.Sessions.EvictAsync(connectionId);

        var node = ServerNodes.FirstOrDefault(n => n.Connection.Id == connectionId);
        if (node is not null) await node.RefreshAsync();

        if (Selected?.ConnectionId == connectionId) WarmConnection(Selected);
        _ctx.SetStatus("Schema metadata refreshed.");
    }

    private static bool SameNetwork(ConnectionInfo a, ConnectionInfo b)
        => a.ProviderId == b.ProviderId && a.Host == b.Host && a.Port == b.Port
           && a.Database == b.Database && a.User == b.User;

    /// <summary>Build a throwaway connection and test it (for the dialog's Test button); nothing is persisted.</summary>
    public async Task<bool> TestConnectionAsync(ConnectionInfo info, string? password, CancellationToken ct)
    {
        var provider = _ctx.Providers.Get(info.ProviderId);
        var factory = provider.CreateConnectionFactory(info, password);
        try { return await factory.TestConnectionAsync(ct); }
        finally { await factory.DisposeAsync(); }
    }

    /// <summary>First-run convenience: seed a demo connection if the project has none, and target it.</summary>
    public async Task SeedDemoConnectionAsync(string host, int port, string database, string user, string password)
    {
        if (_ctx.Project is null || _ctx.Project.Manifest.Connections.Count > 0) return;
        var conn = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = $"{database} (local)",
            ProviderId = "postgres",
            Host = host, Port = port, Database = database, User = user,
            Environment = "local", EnvironmentColor = "#7AA89F",
        };
        await AddOrUpdateConnectionAsync(conn, password);
        _ctx.DefaultConnectionId = conn.Id;
        foreach (var t in Tabs) if (t.ConnectionId is null) SetTabConnection(t, conn.Id);
        _ctx.SetStatus($"Added demo connection '{conn.Name}'. Press F5 to run.");
    }

    /// <summary>Background connect + schema warm so completion is ready before the first Run. Quiet on failure.</summary>
    private async void WarmConnection(EditorTabViewModel? tab)
    {
        if (tab is null) return;
        var info = _ctx.EffectiveConnection(tab);
        if (info is null) return;
        try
        {
            var session = await _ctx.Sessions.GetOrConnectAsync(info, CancellationToken.None);
            if (ReferenceEquals(Selected, tab)) _ctx.IsConnected = true;
            var snapshot = await _ctx.Sessions.EnsureSchemaAsync(session, CancellationToken.None);
            if (ReferenceEquals(Selected, tab))
                _ctx.SetStatus(snapshot is null
                    ? $"Connected to {info.Name}."
                    : $"Connected to {info.Name} · {snapshot.Tables.Count} tables.");
        }
        catch (ConnectionFailedException) { /* Run will surface the error explicitly */ }
        catch { /* completion warming must never disrupt the UI */ }
    }
}
