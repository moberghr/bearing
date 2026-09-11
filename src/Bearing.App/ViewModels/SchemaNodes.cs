using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Bearing.App.Connections;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Bearing.App.Workspace;
using Bearing.Core.Schema;
using Bearing.Sql;

namespace Bearing.App.ViewModels;

/// <summary>
/// One node in the sidebar schema tree. Children are loaded lazily the first time the node is
/// expanded (a placeholder child makes the expander arrow appear beforehand). Load runs on the UI
/// thread's sync context — awaits resume on the UI thread, so mutating <see cref="Children"/> after
/// an await is safe — and a failure replaces the children with an error node rather than throwing.
/// </summary>
public abstract partial class SchemaNodeViewModel : ObservableObject
{
    private bool _loaded;

    protected SchemaNodeViewModel(string glyph, string title, string? detail, bool hasChildren)
    {
        Glyph = glyph;
        _title = title;
        _detail = detail;
        HasChildren = hasChildren;
        if (hasChildren) Children.Add(new MessageNodeViewModel("", "Loading…"));
    }

    public string Glyph { get; }

    /// <summary>Row label. Settable (protected) so a node can be re-labelled in place rather than replaced:
    /// renaming a connection must not collapse the tree or throw away its loaded databases.</summary>
    public string Title { get => _title; protected set => SetProperty(ref _title, value); }
    private string _title;

    /// <summary>Dim second line — the server's <c>host:port</c>, a column's type. Settable for the same
    /// reason as <see cref="Title"/>.</summary>
    public string? Detail { get => _detail; protected set => SetProperty(ref _detail, value); }
    private string? _detail;

    /// <summary>What the detail means, when it is terse enough to need saying. Null on almost every row —
    /// a size or a type explains itself. It exists for the absences, which have to be short on the row and
    /// can afford a sentence under the pointer.</summary>
    public string? DetailTip { get => _detailTip; protected set => SetProperty(ref _detailTip, value); }
    private string? _detailTip;

    /// <summary>Whether the node can be expanded (drives the placeholder + the expander arrow).</summary>
    public bool HasChildren { get; }

    /// <summary>Whether this row is a database — for the context menu's size-ordering items (#76).</summary>
    public virtual bool IsDatabase => false;

    /// <summary>Whether this row is a sequence — for the context menu's copy-nextval items (#119).</summary>
    public virtual bool IsSequence => false;

    public ObservableCollection<SchemaNodeViewModel> Children { get; } = new();

    /// <summary>The row above this one, or null at the root. Set wherever children are attached, so a
    /// question asked of a deep row — which connection is this column's server? (#57) — can be answered by
    /// walking up rather than by every node type carrying its own copy of the connection.</summary>
    public SchemaNodeViewModel? Parent { get; private set; }

    /// <summary>The connection this row belongs to, found by walking up to the nearest server row. Null for
    /// a folder, and for the tree's own roots.</summary>
    public ConnectionInfo? OwningConnection
    {
        get
        {
            for (var n = this; n is not null; n = n.Parent)
                if (n is ServerNodeViewModel server) return server.Connection;
            return null;
        }
    }

    /// <summary>Attach a child and record the link. Used by every path that populates
    /// <see cref="Children"/>.</summary>
    protected void AddChild(SchemaNodeViewModel child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoading;

    /// <summary>True when the node's title matches the current type-ahead search (drives the highlight).</summary>
    [ObservableProperty] private bool _isMatch;

    /// <summary>True while this row is the one being dragged into a folder (#80). The row dims, so it is
    /// visible <em>what</em> is in flight — the platform owns the pointer during a drag, so the cursor
    /// cannot say it, and the drop highlight only says where it would land.</summary>
    [ObservableProperty] private bool _isDragging;

    /// <summary>True while this row is an editable box rather than a label. Only connection and folder rows
    /// ever set it — schema objects are named by the server — but it lives on the base so the sidebar's one
    /// data template can bind it without reflection, and so both row types rename the same way (#39).</summary>
    [ObservableProperty] private bool _isRenaming;

    /// <summary>The name being typed while <see cref="IsRenaming"/>.</summary>
    [ObservableProperty] private string _renameDraft = "";

    /// <summary>Start editing this row's name in place, seeded from what it is called now.</summary>
    public void BeginRename()
    {
        RenameDraft = Title;
        IsRenaming = true;
    }

    /// <summary>
    /// Tests a title against the sidebar's live type-ahead query. The search pass sets it on every loaded node,
    /// and <see cref="EnsureChildrenAsync"/> hands it down to children as they arrive — otherwise a row created
    /// *after* the search ran (a table's columns, loaded on first expand) came up unhighlighted while its
    /// siblings were tinted, because nothing had ever tested it. Null (or an empty query) means no highlight.
    /// </summary>
    internal Func<string, bool>? MatchTest { get; set; }

    /// <summary>Adopt the parent's live search and answer it immediately, for this node and anything it was
    /// constructed holding (a Views / Functions bucket arrives pre-populated).</summary>
    private void InheritSearch(SchemaNodeViewModel parent)
    {
        MatchTest = parent.MatchTest;
        IsMatch = this is not MessageNodeViewModel && (MatchTest?.Invoke(Title) ?? false);
        foreach (var child in Children) child.InheritSearch(this);
    }

    /// <summary>True only for the root server node (drives its context-menu items + double-tap).</summary>
    public virtual bool IsServer => false;

    /// <summary>True only for a connection folder (#80) — the one node type that is organisation rather
    /// than schema. Drives its own context-menu items and makes it a drop target.</summary>
    public virtual bool IsFolder => false;

    /// <summary>Hex environment colour washed across the whole row (server nodes only); null = no wash.
    /// It replaced a 9px leading dot, which read as a connection-state light next to the toolbar's
    /// (issue #45) — a row fill can't.</summary>
    public virtual string? RowAccentColor => null;

    /// <summary>True for nodes that represent a connectable server, so the row carries a beacon for
    /// <see cref="ConnectionState"/>. Every other node type leaves the slot empty.</summary>
    public virtual bool ShowsConnectionState => false;

    /// <summary>This node's server state — the beacon drawn on the row. Same question the tab headers and the
    /// toolbar answer, so the tree can no longer disagree with the tab beside it about whether the user is
    /// connected to a server. Declared on the base because the tree's single <c>TreeDataTemplate</c> binds
    /// against this type; only nodes with <see cref="ShowsConnectionState"/> render it. Kept in sync by
    /// <c>ConnectionsViewModel.RefreshServerNodeState</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStateTip))]
    private Bearing.App.Connections.ConnectionState _connectionState;

    /// <summary>Tooltip for the row's beacon. A string on the VM rather than a converter: there is one
    /// consumer and the wording is the whole logic.</summary>
    public string ConnectionStateTip => ConnectionState switch
    {
        Bearing.App.Connections.ConnectionState.Connected => "Connected",
        Bearing.App.Connections.ConnectionState.Connecting => "Connecting…",
        _ => "Not connected",
    };

    /// <summary>Resource key of a vector icon (Icon.*) shown instead of the text <see cref="Glyph"/>; null = use the glyph.</summary>
    public virtual string? IconKey => null;

    /// <summary>Hex stroke color for the vector icon.</summary>
    public virtual string IconColorHex => "#8B95A1";

    /// <summary>Relations and routines can render a definition; other nodes cannot.</summary>
    public virtual bool CanShowDefinition => false;
    public virtual string DefinitionTitle => $"{Title} — definition";
    public virtual Task<string> LoadDefinitionAsync(CancellationToken ct) => Task.FromResult("");

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) _ = EnsureChildrenAsync();
    }

    /// <summary>
    /// Which load of this node's children is current.
    /// <para>
    /// Bumped by every (re)load, and captured by anything that appends a group after an await
    /// (<see cref="OnChildrenAttached"/>'s reads). "Refresh metadata" clears <see cref="Children"/> and
    /// re-runs the hook on the <em>same</em> node, so without this a read still in flight from the previous
    /// load landed its group on the rebuilt children and the new read added a second one — two Roles groups,
    /// or up to a dozen duplicated groups on a database. The window is the length of a catalog query.
    /// </para>
    /// </summary>
    protected int LoadGeneration { get; private set; }

    /// <summary>Whether <paramref name="generation"/> is still the current load. False = discard the result.</summary>
    protected bool IsCurrentLoad(int generation) => generation == LoadGeneration;

    /// <summary>Load children once; idempotent. Public so tests can await the load directly.</summary>
    public async Task EnsureChildrenAsync()
    {
        if (_loaded || !HasChildren) return;
        _loaded = true;
        LoadGeneration++;
        IsLoading = true;
        try
        {
            var kids = await LoadChildrenAsync();
            Children.Clear();
            foreach (var k in kids)
            {
                k.InheritSearch(this);
                AddChild(k);
            }
            OnChildrenAttached();
        }
        catch (Exception ex)
        {
            Children.Clear();
            // SchemaBrowser opens its own connections, so a connect-time failure here can quote a whole
            // connection string — redact before it reaches the tree (§1.1).
            Children.Add(new MessageNodeViewModel("⚠", SafeErrorText.Of(ex)));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Swap in a fresh set of children without re-reading anything (#132). For a node that can arrange the
    /// data it already holds more than one way — the generation is <b>not</b> bumped, because nothing was
    /// invalidated: a late read still in flight is still about this load and may still land.
    /// </summary>
    /// <summary>
    /// Add one child to a node whose children are already attached, priming it the way the initial load
    /// does. The late reads used to call <c>Children.Add</c> straight, which left the appended group with no
    /// parent and outside the type-ahead's reach.
    /// </summary>
    protected void AppendChild(SchemaNodeViewModel child)
    {
        child.InheritSearch(this);
        AddChild(child);
    }

    protected void ReplaceChildren(IReadOnlyList<SchemaNodeViewModel> next)
    {
        Children.Clear();
        foreach (var child in next)
        {
            child.InheritSearch(this);
            AddChild(child);
        }
    }

    /// <summary>Discard loaded children and reload them if the node is currently expanded (else on next expand).</summary>
    public async Task RefreshAsync()
    {
        if (!HasChildren) return;
        var wasExpanded = IsExpanded;
        _loaded = false;
        // Bumped here too, not only in EnsureChildrenAsync: a refresh of a *collapsed* node reloads nothing,
        // and an in-flight append from the previous load must still be discarded.
        LoadGeneration++;
        Children.Clear();
        Children.Add(new MessageNodeViewModel("", "Loading…"));
        if (wasExpanded) await EnsureChildrenAsync();
    }

    protected abstract Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync();

    /// <summary>
    /// Called once this node's children are attached and visible.
    /// <para>
    /// The hook exists because a late read that <b>adds</b> rows cannot be started from
    /// <see cref="LoadChildrenAsync"/>: <see cref="EnsureChildrenAsync"/> clears <see cref="Children"/> after
    /// that returns, so anything appended in the meantime is thrown away — a race whose outcome depended on
    /// whether the catalog answered faster than the tree rebuilt. A read that only <em>relabels</em> existing
    /// rows (the sizes, #76) was immune and could stay where it was.
    /// </para>
    /// </summary>
    protected virtual void OnChildrenAttached() { }

    protected static IReadOnlyList<SchemaNodeViewModel> None => Array.Empty<SchemaNodeViewModel>();
}

/// <summary>A leaf node used for the "Loading…" placeholder and for load-error messages.</summary>
public sealed class MessageNodeViewModel : SchemaNodeViewModel
{
    public MessageNodeViewModel(string glyph, string message) : base(glyph, message, null, hasChildren: false) { }
    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);
}

/// <summary>
/// A connection folder (#80): organisation, not schema. Its members are handed in already built — the
/// connections it holds are the cached <see cref="ServerNodeViewModel"/>s, so re-filing or re-filtering
/// rebuilds the folder rows without touching what those have loaded.
/// <para>A <see cref="SchemaNodeViewModel"/> rather than a parallel type so it inherits the sidebar's one
/// data template, the type-ahead's flatten/highlight pass, and the expansion binding. It contributes no
/// <see cref="RowAccentColor"/>: a folder is where you filed a connection, an environment is how dangerous
/// it is, and giving folders a hue of their own is how those two channels would start reading as each
/// other (#45).</para>
/// </summary>
public sealed partial class ConnectionFolderNodeViewModel : SchemaNodeViewModel
{
    public ConnectionFolderNodeViewModel(string path, int count, IReadOnlyList<SchemaNodeViewModel> members)
        // hasChildren: false — nothing to load lazily, so no "Loading…" placeholder; the members below are
        // what give the row its expander.
        : base("▸", FolderPath.Name(path) ?? path, count > 0 ? count.ToString() : null, hasChildren: false)
    {
        Path = path;
        Count = count;
        foreach (var m in members) AddChild(m);
    }

    /// <summary>Full "/"-separated path, which is the folder's identity — the row's title is only its last
    /// segment, and two folders can share that.</summary>
    public string Path { get; }

    /// <summary>Connections anywhere beneath, so a collapsed folder still says how much it is hiding.</summary>
    public int Count { get; }

    /// <summary>Whether <see cref="Count"/> is worth rendering. An empty folder hides nothing, and a bare
    /// "0" on the row reads as a value rather than as an absence.</summary>
    public bool HasConnections => Count > 0;

    public override bool IsFolder => true;
    public override string? IconKey => "Icon.Folder";
    public override string IconColorHex => "#E6C384";

    /// <summary>True while a dragged connection is over this folder, so the row says where the drop lands.
    /// The move worked before the Scripts tree grew this, it just gave no sign of its target (#37).</summary>
    [ObservableProperty] private bool _isDropTarget;

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);
}

/// <summary>Root node: a saved connection = a server. Expands to the databases on that server.</summary>
public sealed partial class ServerNodeViewModel : SchemaNodeViewModel
{
    private readonly ISchemaBrowser _browser;

    /// <param name="mode">Passed on to each database node; see <see cref="DatabaseNodeViewModel"/>.</param>
    public ServerNodeViewModel(
        ConnectionInfo connection, ISchemaBrowser browser, Func<SchemaTreeMode>? mode = null)
        : base("⛁", connection.Name, ConnectionEndpoint.HostPort(connection), hasChildren: true)
    {
        Connection = connection;
        _browser = browser;
        _mode = mode;
    }

    private readonly Func<SchemaTreeMode>? _mode;

    public ConnectionInfo Connection { get; private set; }

    /// <summary>
    /// Take on an edited <see cref="ConnectionInfo"/> that targets the same server, keeping this node — its
    /// expansion, its loaded databases, and everything under them. A rename or a change of environment colour
    /// describes the same server, and rebuilding the node for one collapsed the tree and re-read the catalog
    /// for nothing. Callers decide what counts as "same server"
    /// (<c>ConnectionsViewModel.SameNetwork</c>); this only re-labels.
    /// </summary>
    public void Adopt(ConnectionInfo edited)
    {
        Connection = edited;
        Title = edited.Name;
        Detail = ConnectionEndpoint.HostPort(edited);
        OnPropertyChanged(nameof(RowAccentColor));
    }

    public override bool IsServer => true;
    public override string? RowAccentColor => Connection.EnvironmentColor;
    public override bool ShowsConnectionState => true;
    public override string? IconKey => "Icon.Connections"; // server / postgres
    public override string IconColorHex => "#6FA6E2";

    protected override async Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync()
    {
        var databases = await _browser.GetDatabasesAsync(Connection, CancellationToken.None);
        var children = databases
            .Select(db => (SchemaNodeViewModel)new DatabaseNodeViewModel(
                Connection, db, isConnected: string.Equals(db, Connection.Database, StringComparison.Ordinal),
                _browser, _mode))
            .ToList();

        return children;
    }

    /// <summary>
    /// The two reads that follow the databases onto the server row.
    /// <para>
    /// Sizes because <c>pg_database_size</c> stats a whole directory per database (#76), and the roles
    /// because they are a second catalog read answering a question nobody asks on every expand — and, like
    /// the per-database kinds, a group appended after the fact must start here rather than from
    /// <c>LoadChildrenAsync</c>, whose result <c>EnsureChildrenAsync</c> clears.
    /// </para>
    /// </summary>
    protected override void OnChildrenAttached()
    {
        _ = FillDatabaseSizesAsync(Children.ToList());
        _ = FillRolesAsync();
        _ = FillTablespacesAsync();
    }

    /// <summary>
    /// A <b>Tablespaces</b> group under this server — the other cluster-wide kind, so it belongs beside
    /// Roles rather than under a database. Silent on failure, like every other late read here.
    /// </summary>
    private async Task FillTablespacesAsync()
    {
        var generation = LoadGeneration;
        IReadOnlyList<SchemaObjectInfo> tablespaces;
        try { tablespaces = await _browser.GetTablespacesAsync(Connection, CancellationToken.None); }
        catch (Exception) { return; }
        if (tablespaces.Count == 0 || !IsCurrentLoad(generation)) return;

        Children.Add(new SchemaGroupNodeViewModel(
            "Tablespaces", "Icon.Database",
            tablespaces
                .Select(t => (SchemaNodeViewModel)new SchemaObjectNodeViewModel(t, "", "Icon.Database", "▤"))
                .ToList()));
        TablespacesLoaded?.Invoke();
    }

    /// <summary>Raised once the Tablespaces group has been appended. For tests, like the others here.</summary>
    internal Action? TablespacesLoaded { get; set; }

    /// <summary>
    /// Append a <b>Roles</b> group under this server (#120) — cluster-wide, so it belongs here and not under
    /// a database. Silent on failure, like the sizes: a role list is an addition, and a server that will not
    /// show it must not turn the expanded tree into an error message.
    /// </summary>
    private async Task FillRolesAsync()
    {
        var generation = LoadGeneration;
        IReadOnlyList<RoleInfo> roles;
        try { roles = await _browser.GetRolesAsync(Connection, CancellationToken.None); }
        catch (Exception) { return; }
        if (roles.Count == 0 || !IsCurrentLoad(generation)) return;

        var members = roles
            .Select(r => (SchemaNodeViewModel)new RoleNodeViewModel(Connection, r, _browser))
            .ToList();
        Children.Add(new SchemaGroupNodeViewModel("Roles", "Icon.Role", members));
        RolesLoaded?.Invoke();
    }

    /// <summary>Raised once the Roles group has been appended. For tests, like the size reads' events.</summary>
    internal Action? RolesLoaded { get; set; }

    /// <summary>
    /// Label each database row with its size on the server. Silent on failure and per row on unknown: a
    /// database the user cannot connect to reports null rather than raising, and one they cannot reach must
    /// not cost the sizes of the rest.
    /// </summary>
    private async Task FillDatabaseSizesAsync(IReadOnlyList<SchemaNodeViewModel> children)
    {
        IReadOnlyList<DatabaseSize> sizes;
        try { sizes = await _browser.GetDatabaseSizesAsync(Connection, CancellationToken.None); }
        catch (Exception ex)
        {
            // Swallowed for the user, because a size is best-effort and must not break the tree (§5.2/§5.6)
            // — but not swallowed outright. A failed read otherwise looks exactly like a server with nothing
            // to report, which is §4.7's "a silently-swallowed read hides a query bug". Logged rather than
            // surfaced: this is not worth a toast, and it is worth a line in the crash log.
            CrashLog.Write("schema.database-sizes", ex);
            return;
        }

        var byName = new Dictionary<string, DatabaseSize>(StringComparer.Ordinal);
        foreach (var size in sizes) byName[size.Database] = size;

        // Every database the read answered for is labelled, including the ones it answered "no size" for:
        // that null is a fact about the role's privileges, not a gap to leave blank (#133).
        foreach (var database in children.OfType<DatabaseNodeViewModel>())
            if (byName.TryGetValue(database.Database, out var size))
                database.ApplySize(size.Bytes);

        DatabaseSizesLoaded?.Invoke();
    }

    /// <summary>Raised once the database sizes have been applied. For tests; nothing in the app waits.</summary>
    internal Action? DatabaseSizesLoaded { get; set; }
}

/// <summary>
/// A database on the server. Expands to its <b>tables</b> — the default schema's unprefixed and first, then
/// the other schemas as <c>schema.name</c> — with views and functions tucked into collapsed buckets after
/// them, so opening a database shows what queries actually start from instead of hundreds of rows.
/// </summary>
public sealed class DatabaseNodeViewModel : SchemaNodeViewModel
{
    private readonly ConnectionInfo _connection;
    private readonly string _database;
    private readonly ISchemaBrowser _browser;

    /// <param name="mode">How this database arranges its children (#132). Read on every arrange rather than
    /// captured, so the panel's toggle reaches a node built at any time.</param>
    public DatabaseNodeViewModel(
        ConnectionInfo connection, string database, bool isConnected, ISchemaBrowser browser,
        Func<SchemaTreeMode>? mode = null)
        : base("🗄", database, isConnected ? "connected" : null, hasChildren: true)
    {
        _connection = connection;
        _database = database;
        _browser = browser;
        _mode = mode;
        _connectedLabel = isConnected ? "connected" : null;
    }

    private readonly Func<SchemaTreeMode>? _mode;

    public override string? IconKey => "Icon.Database";
    public override string IconColorHex => "#5FC9AD";

    /// <summary>The database this row is for, so a server-level size read can match it by name.</summary>
    internal string Database => _database;

    /// <summary>
    /// Label this row with the database's size (#76), keeping whatever else the detail said. Leading with the
    /// size for the same reason a relation row does — the panel ellipsizes, and a truncated number is worse
    /// than none.
    /// </summary>
    internal void ApplySize(long? bytes)
    {
        var rest = string.IsNullOrEmpty(_connectedLabel) ? null : _connectedLabel;
        var size = bytes is { } value ? ByteSize.Format(value) : NoSizeText;
        Detail = rest is null ? size : $"{size} · {rest}";
        DetailTip = bytes is null ? NoSizeTip : null;
    }

    /// <summary>
    /// How a database with no readable size reads (#133). Blank was the bug: it is indistinguishable from a
    /// size that has not arrived yet (the read is late by design, #76), from a read that failed, and from a
    /// database that genuinely has none.
    /// <para>
    /// The wording says only what was checked. <c>pg_database_size</c> is called solely where
    /// <c>has_database_privilege(datname, 'CONNECT')</c> says it will work, so a null means the role cannot
    /// connect to that database — "not visible" is the honest reading of that and asserts no cause beyond it.
    /// Not <c>0 B</c>, which is a lie; not "unknown", which invites the reader to assume a failure; not a bare
    /// dash, which is as silent as the blank it replaces. §1.7's rule, one row type later: absences are typed.
    /// </para>
    /// </summary>
    private const string NoSizeText = "size not visible";

    private const string NoSizeTip =
        "The connected role has no CONNECT privilege on this database, so its size can't be read.";

    private readonly string? _connectedLabel;

    /// <summary>Lets the tree's one context menu show the size-ordering items on a database row only — the
    /// same shape as <c>IsServer</c>, which the server-only items already bind.</summary>
    public override bool IsDatabase => true;

    /// <summary>
    /// The two reads that happen <em>after</em> the tree is on screen, never before.
    /// <para>
    /// Sizes because <c>pg_total_relation_size</c> stats files per relation, so waiting for it would make
    /// every expand as slow as the biggest database (#76); the #119 kinds because a dozen more catalog reads on
    /// the render path would make every expand slower by all of them, to answer questions only someone who
    /// scrolls past the tables is asking. The rows relabel themselves and the groups append themselves when
    /// each lands.
    /// </para>
    /// </summary>
    protected override void OnChildrenAttached()
    {
        _ = FillSizesAsync(Children.ToList());
        _ = FillObjectKindsAsync(_defaultSchema);
    }

    /// <summary>The default schema this database was expanded with, so the late kind groups label their rows
    /// the same way the relations did.</summary>
    private string _defaultSchema = "public";

    protected override async Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync()
    {
        var objects = await _browser.GetObjectsAsync(_connection, _database, CancellationToken.None);
        _snapshot = objects.Snapshot;
        _snapshotForKinds = objects.Snapshot;
        _routines = objects.Routines;
        _defaultSchema = SchemaObjectLabel.DefaultSchemaOf(objects.Snapshot.SearchPath);

        // A partition is not a sibling of its parent: its rows *are* the parent's, so a partitioned table
        // with a hundred children would otherwise be a hundred rows of noise with nothing linking them. They
        // become the parent's children instead, and the parent's row says how many.
        //
        // The whole map travels into each node rather than that node's own child list, so a partition that is
        // itself partitioned (range then list) can nest its own children. Indexed once, because this runs
        // before the tree renders (see PartitionMap).
        _partitions = PartitionMap.Of(objects.Snapshot);

        return Arrange();
    }

    // What the reads returned, kept so either shape can be built from it without asking again (#132).
    private ISchemaSnapshot? _snapshot;
    private IReadOnlyList<RoutineInfo> _routines = [];
    private PartitionMap? _partitions;
    private DatabaseObjectKinds? _kinds;
    private readonly Dictionary<long, RelationSize> _sizes = new();

    /// <summary>
    /// Read live rather than stored, so a database node built after the user changed the mode cannot be
    /// arranged the old way. Storing it meant every construction site had to remember to push the current
    /// value, which is a bug waiting for the next one that forgets.
    /// </summary>
    private SchemaTreeMode Mode => _mode?.Invoke() ?? SchemaTreeMode.Simple;

    /// <summary>
    /// Re-arrange the children for <paramref name="mode"/> without re-reading anything (#132).
    /// <para>
    /// Expansion below this row is not preserved: the two shapes are different nodes, and pretending a
    /// relation three levels down is the same row as one inline is how the load race of section 9.8 gets
    /// re-introduced.
    /// </para>
    /// </summary>
    internal void ApplyMode()
    {
        if (_snapshot is null) return;   // not expanded yet: the next load picks the new mode up by itself
        ReplaceChildren(Arrange());
    }

    /// <summary>The children for the current mode, built from what has been read so far. The single place
    /// either shape is decided, so a toggle and a first expand cannot disagree.</summary>
    private List<SchemaNodeViewModel> Arrange()
    {
        var children = Mode == SchemaTreeMode.Full ? ArrangeFull() : ArrangeSimple();

        _loadOrder.Clear();
        for (var i = 0; i < children.Count; i++)
            if (children[i] is RelationNodeViewModel relation) _loadOrder[relation] = i;

        return children;
    }

    /// <summary>
    /// Simple mode: the shape the tree has always had, with the long tail behind one bucket.
    /// <para>
    /// Relations inline is the whole point of it: one click from a database to a table, which is what nearly
    /// every expand is for (section 9.9a). What changes is only the run of up to thirteen sibling groups
    /// after them, which is what made the row count unreadable.
    /// </para>
    /// </summary>
    private List<SchemaNodeViewModel> ArrangeSimple()
    {
        var snapshot = _snapshot!;
        var (tables, views) = SplitRelations(snapshot, _defaultSchema);
        var children = Ordered(tables);

        // A Schemas level, for the database that has more than one. Additive here, as it has been: the
        // inline list is still the primary view, and this is the level the tree had no way to express.
        // Skipped for a single-schema database, where it would hold exactly what is already on screen.
        var schemas = SchemaTreeShape.SchemasOf(snapshot, _routines, _kinds, _defaultSchema);
        if (schemas.Count > 1)
            children.Add(new SchemaGroupNodeViewModel(
                SchemaTreeShape.Schemas, "Icon.Folder", schemas.Select(SchemaFolder).ToList()));

        if (views.Count > 0) children.Add(new SchemaGroupNodeViewModel("Views", "Icon.View", Ordered(views)));
        AddRoutineGroup(children, "Functions", RoutineKind.Function, RoutineKind.Aggregate, RoutineKind.Window);
        AddRoutineGroup(children, "Procedures", RoutineKind.Procedure);

        if (OtherObjects() is { } other) children.Add(other);
        return children;
    }

    /// <summary>
    /// Full mode: schemas first, each holding a group per kind, with what belongs to the database rather
    /// than to any schema in one Administer bucket.
    /// </summary>
    private List<SchemaNodeViewModel> ArrangeFull()
    {
        var snapshot = _snapshot!;
        var schemas = SchemaTreeShape.SchemasOf(snapshot, _routines, _kinds, _defaultSchema);
        var children = new List<SchemaNodeViewModel>();

        // One schema collapses the level rather than skipping it. Skipping is what simple mode does, and it
        // works there because the relations are already inline; here the schema level is the only route to
        // them, so a skipped level would be a database with nothing in it.
        if (schemas.Count == 1)
            children.AddRange(SchemaGroups(schemas[0]));
        else if (schemas.Count > 1)
            children.Add(new SchemaGroupNodeViewModel(
                SchemaTreeShape.Schemas, "Icon.Folder", schemas.Select(SchemaFolder).ToList()));

        if (Administer() is { } administer) children.Add(administer);
        return children;
    }

    private SchemaNodeViewModel SchemaFolder(string schema)
        => new SchemaFolderNodeViewModel(
            _connection, _database, schema, _snapshot!, _routines, _browser,
            Mode == SchemaTreeMode.Full ? () => SchemaGroups(schema) : null,
            ApplyCachedSizes);

    /// <summary>The groups inside one schema, in SchemaTreeShape's order. Built here rather than in the
    /// schema node so the collapsed-level case and the folder case produce the same rows.</summary>
    internal IReadOnlyList<SchemaNodeViewModel> SchemaGroups(string schema)
    {
        var snapshot = _snapshot!;
        var groups = new List<SchemaNodeViewModel>();

        // The schema's own relations, unqualified: passing the schema as the default is what drops the
        // prefix, since every row here is already known to be in it.
        var (tables, views) = SplitRelations(snapshot, schema, only: schema);
        groups.Add(Bucket("Tables", "Icon.Table", Ordered(tables)));
        groups.Add(Bucket("Views", "Icon.View", Ordered(views)));
        groups.Add(Bucket("Functions", "Icon.Function", RoutinesIn(schema, schema,
            RoutineKind.Function, RoutineKind.Aggregate, RoutineKind.Window)));
        groups.Add(Bucket("Procedures", "Icon.Function", RoutinesIn(schema, schema, RoutineKind.Procedure)));

        // The kind groups exist only once the late read has landed. Until then the schema shows what the
        // snapshot already knew, and gains the rest when the read arrives (see FillObjectKindsAsync).
        if (_kinds is { } kinds)
            foreach (var kind in SchemaTreeShape.Kinds)
                if (kind.Placement == SchemaKindPlacement.PerSchema)
                    groups.Add(Bucket(kind.Title, kind.IconKey, KindMembers(kind, kinds, schema, schema)));

        return groups;
    }

    /// <summary>Simple mode's one bucket for the long tail, or null while its read is still out. Never an
    /// empty group, which would read as "this database has none" (#132).</summary>
    private SchemaNodeViewModel? OtherObjects()
    {
        // Nothing read yet, or a database with no long tail at all. The em-dash is for a kind that is empty
        // *inside* the bucket, where the fixed set of rows is what makes it scannable; a bucket standing for
        // nothing whatever is just a row that says so, which is the empty group #132 asks us not to render.
        if (_kinds is not { } kinds || SchemaTreeShape.LongTailCount(kinds) == 0) return null;
        var groups = SchemaTreeShape.Kinds
            .Select(k => (SchemaNodeViewModel)Bucket(k.Title, k.IconKey, KindMembers(k, kinds, null, _defaultSchema)))
            .ToList();
        return new SchemaGroupNodeViewModel(
            SchemaTreeShape.OtherObjects, "Icon.Folder", groups,
            SchemaTreeShape.CountText(SchemaTreeShape.LongTailCount(kinds)));
    }

    /// <summary>Full mode's bucket for what belongs to the database rather than to a schema.</summary>
    private SchemaNodeViewModel? Administer()
    {
        if (_kinds is not { } kinds) return null;
        var groups = SchemaTreeShape.Kinds
            .Where(k => k.Placement == SchemaKindPlacement.Administer)
            .Select(k => (SchemaNodeViewModel)Bucket(k.Title, k.IconKey, KindMembers(k, kinds, null, _defaultSchema)))
            .ToList();
        return groups.Any(g => g.Children.Count > 0)
            ? new SchemaGroupNodeViewModel(SchemaTreeShape.Administer, "Icon.Folder", groups)
            : null;
    }

    /// <summary>A group row that exists even when it holds nothing: its count then reads as an em-dash,
    /// which is the difference between "asked, there are none" and "not asked yet" (#132).</summary>
    private static SchemaGroupNodeViewModel Bucket(
        string title, string icon, IReadOnlyList<SchemaNodeViewModel> members)
        => new(title, icon, members, SchemaTreeShape.CountText(members.Count));

    /// <summary>One kind's rows, optionally narrowed to a schema. The node types are the ones each kind
    /// already had: a regrouping must not cost a sequence its nextval items or a policy its colour.</summary>
    private List<SchemaNodeViewModel> KindMembers(
        SchemaTreeKind kind, DatabaseObjectKinds kinds, string? only, string defaultSchema)
    {
        bool Wanted(string schema) => only is null || string.Equals(schema, only, StringComparison.Ordinal);

        List<Sortable> Objects(IReadOnlyList<SchemaObjectInfo> items) => items
            .Where(x => Wanted(x.Schema))
            .Select(x => Entry(x.Schema, x.Name, 0, defaultSchema,
                new SchemaObjectNodeViewModel(x, defaultSchema, kind.IconKey, kind.Glyph)))
            .ToList();

        List<SchemaNodeViewModel> AsRead(IReadOnlyList<SchemaObjectInfo> items)
            => Objects(items).Select(x => x.Node).ToList();

        return kind.Title switch
        {
            "Sequences" => Ordered(kinds.Sequences.Where(q => Wanted(q.Schema))
                .Select(q => Entry(q.Schema, q.Name, 0, defaultSchema,
                    new SequenceNodeViewModel(q, defaultSchema))).ToList()),
            "Types" => Ordered(kinds.Types.Where(t => Wanted(t.Schema))
                .Select(t => Entry(t.Schema, t.Name, 0, defaultSchema,
                    new TypeNodeViewModel(t, defaultSchema))).ToList()),
            // A policy has no schema of its own: it takes the one belonging to the table it guards.
            "Policies" => Ordered(kinds.Policies
                .Where(x => Wanted(SchemaOfTable(x.TableId)))
                .Select(x => Entry("", RelationDetailText.PolicyTitle(x, _snapshotForKinds), 0, defaultSchema,
                    new PolicyNodeViewModel(x, _snapshotForKinds))).ToList()),
            // Extensions have no schema to rank by, so they keep name order rather than going through Ordered.
            "Extensions" => kinds.Extensions
                .Select(e => (SchemaNodeViewModel)new ExtensionNodeViewModel(e)).ToList(),
            // The name-plus-a-line kinds keep the server's own order, as they always have.
            "Publications" => AsRead(kinds.Publications),
            "Subscriptions" => AsRead(kinds.Subscriptions),
            "Foreign servers" => AsRead(kinds.ForeignServers),
            "Event triggers" => AsRead(kinds.EventTriggers),
            "Casts" => AsRead(kinds.Casts),
            "Collations" => Ordered(Objects(kinds.Collations)),
            "Operators" => Ordered(Objects(kinds.Operators)),
            "Operator classes" => Ordered(Objects(kinds.OperatorClasses)),
            "Text search" => Ordered(Objects(kinds.TextSearchConfigs)),
            _ => [],
        };
    }

    private string SchemaOfTable(long tableId)
        => _snapshot?.Tables.FirstOrDefault(t => t.Id == tableId)?.Schema ?? "";

    /// <summary>The database's relations split into tables and views, optionally narrowed to one schema.</summary>
    private (List<Sortable> Tables, List<Sortable> Views) SplitRelations(
        ISchemaSnapshot snapshot, string defaultSchema, string? only = null)
    {
        var tables = new List<Sortable>();
        var views = new List<Sortable>();
        var source = only is null
            ? snapshot.Tables
            : snapshot.Tables.Where(t => string.Equals(t.Schema, only, StringComparison.Ordinal)).ToList();

        foreach (var t in _partitions!.TopLevel(source))
        {
            var node = new RelationNodeViewModel(
                _connection, _database, t, snapshot, _browser, defaultSchema, _partitions);
            if (_sizes.TryGetValue(t.Id, out var size)) node.ApplySize(size);
            (IsViewLike(t.Kind) ? views : tables).Add(
                Entry(t.Schema, t.Name, RelationRank(t.Kind), defaultSchema, node));
        }
        return (tables, views);
    }

    private List<SchemaNodeViewModel> RoutinesIn(string? only, string defaultSchema, params RoutineKind[] kinds)
        => Ordered(_routines
            .Where(r => kinds.Contains(r.Kind)
                        && (only is null || string.Equals(r.Schema, only, StringComparison.Ordinal)))
            .Select(r => Entry(r.Schema, r.Name, RoutineRank(r.Kind), defaultSchema,
                new RoutineNodeViewModel(_connection, _database, r, _browser, defaultSchema)))
            .ToList());

    private void AddRoutineGroup(List<SchemaNodeViewModel> children, string title, params RoutineKind[] kinds)
    {
        var members = RoutinesIn(null, _defaultSchema, kinds);
        if (members.Count > 0)
            children.Add(new SchemaGroupNodeViewModel(title, "Icon.Function", members));
    }

    /// <summary>Label relation rows from the sizes already read, for rows built after the read landed. In
    /// full mode that is every row under a schema the user opens later (#132).</summary>
    internal void ApplyCachedSizes(IEnumerable<SchemaNodeViewModel> nodes)
    {
        if (_sizes.Count == 0) return;
        foreach (var relation in nodes.OfType<RelationNodeViewModel>())
            if (_sizes.TryGetValue(relation.TableId, out var size)) relation.ApplySize(size);
    }

    /// <summary>
    /// How this database's relations are ordered (#76). Name is the default; size answers the question a
    /// tree sorted by name cannot — "which table is eating the disk".
    /// </summary>
    public enum RelationOrder
    {
        Name,
        Size,
    }

    private RelationOrder _order = RelationOrder.Name;

    /// <summary>
    /// Where each relation row sat when the database was expanded — the <c>Ordered()</c> ranking, which is
    /// schema-rank then schema then kind then name. Kept so "sort by name" restores exactly that rather than
    /// an approximation of it.
    /// </summary>
    private readonly Dictionary<RelationNodeViewModel, int> _loadOrder = new();

    /// <summary>
    /// Re-order the relation rows, in place.
    /// <para>
    /// By size means <b>total</b> size, descending, biggest first: the question is always "what is largest",
    /// never "what is smallest". Relations whose size has not arrived — or that have none, like a view — sort
    /// last rather than as zero, so a pending read does not look like an empty table.
    /// </para>
    /// <para>
    /// Only the rows directly under the database move. The Views and Functions buckets keep their own order:
    /// they are collapsed by default, and reordering inside a bucket the user has not opened is motion
    /// nobody asked for.
    /// </para>
    /// </summary>
    public void SetRelationOrder(RelationOrder order)
    {
        _order = order;
        var relations = Children.OfType<RelationNodeViewModel>().ToList();
        if (relations.Count == 0) return;

        var sorted = order == RelationOrder.Size
            ? relations
                .OrderByDescending(r => r.Size is not null)
                .ThenByDescending(r => r.Size?.TotalBytes ?? 0)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ToList()
            // Back to the order the rows were *loaded* in, not merely alphabetical by title. Sorting on Title
            // alone interleaved the default schema's bare names among the qualified ones and dropped the kind
            // ranking, so the item labelled as the default sort could not actually restore it.
            : relations.OrderBy(r => _loadOrder.GetValueOrDefault(r, int.MaxValue)).ToList();

        // Moved rather than removed and re-added: these nodes hold expanded children, and replacing them
        // would collapse whatever the user had open (the same reason ApplySize re-labels in place).
        for (var target = 0; target < sorted.Count; target++)
        {
            var current = Children.IndexOf(sorted[target]);
            if (current != target) Children.Move(current, target);
        }
    }

    /// <summary>
    /// Read every relation's size and label the rows with it. Deliberately fire-and-forget and silent on
    /// failure: sizes are a nicety, and a permission error or a slow catalog must not turn an expanded tree
    /// into an error message.
    /// </summary>
    private async Task FillSizesAsync(IReadOnlyList<SchemaNodeViewModel> children)
    {
        var generation = LoadGeneration;
        IReadOnlyList<RelationSize> sizes;
        try { sizes = await _browser.GetRelationSizesAsync(_connection, _database, CancellationToken.None); }
        catch (Exception) { return; }
        // Relabelling discarded rows is harmless, but SetRelationOrder below reorders the *live* Children,
        // so a stale read must not reach it.
        if (!IsCurrentLoad(generation)) return;

        // Kept, not just applied. In full mode a relation under a schema the user has not opened yet does
        // not exist, so a one-shot pass has nothing to label — those rows ask for their size as they are
        // built instead (#132, ApplyCachedSizes).
        _sizes.Clear();
        foreach (var size in sizes) _sizes[size.TableId] = size;

        foreach (var relation in Relations(children))
            if (_sizes.TryGetValue(relation.TableId, out var size)) relation.ApplySize(size);

        // If the user asked for size order before the sizes existed, this is when it can be honoured.
        if (_order == RelationOrder.Size) SetRelationOrder(RelationOrder.Size);
        SizesLoaded?.Invoke();
    }

    /// <summary>Raised once the size read has re-labelled the rows. For tests — nothing in the app waits on
    /// it, which is the point of loading them late.</summary>
    internal Action? SizesLoaded { get; set; }

    /// <summary>
    /// Read the per-database object kinds (#119) and append a collapsed group per non-empty kind.
    /// <para>
    /// Appended after the relations rather than interleaved: the tables are what nearly every expand is for,
    /// and rows arriving above them later would shift the list under a user who is already reading it. Silent
    /// on failure, like the sizes — these groups are an addition, and a permission error must not turn an
    /// expanded tree into an error message.
    /// </para>
    /// </summary>
    private async Task FillObjectKindsAsync(string defaultSchema)
    {
        var generation = LoadGeneration;
        DatabaseObjectKinds kinds;
        try { kinds = await _browser.GetDatabaseObjectKindsAsync(_connection, _database, CancellationToken.None); }
        catch (Exception) { return; }
        if (!IsCurrentLoad(generation)) return;

        _kinds = kinds;

        if (Mode == SchemaTreeMode.Full)
        {
            // Full mode re-arranges rather than appends. Its kind groups do not hang off the database row at
            // all — they live inside each schema — and a single-schema database has no schema row to refresh,
            // so appending would leave those groups permanently missing. The cost is the same as a toggle,
            // and bounded to the moment between the tree rendering and this read landing.
            ReplaceChildren(Arrange());
        }
        else if (OtherObjects() is { } bucket)
        {
            // Simple mode appends, which is what keeps it non-disruptive: a user who opened a table in that
            // same moment keeps it open. One row now, where this used to add a run of up to thirteen.
            AppendChild(bucket);
        }

        ObjectKindsLoaded?.Invoke();
    }

    /// <summary>The snapshot this database was expanded with, so a policy row can name the table it is on.
    /// Held rather than re-fetched: the kinds arrive after the relations, and the snapshot that produced
    /// them is the one they belong to.</summary>
    private ISchemaSnapshot? _snapshotForKinds;

    /// <summary>Raised once the #119 groups have been appended. For tests, like <see cref="SizesLoaded"/>.</summary>
    internal Action? ObjectKindsLoaded { get; set; }

    /// <summary>
    /// Every relation row under this database that currently exists — inline, inside a bucket, or inside a
    /// schema that has been opened.
    /// <para>
    /// Recursive because full mode puts relations three levels down (#132), and deliberately limited to what
    /// is already materialised: a schema folder loads on first expand, so walking into an unopened one would
    /// mean building rows nobody asked for. The rows that do not exist yet are labelled as they are built.
    /// </para>
    /// </summary>
    private static IEnumerable<RelationNodeViewModel> Relations(IEnumerable<SchemaNodeViewModel> children)
        => children.SelectMany(Flatten).OfType<RelationNodeViewModel>();

    private static IEnumerable<SchemaNodeViewModel> Flatten(SchemaNodeViewModel node)
    {
        yield return node;
        // Only through groups. A relation's own children are its columns, and a schema folder that has never
        // been expanded holds nothing but a "Loading…" placeholder.
        if (node is not SchemaGroupNodeViewModel) yield break;
        foreach (var child in node.Children)
            foreach (var descendant in Flatten(child))
                yield return descendant;
    }

    private Sortable Entry(string schema, string name, int rank, string defaultSchema, SchemaNodeViewModel node)
        => new(SchemaObjectLabel.SchemaRank(schema, defaultSchema), schema, rank, name, node);

    /// <summary>Default schema first, then the other schemas clustered by name — their rows carry a
    /// <c>schema.</c> prefix, so interleaving them would make the prefixes look random. Kind then name
    /// within each schema, as before.</summary>
    private static List<SchemaNodeViewModel> Ordered(List<Sortable> entries) => entries
        .OrderBy(x => x.SchemaRank)
        .ThenBy(x => x.Schema, StringComparer.OrdinalIgnoreCase)
        .ThenBy(x => x.Rank)
        .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        .Select(x => x.Node)
        .ToList();

    private readonly record struct Sortable(int SchemaRank, string Schema, int Rank, string Name, SchemaNodeViewModel Node);

    /// <summary>Views and materialized views are the "look it up when you need it" half of the relation list.</summary>
    private static bool IsViewLike(RelationKind kind) => kind is RelationKind.View or RelationKind.MaterializedView;

    private static int RelationRank(RelationKind kind) => kind switch
    {
        RelationKind.Table => 0,
        RelationKind.Partitioned => 0,
        RelationKind.ForeignTable => 1,
        RelationKind.View => 2,
        RelationKind.MaterializedView => 3,
        _ => 1,
    };

    private static int RoutineRank(RoutineKind kind) => kind == RoutineKind.Procedure ? 5 : 4;
}

/// <summary>
/// A collapsed bucket of secondary objects (views, functions) under a database. Its members are handed in
/// already built — they come from the loaded snapshot, so there is no I/O to defer — which is also what lets
/// the sidebar's type-ahead search reach into a bucket that has never been expanded.
/// </summary>
public sealed class SchemaGroupNodeViewModel : SchemaNodeViewModel
{
    private readonly string _iconKey;

    public SchemaGroupNodeViewModel(string title, string iconKey, IReadOnlyList<SchemaNodeViewModel> members)
        : this(title, iconKey, members, members.Count.ToString(CultureInfo.InvariantCulture)) { }

    /// <param name="count">
    /// What the row shows instead of the member count. Used by the grouped shapes (#132), where a bucket
    /// stands for more than it directly holds — Other objects counts the whole long tail rather than its
    /// thirteen sub-groups — and where a group that came back empty says so with an em-dash rather than
    /// being left out. A group whose read has not landed is never built at all, so the mark can only ever
    /// mean "asked, and there are none".
    /// </param>
    public SchemaGroupNodeViewModel(
        string title, string iconKey, IReadOnlyList<SchemaNodeViewModel> members, string count)
        // hasChildren: false — nothing to load lazily, so no placeholder; the members below give the
        // expander its arrow. Collapsed on construction is the point of the bucket.
        : base("▸", title, count, hasChildren: false)
    {
        _iconKey = iconKey;
        foreach (var m in members) AddChild(m);
    }

    public override string? IconKey => _iconKey;

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);
}

/// <summary>A relation (table/view/…). Expands to its columns; can show a definition (view SQL or DDL).</summary>
public sealed class RelationNodeViewModel : SchemaNodeViewModel
{
    private readonly ConnectionInfo _connection;
    private readonly string _database;
    private readonly TableInfo _table;
    private readonly ISchemaSnapshot _snapshot;
    private readonly ISchemaBrowser _browser;

    /// <param name="partitions">
    /// The snapshot's whole partition hierarchy, so this node can nest its own partitions <em>and</em> hand
    /// the map on to them — a partition can itself be partitioned. Null for a caller with none to give.
    /// </param>
    public RelationNodeViewModel(
        ConnectionInfo connection, string database, TableInfo table, ISchemaSnapshot snapshot, ISchemaBrowser browser,
        string defaultSchema, PartitionMap? partitions = null)
        : base(Glyphs(table.Kind),
            SchemaObjectLabel.Title(table.Schema, table.Name, defaultSchema),
            Describe(table, defaultSchema, partitions),
            hasChildren: true)
    {
        _connection = connection;
        _database = database;
        _table = table;
        _snapshot = snapshot;
        _browser = browser;
        _defaultSchema = defaultSchema;
        _partitions = partitions ?? PartitionMap.Empty;
    }

    private readonly PartitionMap _partitions;

    /// <summary>The row's own detail, with a partition count where there is one.</summary>
    private static string Describe(TableInfo table, string defaultSchema, PartitionMap? partitions)
    {
        var detail = SchemaObjectLabel.Detail(KindLabel(table.Kind), table.Schema, defaultSchema);
        var count = partitions?.ChildrenOf(table.Id).Count ?? 0;
        return count > 0 ? $"{detail} · {RelationDetailText.Partitions(count)}" : detail;
    }

    private readonly string _defaultSchema;

    private bool IsViewLike => _table.Kind is RelationKind.View or RelationKind.MaterializedView;

    public override bool CanShowDefinition => true;

    /// <summary>This relation's id, so a size read can find the row it belongs to.</summary>
    internal long TableId => _table.Id;

    /// <summary>The relation's own schema and name — what a reveal matches on (#117). Not the
    /// <see cref="SchemaNodeViewModel.Title"/>, which is a label: a relation in the default schema shows as
    /// <c>payment</c> and one elsewhere as <c>reporting.payment</c>.</summary>
    internal string SchemaName => _table.Schema;

    /// <inheritdoc cref="SchemaName"/>
    internal string RelationName => _table.Name;

    /// <summary>Which database this relation is in, so a reveal can confirm it landed on the right one.</summary>
    internal string Database => _database;

    /// <summary>What this relation costs on disk, once a size read has answered. Null until then, and for a
    /// view, which has no storage of its own.</summary>
    internal RelationSize? Size { get; private set; }

    /// <summary>
    /// Attach a size, re-labelling the row in place (#76).
    /// <para>
    /// In place rather than by rebuilding: the sizes arrive after the tree is on screen, and replacing nodes
    /// would collapse whatever the user had expanded while they were waiting. <c>Detail</c> is settable for
    /// exactly this reason.
    /// </para>
    /// </summary>
    internal void ApplySize(RelationSize size)
    {
        Size = size;
        Detail = SchemaObjectLabel.WithSize(
            SchemaObjectLabel.Detail(KindLabel(_table.Kind), _table.Schema, _defaultSchema),
            size);
    }

    /// <summary>
    /// Columns inline, then a folder per other kind of thing a table has (#46).
    /// <para>
    /// Columns stay inline rather than going behind a <c>Columns</c> folder: they are what nearly every
    /// expand is for, and a folder in front of them would add a click to the common case to tidy the rare
    /// one. Constraints, keys, references, indexes and triggers are folders, and an empty one is left out — a
    /// table with no triggers should not have to say so.
    /// </para>
    /// <para>
    /// The two foreign-key directions get separate folders, which is the part worth having: outgoing answers
    /// "what does this row point at?", incoming answers "what breaks if I delete it?". Both come out of the
    /// snapshot, so they cost nothing; only constraints, indexes and triggers need the round trip, and if it
    /// fails the columns and the key folders are still there with the reason beside them.
    /// </para>
    /// </summary>
    protected override async Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync()
    {
        var children = new List<SchemaNodeViewModel>();
        var (outgoing, incoming) = RelationDetailText.SplitByDirection(_snapshot, _table.Id);

        // Read *before* the columns are built, because a column row now shows its default, identity and
        // comment — and those come from this read. The failure path still yields the columns: `details`
        // falls back to Empty, so they render with their type alone rather than not at all.
        TableDetails details;
        SchemaNodeViewModel? failure = null;
        try
        {
            details = await _browser.GetTableDetailsAsync(_connection, _database, _table.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            details = TableDetails.Empty;
            // SafeErrorText, not ex.Message: this read opens a connection, and a connect- or parse-time
            // Npgsql failure quotes the whole connection string — password included — into whatever shows it
            // (§1.1). The same reason EnsureChildrenAsync scrubs its own failures.
            failure = new MessageNodeViewModel(
                "⚠", $"Couldn't read indexes and constraints: {SafeErrorText.Of(ex)}");
        }

        foreach (var column in _snapshot.ColumnsOf(_table.Id))
            children.Add(new ColumnNodeViewModel(column, details.ColumnAt(column.Ordinal)));

        // The relation's own comment, on its row: the schema's documentation, which the tree could not show
        // at all before. Applied in place, because the row already exists by now.
        if (details.Comment is { } comment) Detail = RelationDetailText.WithComment(Detail ?? "", comment);

        // Constraints minus the foreign keys: those are the Foreign Keys folder, and listing a key twice
        // under one table makes both counts lie.
        var constraints = details.Constraints.Where(c => c.Kind != ConstraintKind.ForeignKey).ToList();

        Folder("Constraints", "Icon.Constraint", constraints
            .Select(c => (SchemaNodeViewModel)new ConstraintNodeViewModel(_snapshot, _table.Id, c)));
        Folder("Foreign Keys", "Icon.ForeignKey", outgoing
            .Select(fk => (SchemaNodeViewModel)new ForeignKeyNodeViewModel(_snapshot, fk, incoming: false)));
        Folder("References", "Icon.Reference", incoming
            .Select(fk => (SchemaNodeViewModel)new ForeignKeyNodeViewModel(_snapshot, fk, incoming: true)));
        Folder("Indexes", "Icon.Index", details.Indexes
            .Select(i => (SchemaNodeViewModel)new IndexNodeViewModel(_snapshot, _table.Id, i, i.SizeBytes)));
        Folder("Triggers", "Icon.Trigger", details.Triggers
            .Select(t => (SchemaNodeViewModel)new TriggerNodeViewModel(t)));
        // Beside the constraints and triggers (#119), because this is where you are standing when a query
        // returns fewer rows than you expect — the one thing RLS does that nothing else in the tree explains.
        // No snapshot, so the row is the policy's name alone: which table it is on is implied by the folder
        // it sits in, and repeating it spends horizontal space a narrow panel does not have. The
        // per-database group passes one, because there two policies of the same name on different tables
        // would otherwise be indistinguishable.
        Folder("Policies", "Icon.Policy", details.Policies
            .Select(p => (SchemaNodeViewModel)new PolicyNodeViewModel(p)));
        Folder("Rules", "Icon.Rule", details.Rules
            .Select(r => (SchemaNodeViewModel)new RuleNodeViewModel(r)));
        // Last, because a partition is a whole table of its own and the folders above describe *this* one.
        // The map is passed on, so a sub-partition nests under its own parent rather than being unreachable.
        Folder("Partitions", "Icon.Partition", _partitions.ChildrenOf(_table.Id)
            .Select(p => (SchemaNodeViewModel)new RelationNodeViewModel(
                _connection, _database, p, _snapshot, _browser, _defaultSchema, _partitions)));

        if (failure is not null) children.Add(failure);
        return children;

        void Folder(string title, string icon, IEnumerable<SchemaNodeViewModel> members)
        {
            var list = members.ToList();
            if (list.Count > 0) children.Add(new SchemaGroupNodeViewModel(title, icon, list));
        }
    }

    /// <summary>
    /// A view's SQL, or a table's DDL — which now carries its constraints and indexes, the hole the
    /// generator's own note admitted to (#46). The read can fail (no server, no permission); the columns are
    /// still worth showing, so the DDL comes out either way.
    /// </summary>
    public override async Task<string> LoadDefinitionAsync(CancellationToken ct)
    {
        if (IsViewLike)
            return await _browser.GetViewDefinitionAsync(_connection, _database, _table.Id, ct);

        TableDetails details;
        try
        {
            details = await _browser.GetTableDetailsAsync(_connection, _database, _table.Id, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            details = TableDetails.Empty;
        }
        var ddl = TableDdlGenerator.CreateTable(_table, _snapshot, details);
        // The fuller breakdown goes here rather than on the row: heap / indexes / toast / rows has room in a
        // definition view and would not fit on one tight tree line (#71, #76).
        return Size is { } size ? ddl + "\n" + SchemaObjectLabel.SizeBreakdown(size) : ddl;
    }

    private static string KindLabel(RelationKind kind) => kind switch
    {
        RelationKind.Table => "table",
        RelationKind.View => "view",
        RelationKind.MaterializedView => "materialized view",
        RelationKind.ForeignTable => "foreign table",
        RelationKind.Partitioned => "partitioned table",
        _ => "table",
    };

    private static string Glyphs(RelationKind kind) => kind switch
    {
        RelationKind.View => "◨",
        RelationKind.MaterializedView => "◫",
        RelationKind.ForeignTable => "▤",
        _ => "▦",
    };
}

/// <summary>A stored routine (function/procedure/…). Leaf; can show its <c>CREATE …</c> source.</summary>
public sealed class RoutineNodeViewModel : SchemaNodeViewModel
{
    private readonly ConnectionInfo _connection;
    private readonly string _database;
    private readonly RoutineInfo _routine;
    private readonly ISchemaBrowser _browser;

    public RoutineNodeViewModel(
        ConnectionInfo connection, string database, RoutineInfo routine, ISchemaBrowser browser, string defaultSchema)
        : base(GlyphFor(routine.Kind),
            SchemaObjectLabel.Title(routine.Schema, routine.Name, defaultSchema),
            RelationDetailText.WithComment(
                SchemaObjectLabel.Detail(KindLabel(routine.Kind), routine.Schema, defaultSchema),
                routine.Comment),
            hasChildren: false)
    {
        _connection = connection;
        _database = database;
        _routine = routine;
        _browser = browser;
    }

    public override bool CanShowDefinition => true;

    public override Task<string> LoadDefinitionAsync(CancellationToken ct)
        => _browser.GetRoutineDefinitionAsync(_connection, _database, _routine.Id, ct);

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);

    private static string GlyphFor(RoutineKind kind) => kind == RoutineKind.Procedure ? "▷" : "ƒ";

    private static string KindLabel(RoutineKind kind) => kind switch
    {
        RoutineKind.Procedure => "procedure",
        RoutineKind.Aggregate => "aggregate",
        RoutineKind.Window => "window",
        _ => "function",
    };
}

/// <summary>
/// A sequence (#119). Leaf: its whole state fits on the detail line, and there is nothing under it.
/// </summary>
public sealed class SequenceNodeViewModel : SchemaNodeViewModel
{
    public SequenceNodeViewModel(SequenceInfo sequence, string defaultSchema)
        : base("№",
            SchemaObjectLabel.Title(sequence.Schema, sequence.Name, defaultSchema),
            RelationDetailText.Sequence(sequence),
            hasChildren: false)
        => Sequence = sequence;

    /// <summary>The sequence itself, for the context menu's copy items.</summary>
    public SequenceInfo Sequence { get; }

    /// <summary>Lets the tree's one context menu offer the sequence-only items on a sequence row, the same
    /// shape <see cref="IsServer"/> and <see cref="IsDatabase"/> already use.</summary>
    public override bool IsSequence => true;

    /// <summary>
    /// <c>select nextval('schema.name')</c> — the statement you actually want when you have found a sequence,
    /// quoted so a mixed-case or keyword name survives the paste.
    /// </summary>
    public string NextvalSql => $"select nextval('{Quoted}')";

    /// <summary>
    /// <c>select setval('schema.name', &lt;value&gt;)</c>, seeded with where the sequence is now.
    /// <para>
    /// Deliberately <em>not</em> executed from the tree, and not offered pre-advanced: setting a sequence is
    /// how you break a primary key, so this hands over the statement to be read and run through the ordinary
    /// path — write guard included — rather than doing it behind a menu click.
    /// </para>
    /// </summary>
    public string SetvalSql
        => $"select setval('{Quoted}', {Sequence.LastValue?.ToString(CultureInfo.InvariantCulture) ?? "1"})";

    /// <summary>Quoted only where the bare form would not round-trip, so the common case reads as
    /// <c>public.payment_id_seq</c> rather than as a wall of quotes — and a mixed-case or keyword name still
    /// survives the paste.</summary>
    private string Quoted
        => $"{PgIdentifier.QuoteIfNeeded(Sequence.Schema)}.{PgIdentifier.QuoteIfNeeded(Sequence.Name)}";

    public override string? IconKey => "Icon.Sequence";
    public override string IconColorHex => "#7FB4CA";

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);
}

/// <summary>
/// A user-defined type (#119): an enum's labels, a domain's base type and checks, a composite's attributes.
/// <para>
/// The detail is the server's own rendering, not something reassembled here — an enum's labels have to come
/// out in <c>enumsortorder</c> and a domain's checks through <c>pg_get_constraintdef</c>, for the same
/// reason <see cref="ConstraintInfo.Definition"/> is the server's text.
/// </para>
/// </summary>
public sealed class TypeNodeViewModel : SchemaNodeViewModel
{
    private readonly TypeInfo _type;

    public TypeNodeViewModel(TypeInfo type, string defaultSchema)
        : base("τ",
            SchemaObjectLabel.Title(type.Schema, type.Name, defaultSchema),
            RelationDetailText.UserType(type),
            hasChildren: false)
        => _type = type;

    public override string? IconKey => "Icon.Type";
    public override string IconColorHex => "#957FB8";

    /// <summary>Its definition is already in hand, so showing it costs no round trip.</summary>
    public override bool CanShowDefinition => _type.Detail.Length > 0;

    public override Task<string> LoadDefinitionAsync(CancellationToken ct)
        => Task.FromResult(RelationDetailText.TypeDdl(_type));

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);
}

/// <summary>An installed extension (#119): version and schema, and nothing else to ask of it.</summary>
public sealed class ExtensionNodeViewModel : SchemaNodeViewModel
{
    public ExtensionNodeViewModel(ExtensionInfo extension)
        : base("⊞", extension.Name, RelationDetailText.Extension(extension), hasChildren: false) { }

    public override string? IconKey => "Icon.Extension";
    public override string IconColorHex => "#98BB6C";

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);
}

/// <summary>
/// A row-level security policy (#119). Shown under the table it applies to and in the per-database group;
/// the row says which command it covers, whether it permits or restricts, and to whom.
/// </summary>
public sealed class PolicyNodeViewModel : SchemaNodeViewModel
{
    private readonly PolicyInfo _policy;

    public PolicyNodeViewModel(PolicyInfo policy, ISchemaSnapshot? snapshot = null)
        : base("⛨", RelationDetailText.PolicyTitle(policy, snapshot), RelationDetailText.Policy(policy),
            hasChildren: false)
        => _policy = policy;

    public override string? IconKey => "Icon.Policy";

    /// <summary>Restrictive policies are drawn in the warning colour: they can only ever <em>remove</em>
    /// access, so one appearing where you did not expect it is the more alarming find.</summary>
    public override string IconColorHex => _policy.Permissive ? "#7E9CD8" : "#DCA561";

    /// <summary>The expressions, which are the whole content of a policy and too long for a detail line.</summary>
    public override bool CanShowDefinition => _policy.Using is not null || _policy.WithCheck is not null;

    public override Task<string> LoadDefinitionAsync(CancellationToken ct)
        => Task.FromResult(RelationDetailText.PolicyDefinition(_policy));

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);
}

/// <summary>
/// A role (#120). Expands to what it may do on the connection's own database — which is a narrower question
/// than the role itself, and the rows say so.
/// <para>
/// Read-only by design. Generating <c>GRANT</c> / <c>REVOKE</c> is not offered: a mistake there is a
/// production incident, and the write guard has no lexer for ACLs (§1.2).
/// </para>
/// </summary>
public sealed class RoleNodeViewModel : SchemaNodeViewModel
{
    private readonly ConnectionInfo _connection;
    private readonly RoleInfo _role;
    private readonly ISchemaBrowser _browser;

    public RoleNodeViewModel(ConnectionInfo connection, RoleInfo role, ISchemaBrowser browser)
        : base(role.CanLogin ? "☉" : "◍", role.Name, RelationDetailText.Role(role), hasChildren: true)
    {
        _connection = connection;
        _role = role;
        _browser = browser;
    }

    public override string? IconKey => "Icon.Role";

    /// <summary>A superuser is drawn in the warning colour: it is the one attribute on this list that makes
    /// every other privilege question moot, so it should not read like the rest.</summary>
    public override string IconColorHex => _role.IsSuperuser ? "#DCA561" : "#8B95A1";

    /// <summary>The role's own attributes as text, which is all a role "is" — there is no DDL to render and
    /// nothing to fetch.</summary>
    public override bool CanShowDefinition => true;

    public override string DefinitionTitle => $"{_role.Name} — role";

    public override Task<string> LoadDefinitionAsync(CancellationToken ct)
        => Task.FromResult(RelationDetailText.RoleSummary(_role, _connection.Database));

    protected override async Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync()
    {
        var children = new List<SchemaNodeViewModel>();

        // Memberships first: they come from the role list already in hand, so they are there even if the
        // grant read fails — and a role's group membership is often the whole explanation of its privileges.
        if (_role.MemberOf.Count > 0)
            children.Add(new SchemaGroupNodeViewModel(
                "Member of", "Icon.Role",
                _role.MemberOf.Select(m => (SchemaNodeViewModel)new MessageNodeViewModel("◍", m)).ToList()));

        RoleGrants grants;
        try
        {
            grants = await _browser.GetRoleGrantsAsync(
                _connection, _connection.Database, _role.Name, CancellationToken.None);
        }
        catch (Exception ex)
        {
            children.Add(new MessageNodeViewModel("⚠", $"Couldn't read grants: {SafeErrorText.Of(ex)}"));
            return children;
        }

        if (!grants.Visible)
        {
            // Said rather than shown as an empty list: "no grants" and "you may not find out" are different
            // answers, and rendering the second as the first is how a privilege screen misleads (§1.1).
            children.Add(new MessageNodeViewModel("⚠", "Not visible to this role"));
            return children;
        }

        if (grants.Grants.Count == 0)
        {
            children.Add(new MessageNodeViewModel(
                "·", $"No grants on {_connection.Database}"));
            return children;
        }

        children.Add(new SchemaGroupNodeViewModel(
            $"Grants on {_connection.Database}", "Icon.Policy",
            grants.Grants.Select(g => (SchemaNodeViewModel)new RoleGrantNodeViewModel(g)).ToList()));
        return children;
    }
}

/// <summary>One object's grants for a role (#120) — the privileges spelled out, never the acl shorthand.</summary>
public sealed class RoleGrantNodeViewModel : SchemaNodeViewModel
{
    public RoleGrantNodeViewModel(RoleGrant grant)
        : base("·", grant.Object, string.Join(", ", grant.Privileges), hasChildren: false) { }

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);
}

/// <summary>A column of a relation. Leaf; shows type + PK / NOT NULL.</summary>
public sealed class ColumnNodeViewModel : SchemaNodeViewModel
{
    /// <param name="detail">
    /// The column's default, identity, generated expression, collation and comment, when they have been
    /// read. Null on the path where the per-table read failed — the column is still worth showing with its
    /// type alone.
    /// </param>
    public ColumnNodeViewModel(ColumnInfo column, ColumnDetail? detail = null)
        : base(column.IsPrimaryKey ? "🔑" : "·", column.Name, RelationDetailText.Column(column, detail),
            hasChildren: false)
    { }

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);
}

/// <summary>
/// One schema, under the database's <b>Schemas</b> group. Expands to that schema's relations and routines.
/// <para>
/// Lazy on purpose. Its children are built from the snapshot on first expand rather than up front, so a
/// database with two thousand relations does not pay for a second set of nodes it may never open — the
/// inline list above already holds one. Nothing here does I/O; the snapshot is already in hand.
/// </para>
/// <para>
/// Names its objects <em>unqualified</em>, unlike the inline list: inside a row that says <c>reporting</c>,
/// repeating the schema on every child is the noise this level exists to remove.
/// </para>
/// </summary>
public sealed class SchemaFolderNodeViewModel : SchemaNodeViewModel
{
    private readonly ConnectionInfo _connection;
    private readonly string _database;
    private readonly string _schema;
    private readonly ISchemaSnapshot _snapshot;
    private readonly IReadOnlyList<RoutineInfo> _routines;
    private readonly ISchemaBrowser _browser;

    private readonly Func<IReadOnlyList<SchemaNodeViewModel>>? _groups;
    private readonly Action<IEnumerable<SchemaNodeViewModel>>? _applySizes;

    /// <param name="groups">
    /// In full mode, the grouped children this schema should show (Tables, Views, Functions, …), built by the
    /// database node so both the collapsed-single-schema case and the folder case produce the same rows.
    /// Null in simple mode, where this row keeps the flat relation-then-routine list it has always had.
    /// </param>
    /// <param name="applySizes">
    /// Hands the freshly built rows back to the database node so it can label them from a size read that has
    /// already landed (#132). Without it, a relation under a schema opened after that read would never get a
    /// size: the size pass relabels rows that exist, and in full mode these do not exist until now.
    /// </param>
    public SchemaFolderNodeViewModel(
        ConnectionInfo connection,
        string database,
        string schema,
        ISchemaSnapshot snapshot,
        IReadOnlyList<RoutineInfo> routines,
        ISchemaBrowser browser,
        Func<IReadOnlyList<SchemaNodeViewModel>>? groups = null,
        Action<IEnumerable<SchemaNodeViewModel>>? applySizes = null)
        : base("◫", schema, Describe(snapshot, routines, schema), hasChildren: true)
    {
        _connection = connection;
        _database = database;
        _schema = schema;
        _snapshot = snapshot;
        _routines = routines;
        _browser = browser;
        _groups = groups;
        _applySizes = applySizes;
    }

    public override string? IconKey => "Icon.Folder";
    public override string IconColorHex => "#7AA89F";

    /// <summary>How much is in here, so a collapsed row says whether it is worth opening.</summary>
    private static string Describe(ISchemaSnapshot snapshot, IReadOnlyList<RoutineInfo> routines, string schema)
    {
        var relations = snapshot.Tables.Count(t => string.Equals(t.Schema, schema, StringComparison.Ordinal));
        var callable = routines.Count(r => string.Equals(r.Schema, schema, StringComparison.Ordinal));
        var parts = new List<string>();
        if (relations > 0) parts.Add(relations == 1 ? "1 relation" : $"{relations} relations");
        if (callable > 0) parts.Add(callable == 1 ? "1 routine" : $"{callable} routines");
        return string.Join(" · ", parts);
    }

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync()
    {
        // Full mode hands the grouped children down from the database node, which is the only place that
        // holds the late object-kind read. Still no I/O either way: a schema is a filter over a snapshot the
        // database already has, which is what lets a database with two thousand relations stay lazy (9.9a).
        if (_groups is not null)
        {
            var grouped = _groups();
            _applySizes?.Invoke(grouped.SelectMany(g => g.Children));
            return Task.FromResult(grouped);
        }

        var children = new List<SchemaNodeViewModel>();

        // Unqualified: `_schema` is passed as the "default" so SchemaObjectLabel drops the prefix. Partitions
        // are nested here too, for the reason they are nested above — and the map is indexed once and handed
        // on, so a sub-partition is reachable and the parent lookup is not a scan per relation.
        var partitions = PartitionMap.Of(_snapshot);

        foreach (var table in partitions
            .TopLevel(_snapshot.Tables.Where(t => string.Equals(t.Schema, _schema, StringComparison.Ordinal)))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            children.Add(new RelationNodeViewModel(
                _connection, _database, table, _snapshot, _browser, _schema, partitions));
        }

        foreach (var routine in _routines
            .Where(r => string.Equals(r.Schema, _schema, StringComparison.Ordinal))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            children.Add(new RoutineNodeViewModel(_connection, _database, routine, _browser, _schema));
        }

        _applySizes?.Invoke(children);
        return Task.FromResult<IReadOnlyList<SchemaNodeViewModel>>(children);
    }
}

/// <summary>A rule on a relation. Leaf; shows the server's own <c>CREATE RULE</c> on request.</summary>
public sealed class RuleNodeViewModel : SchemaNodeViewModel
{
    private readonly RuleInfo _rule;

    public RuleNodeViewModel(RuleInfo rule)
        : base("↻", rule.Name, RelationDetailText.Rule(rule), hasChildren: false)
        => _rule = rule;

    public override string? IconKey => "Icon.Rule";

    public override bool CanShowDefinition => _rule.Definition.Length > 0;

    public override Task<string> LoadDefinitionAsync(CancellationToken ct) => Task.FromResult(_rule.Definition);

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);
}

/// <summary>
/// One of the dozen kinds whose whole content is a name and a line (<see cref="SchemaObjectInfo"/>) —
/// publications, subscriptions, foreign servers, event triggers, collations, casts, operators, operator
/// classes, text-search configurations, tablespaces. One node type for all of them, because the row is the
/// same row; the group it sits in is what says which kind it is.
/// </summary>
public sealed class SchemaObjectNodeViewModel : SchemaNodeViewModel
{
    public SchemaObjectNodeViewModel(SchemaObjectInfo item, string defaultSchema, string iconKey, string glyph)
        : base(glyph,
            item.Schema.Length == 0 ? item.Name : SchemaObjectLabel.Title(item.Schema, item.Name, defaultSchema),
            RelationDetailText.SchemaObject(item),
            hasChildren: false)
    {
        Item = item;
        _iconKey = iconKey;
    }

    private readonly string _iconKey;

    /// <summary>The object itself, so a caller can read what the row could not fit.</summary>
    public SchemaObjectInfo Item { get; }

    public override string? IconKey => _iconKey;

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);
}

/// <summary>A constraint of a relation. Leaf; shows the server's own definition on request (#46).</summary>
public sealed class ConstraintNodeViewModel : SchemaNodeViewModel
{
    private readonly ConstraintInfo _constraint;

    public ConstraintNodeViewModel(ISchemaSnapshot snapshot, long tableId, ConstraintInfo constraint)
        : base(RelationDetailText.ConstraintGlyph(constraint.Kind),
            constraint.Name,
            RelationDetailText.Constraint(snapshot, tableId, constraint),
            hasChildren: false)
        => _constraint = constraint;

    /// <summary>Already fetched with the table's details, so showing it costs no round trip.</summary>
    public override bool CanShowDefinition => _constraint.Definition.Length > 0;

    public override Task<string> LoadDefinitionAsync(CancellationToken ct) => Task.FromResult(_constraint.Definition);

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);
}

/// <summary>An index of a relation. Leaf; shows its <c>CREATE INDEX</c> on request (#46).</summary>
public sealed class IndexNodeViewModel : SchemaNodeViewModel
{
    private readonly IndexInfo _index;

    public IndexNodeViewModel(ISchemaSnapshot snapshot, long tableId, IndexInfo index, long? sizeBytes = null)
        : base(RelationDetailText.IndexGlyph(index),
            index.Name,
            // The size is what makes "is this index worth its cost" answerable — an index row without it
            // answers only half the question (#76).
            sizeBytes is { } bytes
                ? $"{RelationDetailText.Index(snapshot, tableId, index)} · {ByteSize.Format(bytes)}"
                : RelationDetailText.Index(snapshot, tableId, index),
            hasChildren: false)
        => _index = index;

    public override bool CanShowDefinition => _index.Definition.Length > 0;

    public override Task<string> LoadDefinitionAsync(CancellationToken ct) => Task.FromResult(_index.Definition);

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);
}

/// <summary>A trigger of a relation. Leaf; shows its <c>CREATE TRIGGER</c> on request (#46).</summary>
public sealed class TriggerNodeViewModel : SchemaNodeViewModel
{
    private readonly TriggerInfo _trigger;

    public TriggerNodeViewModel(TriggerInfo trigger)
        : base(RelationDetailText.TriggerGlyph(trigger),
            trigger.Name,
            RelationDetailText.Trigger(trigger),
            hasChildren: false)
        => _trigger = trigger;

    public override bool CanShowDefinition => _trigger.Definition.Length > 0;

    public override Task<string> LoadDefinitionAsync(CancellationToken ct) => Task.FromResult(_trigger.Definition);

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);
}

/// <summary>
/// One foreign key, read from whichever end the user is looking at (#46). The same constraint appears under
/// its declaring table as an outgoing key and under the referenced table as an incoming reference, and the
/// two rows say different things — so the direction is a parameter, not two node types.
/// </summary>
public sealed class ForeignKeyNodeViewModel : SchemaNodeViewModel
{
    public ForeignKeyNodeViewModel(ISchemaSnapshot snapshot, ForeignKeyInfo fk, bool incoming)
        : base(incoming ? "←" : "→",
            fk.Name,
            incoming ? RelationDetailText.Incoming(snapshot, fk) : RelationDetailText.Outgoing(snapshot, fk),
            hasChildren: false)
    { }

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);
}
