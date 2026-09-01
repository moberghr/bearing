using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Bearing.App.Connections;
using Bearing.Core.Data;
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

    /// <summary>Whether the node can be expanded (drives the placeholder + the expander arrow).</summary>
    public bool HasChildren { get; }

    /// <summary>Whether this row is a database — for the context menu's size-ordering items (#76).</summary>
    public virtual bool IsDatabase => false;

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

    /// <summary>Load children once; idempotent. Public so tests can await the load directly.</summary>
    public async Task EnsureChildrenAsync()
    {
        if (_loaded || !HasChildren) return;
        _loaded = true;
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

    /// <summary>Discard loaded children and reload them if the node is currently expanded (else on next expand).</summary>
    public async Task RefreshAsync()
    {
        if (!HasChildren) return;
        var wasExpanded = IsExpanded;
        _loaded = false;
        Children.Clear();
        Children.Add(new MessageNodeViewModel("", "Loading…"));
        if (wasExpanded) await EnsureChildrenAsync();
    }

    protected abstract Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync();

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

    public ServerNodeViewModel(ConnectionInfo connection, ISchemaBrowser browser)
        : base("⛁", connection.Name, ConnectionEndpoint.HostPort(connection), hasChildren: true)
    {
        Connection = connection;
        _browser = browser;
    }

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
                Connection, db, isConnected: string.Equals(db, Connection.Database, StringComparison.Ordinal), _browser))
            .ToList();

        // Sizes after the tree, like the relation sizes below it (#76): pg_database_size stats a whole
        // directory per database, and the server row must not wait for it.
        _ = FillDatabaseSizesAsync(children);
        return children;
    }

    /// <summary>
    /// Label each database row with its size on the server. Silent on failure and per row on unknown: a
    /// database the user cannot connect to reports null rather than raising, and one they cannot reach must
    /// not cost the sizes of the rest.
    /// </summary>
    private async Task FillDatabaseSizesAsync(IReadOnlyList<SchemaNodeViewModel> children)
    {
        IReadOnlyList<DatabaseSize> sizes;
        try { sizes = await _browser.GetDatabaseSizesAsync(Connection, CancellationToken.None); }
        catch (Exception) { return; }

        var byName = new Dictionary<string, DatabaseSize>(StringComparer.Ordinal);
        foreach (var size in sizes) byName[size.Database] = size;

        foreach (var database in children.OfType<DatabaseNodeViewModel>())
            if (byName.TryGetValue(database.Database, out var size) && size.Bytes is { } bytes)
                database.ApplySize(bytes);

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

    public DatabaseNodeViewModel(ConnectionInfo connection, string database, bool isConnected, ISchemaBrowser browser)
        : base("🗄", database, isConnected ? "connected" : null, hasChildren: true)
    {
        _connection = connection;
        _database = database;
        _browser = browser;
        _connectedLabel = isConnected ? "connected" : null;
    }

    public override string? IconKey => "Icon.Database";
    public override string IconColorHex => "#5FC9AD";

    /// <summary>The database this row is for, so a server-level size read can match it by name.</summary>
    internal string Database => _database;

    /// <summary>
    /// Label this row with the database's size (#76), keeping whatever else the detail said. Leading with the
    /// size for the same reason a relation row does — the panel ellipsizes, and a truncated number is worse
    /// than none.
    /// </summary>
    internal void ApplySize(long bytes)
    {
        var rest = string.IsNullOrEmpty(_connectedLabel) ? null : _connectedLabel;
        Detail = rest is null ? ByteSize.Format(bytes) : $"{ByteSize.Format(bytes)} · {rest}";
    }

    private readonly string? _connectedLabel;

    /// <summary>Lets the tree's one context menu show the size-ordering items on a database row only — the
    /// same shape as <c>IsServer</c>, which the server-only items already bind.</summary>
    public override bool IsDatabase => true;

    protected override async Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync()
    {
        var objects = await _browser.GetObjectsAsync(_connection, _database, CancellationToken.None);
        var snapshot = objects.Snapshot;
        var defaultSchema = SchemaObjectLabel.DefaultSchemaOf(snapshot.SearchPath);

        var tables = new List<Sortable>();
        var views = new List<Sortable>();
        foreach (var t in snapshot.Tables)
        {
            var node = new RelationNodeViewModel(_connection, _database, t, snapshot, _browser, defaultSchema);
            var entry = Entry(t.Schema, t.Name, RelationRank(t.Kind), defaultSchema, node);
            (IsViewLike(t.Kind) ? views : tables).Add(entry);
        }

        var routines = objects.Routines
            .Select(r => Entry(r.Schema, r.Name, RoutineRank(r.Kind), defaultSchema,
                new RoutineNodeViewModel(_connection, _database, r, _browser, defaultSchema)))
            .ToList();

        // Tables inline; the secondary object kinds behind one collapsed row each (skipped when empty
        // rather than shown as an empty bucket).
        var children = Ordered(tables);
        if (views.Count > 0) children.Add(new SchemaGroupNodeViewModel("Views", "Icon.View", Ordered(views)));
        if (routines.Count > 0) children.Add(new SchemaGroupNodeViewModel("Functions", "Icon.Function", Ordered(routines)));

        _loadOrder.Clear();
        for (var i = 0; i < children.Count; i++)
            if (children[i] is RelationNodeViewModel relation) _loadOrder[relation] = i;

        // Sizes are read *after* the tree is handed back, never before: pg_total_relation_size stats files
        // per relation, so waiting for it would make every expand as slow as the biggest database (#76). The
        // rows re-label themselves when it lands.
        _ = FillSizesAsync(children);
        return children;
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
        IReadOnlyList<RelationSize> sizes;
        try { sizes = await _browser.GetRelationSizesAsync(_connection, _database, CancellationToken.None); }
        catch (Exception) { return; }

        var byTable = sizes.ToDictionary(s => s.TableId);
        foreach (var relation in Relations(children))
            if (byTable.TryGetValue(relation.TableId, out var size)) relation.ApplySize(size);

        // If the user asked for size order before the sizes existed, this is when it can be honoured.
        if (_order == RelationOrder.Size) SetRelationOrder(RelationOrder.Size);
        SizesLoaded?.Invoke();
    }

    /// <summary>Raised once the size read has re-labelled the rows. For tests — nothing in the app waits on
    /// it, which is the point of loading them late.</summary>
    internal Action? SizesLoaded { get; set; }

    /// <summary>Every relation row under this database, the ones inside the Views bucket included.</summary>
    private static IEnumerable<RelationNodeViewModel> Relations(IReadOnlyList<SchemaNodeViewModel> children)
        => children
            .SelectMany(c => c is SchemaGroupNodeViewModel group ? group.Children : [c])
            .OfType<RelationNodeViewModel>();

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
        // hasChildren: false — nothing to load lazily, so no placeholder; the members below give the
        // expander its arrow. Collapsed on construction is the point of the bucket.
        : base("▸", title, members.Count.ToString(), hasChildren: false)
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

    public RelationNodeViewModel(
        ConnectionInfo connection, string database, TableInfo table, ISchemaSnapshot snapshot, ISchemaBrowser browser,
        string defaultSchema)
        : base(Glyphs(table.Kind),
            SchemaObjectLabel.Title(table.Schema, table.Name, defaultSchema),
            SchemaObjectLabel.Detail(KindLabel(table.Kind), table.Schema, defaultSchema),
            hasChildren: true)
    {
        _connection = connection;
        _database = database;
        _table = table;
        _snapshot = snapshot;
        _browser = browser;
        _defaultSchema = defaultSchema;
    }

    private readonly string _defaultSchema;

    private bool IsViewLike => _table.Kind is RelationKind.View or RelationKind.MaterializedView;

    public override bool CanShowDefinition => true;

    /// <summary>This relation's id, so a size read can find the row it belongs to.</summary>
    internal long TableId => _table.Id;

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
        foreach (var column in _snapshot.ColumnsOf(_table.Id))
            children.Add(new ColumnNodeViewModel(column));

        var (outgoing, incoming) = RelationDetailText.SplitByDirection(_snapshot, _table.Id);

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
            SchemaObjectLabel.Detail(KindLabel(routine.Kind), routine.Schema, defaultSchema),
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

/// <summary>A column of a relation. Leaf; shows type + PK / NOT NULL.</summary>
public sealed class ColumnNodeViewModel : SchemaNodeViewModel
{
    public ColumnNodeViewModel(ColumnInfo column)
        : base(column.IsPrimaryKey ? "🔑" : "·", column.Name, DetailFor(column), hasChildren: false) { }

    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync() => Task.FromResult(None);

    private static string DetailFor(ColumnInfo c)
    {
        var s = c.DataType;
        if (c.NotNull) s += " not null";
        if (c.IsPrimaryKey) s += " · PK";
        return s;
    }
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
