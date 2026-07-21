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
public sealed partial class MainWindowViewModel
{
    // ---- Connections ------------------------------------------------------------------------

    private void RefreshConnections()
    {
        Connections.Clear();
        ServerNodes.Clear();
        if (_project is null) return;
        foreach (var c in _project.Manifest.Connections)
        {
            Connections.Add(c);
            ServerNodes.Add(new ServerNodeViewModel(c, _schemaBrowser));
        }
    }

    private ConnectionInfo? FindConnection(Guid id)
        => _project?.Manifest.Connections.FirstOrDefault(c => c.Id == id);

    private void ApplyConnectionDisplay(EditorTabViewModel tab)
    {
        var info = tab.ConnectionId is { } id ? FindConnection(id) : null;
        tab.ConnectionDisplay = info?.Name;
        tab.ConnectionColor = info?.EnvironmentColor;
        tab.DatabaseName ??= info?.Database; // default the active DB to the connection's own
    }

    /// <summary>The connection a tab actually runs against: its saved connection with the active
    /// database substituted in (the toolbar Database pill can point at another DB on the same server).
    /// Keeps the connection <c>Id</c> so the password (secret keyed by Id) is reused on connect.</summary>
    private ConnectionInfo? EffectiveConnection(EditorTabViewModel tab)
    {
        if (tab.ConnectionId is not { } id || FindConnection(id) is not { } info) return null;
        return tab.DatabaseName is { } db && !string.Equals(db, info.Database, StringComparison.Ordinal)
            ? info with { Database = db }
            : info;
    }

    public void SetTabConnection(EditorTabViewModel tab, Guid? id)
    {
        tab.ConnectionId = id;
        tab.DatabaseName = null;            // reset to the new connection's default DB
        ApplyConnectionDisplay(tab);
        if (ReferenceEquals(tab, SelectedTab))
        {
            OnPropertyChanged(nameof(SelectedTabConnection));
            OnPropertyChanged(nameof(ActiveConnectionColor));
            OnPropertyChanged(nameof(SelectedTabDatabase));
            IsConnected = id is { } cid && _sessions.TryGet(cid) is not null;
            RefreshTabDatabases(tab);
            WarmConnection(tab);
        }
    }

    // ---- Database selection (toolbar Database pill) ------------------------------------------

    /// <summary>Databases available on the selected tab's server (populates the Database pill).</summary>
    public ObservableCollection<string> TabDatabases { get; } = new();

    /// <summary>Two-way binding target for the Database pill; switching opens a session on that DB.
    /// Falls back to the connection's own database so the pill shows the DB actually in use even when
    /// no explicit override has been chosen.</summary>
    public string? SelectedTabDatabase
    {
        get
        {
            if (SelectedTab is not { } tab) return null;
            return tab.DatabaseName ?? (tab.ConnectionId is { } id ? FindConnection(id)?.Database : null);
        }
        set { if (SelectedTab is { } tab && value is not null) SetTabDatabase(tab, value); }
    }

    /// <summary>Point a tab at another database on its server. Reuses the connection's credentials;
    /// the session manager disposes the old DB's session and connects the new one on next use.</summary>
    public void SetTabDatabase(EditorTabViewModel tab, string database)
    {
        if (string.Equals(tab.DatabaseName, database, StringComparison.Ordinal)) return;
        tab.DatabaseName = database;
        if (ReferenceEquals(tab, SelectedTab))
        {
            OnPropertyChanged(nameof(SelectedTabDatabase));
            IsConnected = false;
            WarmConnection(tab);
        }
    }

    /// <summary>Load the server's database list into <see cref="TabDatabases"/> for the given tab.</summary>
    private async void RefreshTabDatabases(EditorTabViewModel? tab)
    {
        TabDatabases.Clear();
        if (tab?.ConnectionId is not { } id || FindConnection(id) is not { } info)
        {
            OnPropertyChanged(nameof(SelectedTabDatabase));
            return;
        }
        var current = tab.DatabaseName ?? info.Database;
        // Show the tab's current DB immediately (never leave the pill empty while offline), and
        // re-notify so the ComboBox selects it now that the item exists (the earlier notify from
        // OnSelectedTabChanged fired before this list was populated).
        TabDatabases.Add(current);
        OnPropertyChanged(nameof(SelectedTabDatabase));
        try
        {
            var dbs = await _schemaBrowser.GetDatabasesAsync(info, CancellationToken.None);
            if (!ReferenceEquals(tab, SelectedTab)) return;
            TabDatabases.Clear();
            foreach (var d in dbs) TabDatabases.Add(d);
            if (!TabDatabases.Contains(current)) TabDatabases.Insert(0, current); // keep the selection valid
            OnPropertyChanged(nameof(SelectedTabDatabase));
        }
        catch { /* offline — keep the single current-DB entry */ }
    }

    /// <summary>Fetch the stored password for the connection editor's edit mode (null if none).</summary>
    public async Task<string?> GetConnectionPasswordAsync(Guid id)
        => _secretStore is null ? null : await _secretStore.GetPasswordAsync(id, CancellationToken.None);

    /// <summary>Add or replace a connection in the manifest and its password in the secret store.</summary>
    public async Task AddOrUpdateConnectionAsync(ConnectionInfo conn, string? password)
    {
        if (_project is null) return;
        var list = _project.Manifest.Connections;
        var idx = list.FindIndex(c => c.Id == conn.Id);
        var networkChanged = true;
        if (idx >= 0) { networkChanged = !SameNetwork(list[idx], conn); list[idx] = conn; }
        else list.Add(conn);

        try
        {
            if (_secretStore is not null && password is not null)
            {
                if (password.Length == 0) await _secretStore.DeleteAsync(conn.Id, CancellationToken.None);
                else await _secretStore.SetPasswordAsync(conn.Id, password, CancellationToken.None);
            }
            await _projectStore.SaveAsync(_project, CancellationToken.None);
        }
        catch (Exception ex) { StatusText = $"Saved connection but secret/store failed: {ex.Message}"; }

        if (networkChanged) await _sessions.EvictAsync(conn.Id);
        DefaultConnectionId ??= conn.Id;
        RefreshConnections();
        foreach (var t in Tabs) if (t.ConnectionId == conn.Id) ApplyConnectionDisplay(t);
        OnPropertyChanged(nameof(SelectedTabConnection));
        StatusText = $"Saved connection '{conn.Name}'.";
    }

    public async Task DeleteConnectionAsync(Guid id)
    {
        if (_project is null) return;
        var removed = _project.Manifest.Connections.FirstOrDefault(c => c.Id == id);
        _project.Manifest.Connections.RemoveAll(c => c.Id == id);

        try
        {
            if (_secretStore is not null) await _secretStore.DeleteAsync(id, CancellationToken.None);
            await _projectStore.SaveAsync(_project, CancellationToken.None);
        }
        catch (Exception ex) { StatusText = $"Deleted connection but store failed: {ex.Message}"; }

        await _sessions.EvictAsync(id);
        foreach (var t in Tabs) if (t.ConnectionId == id) { t.ConnectionId = null; ApplyConnectionDisplay(t); }
        if (DefaultConnectionId == id) DefaultConnectionId = null;
        RefreshConnections();
        OnPropertyChanged(nameof(SelectedTabConnection));
        StatusText = removed is null ? "Connection deleted." : $"Deleted connection '{removed.Name}'.";
    }

    /// <summary>
    /// Refresh all cached metadata for a connection: drop the schema-browser's per-database readers,
    /// evict the live session so completion + editability reload its snapshot, reload the tree node,
    /// and re-warm the selected tab if it targets this connection.
    /// </summary>
    public async Task RefreshServerMetadataAsync(Guid connectionId)
    {
        await _schemaBrowser.InvalidateAsync(connectionId);
        await _sessions.EvictAsync(connectionId);

        var node = ServerNodes.FirstOrDefault(n => n.Connection.Id == connectionId);
        if (node is not null) await node.RefreshAsync();

        if (SelectedTab?.ConnectionId == connectionId) WarmConnection(SelectedTab);
        StatusText = "Schema metadata refreshed.";
    }

    private static bool SameNetwork(ConnectionInfo a, ConnectionInfo b)
        => a.ProviderId == b.ProviderId && a.Host == b.Host && a.Port == b.Port
           && a.Database == b.Database && a.User == b.User;

    /// <summary>Build a throwaway connection and test it (for the dialog's Test button); nothing is persisted.</summary>
    public async Task<bool> TestConnectionAsync(ConnectionInfo info, string? password, CancellationToken ct)
    {
        var provider = _providers.Get(info.ProviderId);
        var factory = provider.CreateConnectionFactory(info, password);
        try { return await factory.TestConnectionAsync(ct); }
        finally { await factory.DisposeAsync(); }
    }

    /// <summary>First-run convenience: seed a demo connection if the project has none, and target it.</summary>
    public async Task SeedDemoConnectionAsync(string host, int port, string database, string user, string password)
    {
        if (_project is null || _project.Manifest.Connections.Count > 0) return;
        var conn = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = $"{database} (local)",
            ProviderId = "postgres",
            Host = host, Port = port, Database = database, User = user,
            Environment = "local", EnvironmentColor = "#7AA89F",
        };
        await AddOrUpdateConnectionAsync(conn, password);
        DefaultConnectionId = conn.Id;
        foreach (var t in Tabs) if (t.ConnectionId is null) SetTabConnection(t, conn.Id);
        StatusText = $"Added demo connection '{conn.Name}'. Press F5 to run.";
    }

    /// <summary>Background connect + schema warm so completion is ready before the first Run. Quiet on failure.</summary>
    private async void WarmConnection(EditorTabViewModel? tab)
    {
        if (tab is null) return;
        var info = EffectiveConnection(tab);
        if (info is null) return;
        try
        {
            var session = await _sessions.GetOrConnectAsync(info, CancellationToken.None);
            if (ReferenceEquals(SelectedTab, tab)) IsConnected = true;
            var snapshot = await _sessions.EnsureSchemaAsync(session, CancellationToken.None);
            if (ReferenceEquals(SelectedTab, tab))
                StatusText = snapshot is null
                    ? $"Connected to {info.Name}."
                    : $"Connected to {info.Name} · {snapshot.Tables.Count} tables.";
        }
        catch (ConnectionFailedException) { /* Run will surface the error explicitly */ }
        catch { /* completion warming must never disrupt the UI */ }
    }
}
