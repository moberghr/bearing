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
        Title = title;
        Detail = detail;
        HasChildren = hasChildren;
        if (hasChildren) Children.Add(new MessageNodeViewModel("", "Loading…"));
    }

    public string Glyph { get; }
    public string Title { get; }
    public string? Detail { get; }

    /// <summary>Whether the node can be expanded (drives the placeholder + the expander arrow).</summary>
    public bool HasChildren { get; }

    public ObservableCollection<SchemaNodeViewModel> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoading;

    /// <summary>True when the node's title matches the current type-ahead search (drives the highlight).</summary>
    [ObservableProperty] private bool _isMatch;

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

    /// <summary>Hex environment colour washed across the whole row (server nodes only); null = no wash.
    /// It replaced a 9px leading dot, which read as a connection-state light next to the toolbar's
    /// (issue #45) — a row fill can't.</summary>
    public virtual string? RowAccentColor => null;

    /// <summary>True for nodes that represent a connectable server, so the row carries a chain glyph for
    /// <see cref="ConnectionLive"/>. Every other node type leaves the slot empty.</summary>
    public virtual bool ShowsConnectionState => false;

    /// <summary>Whether this node's server has a live session — linked vs broken chain on the row. Declared
    /// on the base because the tree's single <c>TreeDataTemplate</c> binds against this type; only nodes with
    /// <see cref="ShowsConnectionState"/> render it. Kept in sync by
    /// <c>ConnectionsViewModel.RefreshServerNodeLive</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStateTip))]
    private bool _connectionLive;

    /// <summary>Tooltip for the row's chain glyph. A string on the VM rather than a converter: there is one
    /// consumer and the two words are the whole logic.</summary>
    public string ConnectionStateTip => ConnectionLive ? "Connected" : "Not connected";

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
                Children.Add(k);
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

/// <summary>Root node: a saved connection = a server. Expands to the databases on that server.</summary>
public sealed class ServerNodeViewModel : SchemaNodeViewModel
{
    private readonly ISchemaBrowser _browser;

    public ServerNodeViewModel(ConnectionInfo connection, ISchemaBrowser browser)
        : base("⛁", connection.Name, connection.Host, hasChildren: true)
    {
        Connection = connection;
        _browser = browser;
    }

    public ConnectionInfo Connection { get; }
    public override bool IsServer => true;
    public override string? RowAccentColor => Connection.EnvironmentColor;
    public override bool ShowsConnectionState => true;
    public override string? IconKey => "Icon.Connections"; // server / postgres
    public override string IconColorHex => "#6FA6E2";

    protected override async Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync()
    {
        var databases = await _browser.GetDatabasesAsync(Connection, CancellationToken.None);
        return databases
            .Select(db => (SchemaNodeViewModel)new DatabaseNodeViewModel(
                Connection, db, isConnected: string.Equals(db, Connection.Database, StringComparison.Ordinal), _browser))
            .ToList();
    }
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
    }

    public override string? IconKey => "Icon.Database";
    public override string IconColorHex => "#5FC9AD";

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
        return children;
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
        // hasChildren: false — nothing to load lazily, so no placeholder; the members below give the
        // expander its arrow. Collapsed on construction is the point of the bucket.
        : base("▸", title, members.Count.ToString(), hasChildren: false)
    {
        _iconKey = iconKey;
        foreach (var m in members) Children.Add(m);
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
    }

    private bool IsViewLike => _table.Kind is RelationKind.View or RelationKind.MaterializedView;

    public override bool CanShowDefinition => true;

    // Columns are already in the loaded snapshot — no round-trip.
    protected override Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync()
        => Task.FromResult<IReadOnlyList<SchemaNodeViewModel>>(
            _snapshot.ColumnsOf(_table.Id).Select(c => (SchemaNodeViewModel)new ColumnNodeViewModel(c)).ToList());

    public override async Task<string> LoadDefinitionAsync(CancellationToken ct)
        => IsViewLike
            ? await _browser.GetViewDefinitionAsync(_connection, _database, _table.Id, ct)
            : TableDdlGenerator.CreateTable(_table, _snapshot);

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
