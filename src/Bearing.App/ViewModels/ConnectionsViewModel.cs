using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Bearing.App.Connections;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Data.Postgres;

namespace Bearing.App.ViewModels;

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

    /// <summary>The engines this build ships. Exposed for the connection editor, which is provider-driven:
    /// its picker and every field it renders come from here (<c>IDbProvider.ConnectionFields</c>).</summary>
    public IProviderRegistry Providers => _ctx.Providers;

    public ConnectionsViewModel(WorkspaceContext ctx)
    {
        _ctx = ctx;
        _ctx.SelectedTabChanged += OnSelectedTabChanged;
        // Reflect the real session state: any connect (explicit, or lazily from a query / schema warm) or
        // teardown (disconnect, idle sweep, expiry) re-derives the indicators, so they can't drift out of
        // sync. Both events are wired because they answer different questions — see OnServerLinkChanged.
        _ctx.Sessions.LiveChanged += OnSessionLiveChanged;
        _ctx.Sessions.LinkChanged += OnServerLinkChanged;
    }

    /// <summary>A session pool for one connection+database changed. The indicators no longer read pools
    /// (they read the server link — see <see cref="IsTabServerLinked"/>), so this is a cheap re-derive that
    /// exists for the ordering: a first connect raises LiveChanged and LinkChanged, and a lazy pool open on
    /// an already-linked server raises only this one, where nothing user-visible has moved. Fires possibly
    /// off-thread.</summary>
    private void OnSessionLiveChanged(SessionKey key)
        => Dispatcher.UIThread.Post(() => RefreshIndicators(key.ConnectionId));

    /// <summary>A connection gained or lost its server link — a first handshake, an explicit Disconnect, a
    /// connection edited/deleted, a credential expiring, a project close. This is the event the chain glyphs
    /// and the toolbar dot actually turn on, and it is why a query-driven connect lights the toolbar without
    /// the user touching the toggle, while an idle sweep no longer darkens it. Fires possibly off-thread.</summary>
    private void OnServerLinkChanged(Guid connectionId)
        => Dispatcher.UIThread.Post(() => RefreshIndicators(connectionId));

    /// <summary>Re-derive everything the given connection can affect. Only the connection is tested: every
    /// indicator is server-level now, and a tab pointed at another database of the same server is exactly a
    /// tab that must refresh with it.</summary>
    private void RefreshIndicators(Guid connectionId)
    {
        var tab = Selected;
        // Don't clobber an in-flight explicit connect's Connecting state with a spurious re-derive.
        if (tab?.ConnectionId == connectionId && State != ConnectionState.Connecting) SyncStateFromLink(tab);
        RefreshTabConnectionState(); // unconditional: SyncStateFromLink only fires it on a real change
        RefreshServerNodeState();
    }

    /// <summary>Push this tab's own server state onto every tab, so each tab header's beacon reports its own
    /// connection rather than the selected tab's. The selected tab shows Connecting while an explicit connect
    /// is in flight, so its beacon starts pulsing when the attempt starts rather than when it lands —
    /// matching the toolbar, which is showing Connecting at the same moment.</summary>
    private void RefreshTabConnectionState()
    {
        foreach (var t in Tabs) RefreshTabConnectionState(t);
    }

    private void RefreshTabConnectionState(EditorTabViewModel tab)
        => tab.ConnectionState = ReferenceEquals(tab, Selected) && State == ConnectionState.Connecting
            ? ConnectionState.Connecting
            : IsTabServerLinked(tab) ? ConnectionState.Connected : ConnectionState.Disconnected;

    /// <summary>Push "is this server linked" onto every schema-tree server row. Same question as the per-tab
    /// beacon now, and deliberately so — the node <i>is</i> the server, the databases are its children, and
    /// having the row and the tab beside it answer differently is what made the old model read as broken.
    /// Only ever Connected or Disconnected: Connecting belongs to an attempt, and an attempt belongs to a
    /// tab, not to a tree row.</summary>
    private void RefreshServerNodeState()
    {
        foreach (var node in ServerNodes)
            node.ConnectionState = _ctx.Sessions.IsLinked(node.Connection.Id)
                ? ConnectionState.Connected
                : ConnectionState.Disconnected;
    }

    /// <summary>True when this tab's <i>server</i> is linked — not when the pool for the database it happens
    /// to point at is warm. Postgres binds a pool to one database, so the old per-(connection, database)
    /// reading meant connecting on <c>app</c> and then picking <c>reporting</c> from the Database pill showed
    /// the tab as disconnected from a server it was demonstrably still authenticated to, while the schema
    /// tree's row for that same server stayed lit. The pool for the new database is opened lazily (or warmed
    /// by <see cref="SetTabDatabase"/>) and costs a handshake, but that is a latency detail, not a change in
    /// what the user is connected to.</summary>
    private bool IsTabServerLinked(EditorTabViewModel tab)
        => tab.ConnectionId is { } id && _ctx.Sessions.IsLinked(id);

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

    // ---- connection status (toolbar / status-bar indicator) -----------------------------------

    /// <summary>Server-link state of the selected tab's connection. Drives the toolbar and status-bar beacon,
    /// its label, and the colour of the power toggle beside it — all on the Status.* palette, never the
    /// connection's environment hue (see ConnectionStatusView).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(IsConnecting))]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyPropertyChangedFor(nameof(ToggleTip))]
    private ConnectionState _state = ConnectionState.Disconnected;

    public string StatusLabel => State switch
    {
        ConnectionState.Connected => "Connected",
        ConnectionState.Connecting => "Connecting…",
        _ => "Disconnected",
    };

    // IsConnecting / IsDisconnected drive the two style classes on the status view and the toggle; the
    // beacon reads State itself. There is deliberately no "IsLinked" any more — it existed only to pick
    // between the linked and broken chain glyphs, and the power toggle that replaced them is one mark in
    // every state.
    public bool IsConnecting => State == ConnectionState.Connecting;
    public bool IsDisconnected => State == ConnectionState.Disconnected;

    public string ToggleTip => State switch
    {
        ConnectionState.Connected => "Disconnect from server",
        ConnectionState.Connecting => "Cancel connecting",
        _ => "Connect to server",
    };

    /// <summary>Keep the context's (unbound, logic-facing) flag in lockstep with the observable state —
    /// one source of truth instead of the former ad-hoc <c>TryGet</c> assignments scattered per call site.</summary>
    partial void OnStateChanged(ConnectionState value)
    {
        _ctx.IsConnected = value == ConnectionState.Connected;
        RefreshTabConnectionState(); // Connecting/Cancel never touch the pool, so the glyphs need telling
        RefreshServerNodeState();
    }

    // Per-attempt cancellation + epoch: a cancelled or superseded connect must never flip to Connected.
    private CancellationTokenSource? _connectCts;
    private int _connectEpoch;

    /// <summary>Set <see cref="State"/> from whether the tab's <i>server</i> is linked right now (called on
    /// tab/connection/database changes, before a fresh connect flips it to Connecting).</summary>
    private void SyncStateFromLink(EditorTabViewModel? tab)
        => State = tab is not null && IsTabServerLinked(tab)
            ? ConnectionState.Connected
            : ConnectionState.Disconnected;

    /// <summary>Toolbar chain toggle: Connect when disconnected, Cancel while connecting, Disconnect when
    /// connected. Connect reuses <see cref="ConnectAsync"/>; disconnect drops <i>every</i> database's session
    /// on this connection, not just the selected tab's — the button says "disconnect from server", and the
    /// schema tree's server row lights for any live session on the connection, so a one-database evict would
    /// leave it linked immediately after the user pressed Disconnect (#54).
    /// <c>AllowConcurrentExecutions</c> is required: the connect keeps this command's task in flight, and
    /// without it the button would disable itself mid-connect — the user could never click it to Cancel.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleConnectionAsync()
    {
        var tab = Selected;
        switch (State)
        {
            case ConnectionState.Disconnected:
                if (tab is not null) await ConnectAsync(tab, isExplicit: true);
                break;
            case ConnectionState.Connecting:
                _connectCts?.Cancel();
                State = ConnectionState.Disconnected;
                break;
            case ConnectionState.Connected:
                if (tab?.ConnectionId is { } id)
                {
                    var name = _ctx.FindConnection(id)?.Name;
                    await _ctx.Sessions.EvictConnectionAsync(id);
                    State = ConnectionState.Disconnected;
                    _ctx.SetStatus(name is null ? "Disconnected." : $"Disconnected from {name}.");
                }
                else State = ConnectionState.Disconnected;
                break;
        }
    }

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
        get => Selected is { } tab ? _ctx.EffectiveConnection(tab)?.Database : null;
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
        // No eager connect on tab switch — just reflect whatever session already exists. Connecting is
        // driven by an explicit action (the Connect toggle, running a query, expanding the schema tree).
        SyncStateFromLink(tab);
        RefreshTabConnectionState(); // the "selected tab is connecting" arm above moved with the selection
        CrashReporter.Observe(RefreshTabDatabasesAsync(tab), "connections.refresh-databases");
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
        RefreshServerNodeState(); // the nodes are new objects, so their flags start false
    }

    public void ApplyConnectionDisplay(EditorTabViewModel tab)
    {
        var info = tab.ConnectionId is { } id ? _ctx.FindConnection(id) : null;
        tab.ConnectionDisplay = info?.Name;
        tab.ConnectionColor = info?.EnvironmentColor;
        tab.DatabaseName ??= info?.Database; // default the active DB to the connection's own
        RefreshTabConnectionState(tab);
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
            SyncStateFromLink(tab); // reflect the existing session; do not eagerly connect
            CrashReporter.Observe(RefreshTabDatabasesAsync(tab), "connections.refresh-databases");
        }
    }

    /// <summary>Point a tab at another database on its server. The old database's pool is left alone (§9.4);
    /// the new one's is opened in the background when the server is already linked — see
    /// <see cref="WarmDatabaseAsync"/> — so the tab is genuinely ready rather than merely claiming to be.</summary>
    public void SetTabDatabase(EditorTabViewModel tab, string database)
    {
        if (string.Equals(tab.DatabaseName, database, StringComparison.Ordinal)) return;
        tab.DatabaseName = database;
        // No glyph refresh here: the chain is server-level now, and moving between databases of the same
        // server cannot change it. That is the whole behaviour change.
        if (ReferenceEquals(tab, Selected))
        {
            OnPropertyChanged(nameof(SelectedTabDatabase));
            SyncStateFromLink(tab);
        }
        CrashReporter.Observe(WarmDatabaseAsync(tab), "connections.warm-database");
    }

    /// <summary>Open the pool for a tab's newly-chosen database, but only when its server is already linked.
    ///
    /// <para>This is not the connect-on-tab-switch that was deliberately removed. The user has already opted
    /// into this server and we hold its credential in memory, so this never prompts and never reaches a server
    /// they didn't ask for — it is the same authenticated conversation, on a second database. What it buys is
    /// honesty: with the indicators reading the server link, the tab claims to be connected the moment the
    /// pill changes, and this makes that claim true instead of aspirational. A failure is left to the tab's
    /// first query to report properly; if it was also the connection's last pool, the manager drops the link
    /// and the chain breaks on its own.</para>
    ///
    /// <para>Superseded attempts are cancelled, so clicking through five databases in the pill opens the fifth
    /// rather than racing all five.</para></summary>
    private async Task WarmDatabaseAsync(EditorTabViewModel tab)
    {
        if (tab.ConnectionId is not { } id || !_ctx.Sessions.IsLinked(id)) return;
        if (_ctx.EffectiveConnection(tab) is not { } target) return;
        if (_ctx.Sessions.TryGet(SessionKey.For(target)) is not null) return; // already warm

        var cts = new CancellationTokenSource();
        var prior = Interlocked.Exchange(ref _warmCts, cts);
        prior?.Cancel();
        prior?.Dispose();
        try { await _ctx.Sessions.GetOrConnectAsync(target, cts.Token); }
        catch (OperationCanceledException) { /* superseded by a later switch */ }
        catch (ConnectionFailedException) { /* the tab's first query reports this properly */ }
    }

    private CancellationTokenSource? _warmCts;

    /// <summary>Load the server's database list into <see cref="TabDatabases"/> for the given tab. Awaited
    /// through <see cref="CrashReporter.Observe(Task, string)"/> at every call site (it is fire-and-forget,
    /// triggered by tab/connection changes), so a preamble fault is logged instead of escaping unobserved.
    /// The offline fallback below is deliberate and stays swallowed.</summary>
    private async Task RefreshTabDatabasesAsync(EditorTabViewModel? tab)
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
    /// <remarks>A keyring that <i>errors</i> now raises rather than reporting "no password" (see
    /// <c>SecretToolSecretStore.GetPasswordAsync</c>). Here that must not stop the dialog from opening — the
    /// user can still edit the host or retype the password — so it opens with an empty password box and says
    /// why in the status bar. Silently showing an empty box is what this avoids: it looks like no password was
    /// ever stored, and saving over it would replace a good secret with nothing.</remarks>
    public async Task<string?> GetConnectionPasswordAsync(Guid id)
    {
        if (_ctx.Secrets is null) return null;
        try
        {
            return await _ctx.Secrets.GetPasswordAsync(id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _ctx.SetStatus($"Could not read the stored password: {SafeErrorText.Of(ex)}");
            return null;
        }
    }

    /// <summary>Add or replace a connection in the manifest and its password in the secret store.</summary>
    public async Task AddOrUpdateConnectionAsync(ConnectionInfo conn, string? password)
    {
        if (_ctx.Project is null) return;
        var list = _ctx.Project.Manifest.Connections;
        var idx = list.FindIndex(c => c.Id == conn.Id);
        var networkChanged = true;
        if (idx >= 0) { networkChanged = !SameNetwork(list[idx], conn); list[idx] = conn; }
        else list.Add(conn);

        var refusedSecret = false;
        try
        {
            if (_ctx.Secrets is not null && password is not null)
            {
                if (password.Length == 0) await _ctx.Secrets.DeleteAsync(conn.Id, CancellationToken.None);
                else await _ctx.Secrets.SetPasswordAsync(conn.Id, password, CancellationToken.None);
            }
            await _ctx.ProjectStore.SaveAsync(_ctx.Project, CancellationToken.None);
        }
        // A store with nowhere safe to put the password isn't a failure — the connection is still saved, and
        // the password will be asked for at connect time and kept in memory. Say that, then finish the save.
        catch (SecretStorageRefusedException)
        {
            refusedSecret = true;
            try { await _ctx.ProjectStore.SaveAsync(_ctx.Project, CancellationToken.None); }
            catch (Exception ex) { _ctx.SetStatus($"Saved connection but store failed: {SafeErrorText.Of(ex)}"); }
        }
        catch (Exception ex) { _ctx.SetStatus($"Saved connection but secret/store failed: {SafeErrorText.Of(ex)}"); }

        // A changed network target means the cached schema describes a different server, so drop it too —
        // eviction alone deliberately keeps it (that is what makes completion survive a mere disconnect).
        if (networkChanged) { _ctx.Sessions.InvalidateSchema(conn.Id); await _ctx.Sessions.EvictConnectionAsync(conn.Id); }
        _ctx.DefaultConnectionId ??= conn.Id;
        RefreshConnections();
        foreach (var t in Tabs) if (t.ConnectionId == conn.Id) ApplyConnectionDisplay(t);
        OnPropertyChanged(nameof(SelectedTabConnection));
        _ctx.SetStatus(refusedSecret
            ? $"Saved connection '{conn.Name}' — password not saved (no keyring); you'll be asked when connecting."
            : $"Saved connection '{conn.Name}'.");
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

        _ctx.Sessions.InvalidateSchema(id);   // the connection is gone; don't keep its catalog around
        await _ctx.Sessions.EvictConnectionAsync(id);   // every database on it, not just one
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
        _ctx.Sessions.InvalidateSchema(connectionId);   // this command's entire point is to re-read the catalog
        await _ctx.Sessions.EvictConnectionAsync(connectionId);   // every database's pool re-reads on next use

        var node = ServerNodes.FirstOrDefault(n => n.Connection.Id == connectionId);
        if (node is not null) await node.RefreshAsync();

        if (Selected?.ConnectionId == connectionId) CrashReporter.Observe(ConnectAsync(Selected, isExplicit: false), "connections.warm");
        _ctx.SetStatus("Schema metadata refreshed.");
    }

    private static bool SameNetwork(ConnectionInfo a, ConnectionInfo b)
        => a.ProviderId == b.ProviderId && a.Host == b.Host && a.Port == b.Port
           && a.Database == b.Database && a.User == b.User;

    /// <summary>Build a throwaway connection and test it (for the dialog's Test button); nothing is persisted.
    /// For an Entra connection the token is minted through the resolver (ignoring the box); prompt / stored
    /// connections test with the value typed into the dialog.</summary>
    public async Task<bool> TestConnectionAsync(ConnectionInfo info, string? password, CancellationToken ct)
    {
        var secret = password;
        if (info.CredentialKind == CredentialKind.EntraToken)
            secret = (await _ctx.Credentials.ResolveAsync(info, forceRefresh: true, ct)).Secret;

        var provider = _ctx.Providers.Get(info.ProviderId);
        var factory = provider.CreateConnectionFactory(info, secret);
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
            // The constant, not the string: the demo seed is Postgres because pagila is, and an id typed
            // out by hand is an id that can drift from the provider that has to resolve it. There is
            // deliberately no SQL Server equivalent of this seed.
            ProviderId = PostgresProvider.ProviderId,
            Host = host,
            Port = port,
            Database = database,
            User = user,
            Environment = "local",
            EnvironmentColor = "#7AA89F",
        };
        await AddOrUpdateConnectionAsync(conn, password);
        _ctx.DefaultConnectionId = conn.Id;
        foreach (var t in Tabs) if (t.ConnectionId is null) SetTabConnection(t, conn.Id);
        _ctx.SetStatus($"Added demo connection '{conn.Name}'. Press F5 to run.");
    }

    /// <summary>Establish the connection on an explicit trigger — the toolbar chain toggle
    /// (<paramref name="isExplicit"/> = true) or a metadata refresh (<paramref name="isExplicit"/> = false).
    /// There is no longer any connect-on-tab-switch; a query connects lazily through the execution path,
    /// and the <see cref="OnSessionLiveChanged"/> handler keeps the indicator in sync for those. Drives
    /// <see cref="State"/>: Connecting while the attempt runs, then Connected on success (+ schema warm +
    /// status) or Disconnected on failure. Also warms completion metadata. The attempt is cancellable and
    /// epoch-guarded, so a cancelled or superseded attempt is ignored and can never flip the dot to
    /// Connected. Only an explicit connect reports a failure status. Awaited through
    /// <see cref="CrashReporter.Observe(Task, string)"/> at its call sites (fire-and-forget), so a preamble
    /// fault is logged instead of escaping unobserved.</summary>
    private async Task ConnectAsync(EditorTabViewModel? tab, bool isExplicit)
    {
        if (tab is null) return;
        var info = _ctx.EffectiveConnection(tab);
        if (info is null) { SyncStateFromLink(tab); return; }

        var cts = new CancellationTokenSource();
        var prior = Interlocked.Exchange(ref _connectCts, cts);
        prior?.Cancel();
        prior?.Dispose();
        var epoch = ++_connectEpoch;
        State = ConnectionState.Connecting;

        try
        {
            var session = await _ctx.Sessions.GetOrConnectAsync(info, cts.Token);
            if (cts.IsCancellationRequested)
            {
                // Cancelled during the open, but the attempt still produced a live session (cancel raced
                // the connect completing) — tear down the one this attempt opened, so "Cancel" leaves nothing
                // pooled from it while any other database already connected on this server stays up.
                await _ctx.Sessions.EvictAsync(SessionKey.For(info));
                return;
            }
            if (!IsCurrentAttempt(epoch, tab)) return; // superseded by a newer attempt which now owns state
            State = ConnectionState.Connected;
            var snapshot = await _ctx.Sessions.EnsureSchemaAsync(session, cts.Token);
            if (cts.IsCancellationRequested || !IsCurrentAttempt(epoch, tab)) return;
            _ctx.SetStatus(snapshot is null
                ? $"Connected to {info.Name}."
                : $"Connected to {info.Name} · {snapshot.Tables.Count} tables.");
        }
        catch (OperationCanceledException) { /* cancelled — the cancel path already set Disconnected */ }
        catch (ConnectionFailedException)
        {
            // A cancelled attempt surfaces here too (the session manager wraps the cancellation), so bail
            // on cancellation to leave the deliberate Disconnected state and avoid a spurious failure status.
            if (cts.IsCancellationRequested || !IsCurrentAttempt(epoch, tab)) return;
            State = ConnectionState.Disconnected;
            if (isExplicit) _ctx.SetStatus($"Could not connect to {info.Name}.");
        }
        catch
        {
            // Unexpected preamble fault: fail closed to Disconnected, never disrupt the UI.
            if (!cts.IsCancellationRequested && IsCurrentAttempt(epoch, tab)) State = ConnectionState.Disconnected;
        }
    }

    /// <summary>A connect attempt's result may be applied only while it is still the latest attempt and the
    /// tab it targeted is still selected — otherwise a stale attempt could clobber a newer state.</summary>
    private bool IsCurrentAttempt(int epoch, EditorTabViewModel tab)
        => epoch == _connectEpoch && ReferenceEquals(Selected, tab);
}
