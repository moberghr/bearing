using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Squirrel.App.Connections;
using Squirrel.Core.Data;
using Squirrel.Core.Schema;
using Squirrel.Sql;

namespace Squirrel.App.ViewModels;

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

    /// <summary>True only for the root server node (drives its context-menu items + double-tap).</summary>
    public virtual bool IsServer => false;

    /// <summary>Hex color for a leading badge (environment color on the server node); null = no badge.</summary>
    public virtual string? BadgeColor => null;

    /// <summary>Resource key of a vector icon (Icon.*) shown instead of the text <see cref="Glyph"/>; null = use the glyph.</summary>
    public virtual string? IconKey => null;

    /// <summary>Hex stroke color for the vector icon.</summary>
    public virtual string IconColorHex => "#9D967F";

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
            foreach (var k in kids) Children.Add(k);
        }
        catch (Exception ex)
        {
            Children.Clear();
            Children.Add(new MessageNodeViewModel("⚠", ex.Message));
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
    public override string? BadgeColor => Connection.EnvironmentColor;
    public override string? IconKey => "Icon.Connections"; // server / postgres
    public override string IconColorHex => "#7E9CD8";

    protected override async Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync()
    {
        var databases = await _browser.GetDatabasesAsync(Connection, CancellationToken.None);
        return databases
            .Select(db => (SchemaNodeViewModel)new DatabaseNodeViewModel(
                Connection, db, isConnected: string.Equals(db, Connection.Database, StringComparison.Ordinal), _browser))
            .ToList();
    }
}

/// <summary>A database on the server. Expands to its objects (relations + routines), listed flat.</summary>
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
    public override string IconColorHex => "#98BB6C";

    protected override async Task<IReadOnlyList<SchemaNodeViewModel>> LoadChildrenAsync()
    {
        var objects = await _browser.GetObjectsAsync(_connection, _database, CancellationToken.None);
        var snapshot = objects.Snapshot;

        var nodes = new List<(int rank, string name, SchemaNodeViewModel node)>();
        foreach (var t in snapshot.Tables)
            nodes.Add((RelationRank(t.Kind), t.Name,
                new RelationNodeViewModel(_connection, _database, t, snapshot, _browser)));
        foreach (var r in objects.Routines)
            nodes.Add((RoutineRank(r.Kind), r.Name,
                new RoutineNodeViewModel(_connection, _database, r, _browser)));

        return nodes
            .OrderBy(x => x.rank)
            .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.node)
            .ToList();
    }

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

/// <summary>A relation (table/view/…). Expands to its columns; can show a definition (view SQL or DDL).</summary>
public sealed class RelationNodeViewModel : SchemaNodeViewModel
{
    private readonly ConnectionInfo _connection;
    private readonly string _database;
    private readonly TableInfo _table;
    private readonly ISchemaSnapshot _snapshot;
    private readonly ISchemaBrowser _browser;

    public RelationNodeViewModel(
        ConnectionInfo connection, string database, TableInfo table, ISchemaSnapshot snapshot, ISchemaBrowser browser)
        : base(Glyphs(table.Kind), table.Name, $"{KindLabel(table.Kind)} · {table.Schema}", hasChildren: true)
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

    public RoutineNodeViewModel(ConnectionInfo connection, string database, RoutineInfo routine, ISchemaBrowser browser)
        : base(GlyphFor(routine.Kind), routine.Name, DetailFor(routine), hasChildren: false)
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

    private static string DetailFor(RoutineInfo r)
    {
        var label = r.Kind switch
        {
            RoutineKind.Procedure => "procedure",
            RoutineKind.Aggregate => "aggregate",
            RoutineKind.Window => "window",
            _ => "function",
        };
        return $"{label} · {r.Schema}";
    }
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
