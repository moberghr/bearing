using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Demo;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The rest of the explorer's breadth (#119 follow-up): column defaults and identity, comments, rules,
/// partitions nested under their parent, a Schemas level, tablespaces, and the long-tail kinds. As
/// everywhere in this project, what is asserted here is how the <b>tree</b> behaves given a shape;
/// resolution against a real catalog is <c>Bearing.Data.Tests</c>' job (§4.6).
/// </summary>
public class SchemaBreadthTests
{
    private static ConnectionInfo Conn() => new()
    {
        Id = Guid.NewGuid(),
        Name = "srv",
        ProviderId = "postgres",
        Host = "h",
        Port = 5432,
        Database = "demo",
        User = "u",
    };

    private static async Task<DatabaseNodeViewModel> Database(BreadthBrowser browser)
    {
        var db = new DatabaseNodeViewModel(Conn(), "demo", isConnected: true, browser);
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        db.ObjectKindsLoaded = () => landed.TrySetResult();
        await db.EnsureChildrenAsync();
        await Task.WhenAny(landed.Task, Task.Delay(5000));
        return db;
    }

    private static IReadOnlyList<string> Groups(SchemaNodeViewModel node)
        => node.Children.OfType<SchemaGroupNodeViewModel>().Select(g => g.Title).ToList();

    private static SchemaGroupNodeViewModel Group(SchemaNodeViewModel node, string title)
        => node.Children.OfType<SchemaGroupNodeViewModel>().Single(g => g.Title == title);

    // ---- 1. column defaults, identity, generated, collation ------------------------------------------

    [Fact]
    public async Task A_column_says_where_its_value_comes_from()
    {
        // The half of #119 that was missing: SequenceInfo.OwnedBy gave sequence → column, and this is
        // column → sequence. `nextval(…)` on the row is the route the tree had no way to offer.
        var relation = await Relation(DemoCatalog.PaymentId);
        var columns = relation.Children.OfType<ColumnNodeViewModel>().ToList();

        var id = columns.Single(c => c.Title == "id");
        Assert.Contains("default nextval('shop.payment_id_seq')", id.Detail);
        Assert.Contains("PK", id.Detail);
    }

    [Fact]
    public async Task An_identity_column_says_which_kind_of_identity()
    {
        // ALWAYS rejects an explicit value without OVERRIDING; BY DEFAULT accepts one. Different rows.
        var relation = await Relation(DemoCatalog.StoreId);
        var columns = relation.Children.OfType<ColumnNodeViewModel>().ToList();

        Assert.Contains("identity (always)", columns.Single(c => c.Title == "id").Detail);
    }

    [Fact]
    public async Task A_non_default_collation_is_on_the_row_and_a_default_one_is_not()
    {
        // Every text column has a collation. Listing the database default on all of them would bury the one
        // column that was given a different one, which is the only interesting case.
        var relation = await Relation(DemoCatalog.StoreId);
        var columns = relation.Children.OfType<ColumnNodeViewModel>().ToList();

        Assert.Contains("collate case_insensitive", columns.Single(c => c.Title == "name").Detail);
        Assert.DoesNotContain("collate", columns.Single(c => c.Title == "active").Detail);
    }

    [Fact]
    public async Task A_column_with_no_extras_reads_as_it_always_did()
    {
        var relation = await Relation(DemoCatalog.StoreId);
        var active = relation.Children.OfType<ColumnNodeViewModel>().Single(c => c.Title == "active");

        // The catalog's own spelling, unchanged: the reader reports what Postgres reports.
        Assert.Equal("bool", active.Detail);
    }

    [Fact]
    public async Task A_failed_detail_read_still_yields_the_columns_with_their_types()
    {
        // The columns come from the snapshot; only the extras need the round trip. Losing the extras must
        // not lose the columns.
        var relation = await Relation(DemoCatalog.StoreId, new BreadthBrowser { ThrowOnDetails = true });

        var columns = relation.Children.OfType<ColumnNodeViewModel>().ToList();
        Assert.NotEmpty(columns);
        // Type, NOT NULL and PK come from the snapshot, so they survive; the extras are what is lost.
        Assert.Equal("int4 · not null · PK", columns.Single(c => c.Title == "id").Detail);
        Assert.Contains(relation.Children, c => c.Title.StartsWith("Couldn't read"));
    }

    // ---- 2. comments ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_table_s_comment_lands_on_its_row()
    {
        // The schema's own documentation. Someone wrote it to be read here, and the tree could not show it
        // at all before.
        var relation = await Relation(DemoCatalog.PaymentId);
        Assert.Contains("every payment taken", relation.Detail);
    }

    [Fact]
    public async Task A_column_s_comment_lands_on_its_row()
    {
        var relation = await Relation(DemoCatalog.PaymentId);
        var store = relation.Children.OfType<ColumnNodeViewModel>().Single(c => c.Title == "store_id");

        Assert.Contains("null for an online sale", store.Detail);
    }

    [Fact]
    public async Task A_kind_s_comment_lands_on_its_row_too()
    {
        var db = await Database(new BreadthBrowser());
        var archive = Group(db, "Sequences").Children.Single(c => c.Title == "receipt_no_seq");

        Assert.Contains("reset each year", archive.Detail);
    }

    // ---- 3. rules ------------------------------------------------------------------------------------

    [Fact]
    public async Task Rules_get_their_own_folder_beside_the_triggers()
    {
        var relation = await Relation(DemoCatalog.ReceiptViewId);

        var rules = Group(relation, "Rules");
        Assert.Equal(["receipt_no_insert"], rules.Children.Select(c => c.Title));
        Assert.Contains("insert", rules.Children[0].Detail);
        Assert.Contains("instead", rules.Children[0].Detail);
    }

    [Fact]
    public async Task A_relation_with_no_rules_grows_no_rules_folder()
        => Assert.DoesNotContain("Rules", Groups(await Relation(DemoCatalog.StoreId)));

    // ---- 4. partitions -------------------------------------------------------------------------------

    [Fact]
    public async Task A_partition_is_a_child_of_its_parent_rather_than_a_sibling()
    {
        // Its rows *are* the parent's. A hundred sibling rows with nothing linking them is noise and a
        // missing answer at the same time.
        var db = await Database(new BreadthBrowser());
        var relations = db.Children.OfType<RelationNodeViewModel>().ToList();

        Assert.Contains(relations, r => r.RelationName == "event");
        Assert.DoesNotContain(relations, r => r.RelationName == "event_2026");
        Assert.DoesNotContain(relations, r => r.RelationName == "event_2027");

        var parent = relations.Single(r => r.RelationName == "event");
        Assert.Contains("2 partitions", parent.Detail);

        await parent.EnsureChildrenAsync();
        var partitions = Group(parent, "Partitions");
        Assert.Equal(["event_2026", "event_2027"], partitions.Children.Select(c => c.Title));
    }

    [Fact]
    public async Task An_unpartitioned_table_says_nothing_about_partitions()
    {
        var db = await Database(new BreadthBrowser());
        var store = db.Children.OfType<RelationNodeViewModel>().Single(r => r.RelationName == "store");

        Assert.DoesNotContain("partition", store.Detail);
        await store.EnsureChildrenAsync();
        Assert.DoesNotContain("Partitions", Groups(store));
    }

    [Fact]
    public async Task A_partition_whose_parent_is_not_in_the_snapshot_still_appears()
    {
        // Filtered-out parent (another schema's, say). Hiding the child then would make it vanish from the
        // tree entirely, which is worse than showing it at the top level.
        var browser = new BreadthBrowser { OrphanPartition = true };
        var db = await Database(browser);

        Assert.Contains(db.Children.OfType<RelationNodeViewModel>(), r => r.RelationName == "orphan_2026");
    }

    // ---- 5. the Schemas level ------------------------------------------------------------------------

    [Fact]
    public async Task A_multi_schema_database_gets_a_schemas_level()
    {
        var db = await Database(new BreadthBrowser());

        var schemas = Group(db, "Schemas");
        Assert.Equal(["shop", "reporting"], schemas.Children.Select(c => c.Title));
        // The row says how much is inside, so a collapsed one is worth a decision.
        Assert.Contains("relations", schemas.Children[0].Detail);
    }

    [Fact]
    public async Task A_schema_names_its_objects_unqualified()
    {
        // Inside a row that says `reporting`, repeating the schema on every child is the noise this level
        // exists to remove.
        var db = await Database(new BreadthBrowser());
        var reporting = Group(db, "Schemas").Children.Single(c => c.Title == "reporting");
        await reporting.EnsureChildrenAsync();

        Assert.Equal(["summary"], reporting.Children.Select(c => c.Title));
        // …whereas the inline list still qualifies it, because there it sits among other schemas'.
        Assert.Contains(db.Children.OfType<RelationNodeViewModel>(), r => r.Title == "reporting.summary");
    }

    [Fact]
    public async Task A_single_schema_database_gets_no_schemas_level()
    {
        // A Schemas row holding everything already on screen is pure noise.
        var db = await Database(new BreadthBrowser { SingleSchema = true });
        Assert.DoesNotContain("Schemas", Groups(db));
    }

    [Fact]
    public async Task A_schema_is_loaded_lazily()
    {
        // Its children are a *second* set of nodes for relations the inline list already holds, so a
        // database with two thousand of them must not pay for them up front.
        var db = await Database(new BreadthBrowser());
        var shop = Group(db, "Schemas").Children.Single(c => c.Title == "shop");

        Assert.True(shop.HasChildren);
        Assert.Single(shop.Children);
        Assert.IsType<MessageNodeViewModel>(shop.Children[0]);
    }

    // ---- 6. tablespaces ------------------------------------------------------------------------------

    [Fact]
    public async Task Tablespaces_sit_on_the_server_beside_the_roles()
    {
        // The other cluster-wide kind, so it cannot live under a database either.
        var browser = new BreadthBrowser();
        var server = new ServerNodeViewModel(Conn(), browser);
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.TablespacesLoaded = () => landed.TrySetResult();
        await server.EnsureChildrenAsync();
        await Task.WhenAny(landed.Task, Task.Delay(5000));

        var group = Group(server, "Tablespaces");
        Assert.Equal(["pg_default", "shop_archive"], group.Children.Select(c => c.Title));
        Assert.Equal("the data directory", group.Children[0].Detail);
        Assert.Contains("cold storage", group.Children[1].Detail);
    }

    // ---- 7-8. the long-tail kinds --------------------------------------------------------------------

    [Fact]
    public async Task Every_kind_the_demo_reports_gets_a_group_and_nothing_else_does()
    {
        var db = await Database(new BreadthBrowser());

        Assert.Equal(
            [
                // Schemas leads: it is structural, and a level that arrives below the object kinds it
                // organises reads as an afterthought.
                "Schemas", "Views", "Functions", "Procedures",
                "Sequences", "Types", "Extensions", "Policies",
                "Publications", "Subscriptions", "Foreign servers", "Event triggers",
                "Collations", "Casts", "Operators", "Operator classes", "Text search",
            ],
            Groups(db));
        Assert.All(db.Children.OfType<SchemaGroupNodeViewModel>(), g => Assert.False(g.IsExpanded));
    }

    [Fact]
    public async Task An_empty_long_tail_leaves_its_groups_out()
    {
        var db = await Database(new BreadthBrowser { Kinds = DatabaseObjectKinds.Empty });

        foreach (var absent in new[]
                 {
                     "Publications", "Subscriptions", "Foreign servers", "Event triggers",
                     "Collations", "Casts", "Operators", "Operator classes", "Text search",
                 })
            Assert.DoesNotContain(absent, Groups(db));
    }

    [Fact]
    public async Task A_long_tail_row_is_its_name_and_one_line()
    {
        var db = await Database(new BreadthBrowser());

        var publication = Group(db, "Publications").Children.Single();
        Assert.Equal("shop_stream", publication.Title);
        Assert.Equal("2 tables · insert, update, delete", publication.Detail);

        // A cast has no name of its own in the catalog, so the row is what it converts.
        var cast = Group(db, "Casts").Children.Single();
        Assert.Equal("shop.positive_amount → numeric", cast.Title);

        // A schema-scoped kind is qualified when it is outside the default schema, like everything else.
        var collation = Group(db, "Collations").Children.Single();
        Assert.Equal("case_insensitive", collation.Title);
    }

    [Fact]
    public async Task A_subscription_row_carries_no_connection_string()
    {
        // pg_subscription.subconninfo is the publisher's connection string and can hold a password, so it
        // is never read (§1.1). Asserted on the row, which is where it would surface.
        var db = await Database(new BreadthBrowser());
        var subscription = Group(db, "Subscriptions").Children.Single();

        Assert.Equal("shop_replica", subscription.Title);
        Assert.DoesNotContain("host=", subscription.Detail);
        Assert.DoesNotContain("password", subscription.Detail);
        Assert.Contains("enabled", subscription.Detail);
    }

    // ---- regressions from the code review -------------------------------------------------------------

    [Fact]
    public async Task A_sub_partition_is_reachable_through_its_own_parent()
    {
        // The bug: with two levels, the sub-partition was skipped at the top (its parent was present) and
        // never added below (each nested node was built with no map), so it was reachable from nowhere.
        var db = await Database(new BreadthBrowser { SubPartition = true });

        var event_ = db.Children.OfType<RelationNodeViewModel>().Single(r => r.RelationName == "event");
        await event_.EnsureChildrenAsync();
        var mid = Group(event_, "Partitions").Children
            .OfType<RelationNodeViewModel>().Single(r => r.RelationName == "event_2026");

        // The middle level says it has one of its own, and it is under there.
        Assert.Contains("1 partition", mid.Detail);
        await mid.EnsureChildrenAsync();
        Assert.Equal(["event_2026_eu"], Group(mid, "Partitions").Children.Select(c => c.Title));
    }

    /// <summary>
    /// A late group append from a superseded load must be discarded.
    /// <para>
    /// "Refresh metadata" clears <c>Children</c> and re-runs <c>OnChildrenAttached</c> on the same node, so a
    /// read still in flight from the previous load used to land its group on the rebuilt children and the new
    /// read added a second — two Roles groups, or up to a dozen duplicated groups on a database.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_slow_kind_read_from_a_superseded_load_does_not_duplicate_its_groups()
    {
        var browser = new BreadthBrowser { GateKinds = true };
        var db = new DatabaseNodeViewModel(Conn(), "demo", isConnected: true, browser);

        await db.EnsureChildrenAsync();          // load 1 — its kind read is parked on the gate
        // Expanded, or RefreshAsync would reload nothing and there would be no second read to race.
        db.IsExpanded = true;
        await db.RefreshAsync();                 // load 2 — supersedes it

        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        db.ObjectKindsLoaded = () => landed.TrySetResult();
        browser.ReleaseKinds();                  // both reads complete now
        await Task.WhenAny(landed.Task, Task.Delay(5000));
        // Give the superseded one every chance to append as well.
        await Task.Delay(50);

        // One of each, not two.
        Assert.Equal(
            Groups(db).Distinct().Count(),
            Groups(db).Count);
        Assert.Single(Groups(db), g => g == "Sequences");
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static async Task<RelationNodeViewModel> Relation(long tableId, BreadthBrowser? browser = null)
    {
        browser ??= new BreadthBrowser();
        var snapshot = browser.Snapshot();
        var table = snapshot.Tables.Single(t => t.Id == tableId);
        var relation = new RelationNodeViewModel(Conn(), "demo", table, snapshot, browser, "shop");
        await relation.EnsureChildrenAsync();
        return relation;
    }

    /// <summary>
    /// The demo catalog, plus the shapes it does not carry: an identity column, a generated column, a
    /// non-default collation, comments, a rule, and a partitioned table.
    /// </summary>
    private sealed class BreadthBrowser : ISchemaBrowser
    {
        public DatabaseObjectKinds Kinds { get; init; } = DemoCatalog.ObjectKinds();
        public bool ThrowOnDetails { get; init; }
        public bool SingleSchema { get; init; }
        public bool OrphanPartition { get; init; }

        /// <summary>Add a second level of partitioning under event_2026.</summary>
        public bool SubPartition { get; init; }

        /// <summary>Hold the kind read until <see cref="ReleaseKinds"/>, so two loads can overlap.</summary>
        public bool GateKinds { get; init; }

        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseKinds() => _gate.TrySetResult();

        private const long EventId = 8001;
        private const long Event2026Id = 8002;
        private const long Event2027Id = 8003;
        private const long OrphanId = 8004;
        private const long MissingParentId = 8999;

        public SchemaSnapshot Snapshot()
        {
            var demo = DemoCatalog.Snapshot();
            var tables = demo.Tables.ToList();
            var columns = demo.Tables.SelectMany(t => demo.ColumnsOf(t.Id)).ToList();

            if (!SingleSchema)
            {
                tables.Add(new TableInfo(8100, "reporting", "summary", RelationKind.Table));
                columns.Add(new ColumnInfo(8100, 1, "day", "date", NotNull: true, IsPrimaryKey: true));
            }

            tables.Add(new TableInfo(EventId, "shop", "event", RelationKind.Partitioned));
            tables.Add(new TableInfo(Event2026Id, "shop", "event_2026", RelationKind.Table, PartitionOf: EventId));
            tables.Add(new TableInfo(Event2027Id, "shop", "event_2027", RelationKind.Table, PartitionOf: EventId));
            columns.Add(new ColumnInfo(EventId, 1, "at", "timestamptz", NotNull: true, IsPrimaryKey: false));

            if (SubPartition)
            {
                tables.Add(new TableInfo(8050, "shop", "event_2026_eu", RelationKind.Table,
                    PartitionOf: Event2026Id));
                columns.Add(new ColumnInfo(8050, 1, "at", "timestamptz", NotNull: true, IsPrimaryKey: false));
            }

            if (OrphanPartition)
            {
                // A parent that is not in the snapshot at all.
                tables.Add(new TableInfo(OrphanId, "shop", "orphan_2026", RelationKind.Table,
                    PartitionOf: MissingParentId));
                columns.Add(new ColumnInfo(OrphanId, 1, "at", "timestamptz", NotNull: true, IsPrimaryKey: false));
            }

            return new SchemaSnapshot(
                "demo",
                SingleSchema ? ["shop"] : ["shop", "reporting"],
                tables,
                columns,
                demo.Tables.SelectMany(t => demo.ForeignKeysTouching(t.Id)).Distinct().ToList(),
                searchPath: ["shop"]);
        }

        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(["demo"]);

        /// <summary>The demo's function plus a procedure, so both routine groups render (#119's split).</summary>
        private static IReadOnlyList<RoutineInfo> Routines() =>
            [.. DemoCatalog.Routines(), new RoutineInfo(3002, "shop", "close_day", RoutineKind.Procedure, "()", "")];

        public Task<DatabaseObjects> GetObjectsAsync(ConnectionInfo connection, string database, CancellationToken ct)
            => Task.FromResult(new DatabaseObjects(Snapshot(), Routines()));

        public async Task<DatabaseObjectKinds> GetDatabaseObjectKindsAsync(
            ConnectionInfo connection, string database, CancellationToken ct)
        {
            if (GateKinds) await _gate.Task;
            return Kinds;
        }

        public Task<TableDetails> GetTableDetailsAsync(
            ConnectionInfo connection, string database, long tableId, CancellationToken ct)
        {
            if (ThrowOnDetails)
                return Task.FromException<TableDetails>(new InvalidOperationException("permission denied"));

            var baseline = DemoCatalog.DetailsOf(tableId);
            return Task.FromResult(tableId switch
            {
                DemoCatalog.PaymentId => baseline with
                {
                    Comment = "every payment taken, one row per authorisation",
                    Columns =
                    [
                        new ColumnDetail(1, "nextval('shop.payment_id_seq')", ColumnIdentity.None, null, null, null),
                        new ColumnDetail(2, null, ColumnIdentity.None, null, null, "null for an online sale"),
                    ],
                },
                DemoCatalog.StoreId => baseline with
                {
                    Columns =
                    [
                        new ColumnDetail(1, null, ColumnIdentity.Always, null, null, null),
                        new ColumnDetail(2, null, ColumnIdentity.None, null, "case_insensitive", null),
                    ],
                },
                DemoCatalog.ReceiptViewId => baseline with
                {
                    Rules =
                    [
                        new RuleInfo(9001, "receipt_no_insert",
                            "CREATE RULE receipt_no_insert AS ON INSERT TO shop.receipt DO INSTEAD NOTHING;"),
                    ],
                },
                _ => baseline,
            });
        }

        public Task<string> GetViewDefinitionAsync(
            ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => Task.FromResult("select 1");

        public Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(
            ConnectionInfo connection, string database, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RelationSize>>([]);

        public Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DatabaseSize>>([]);

        public Task<string> GetRoutineDefinitionAsync(
            ConnectionInfo connection, string database, long routineId, CancellationToken ct)
            => Task.FromResult("CREATE FUNCTION …");

        public Task<IReadOnlyList<RoleInfo>> GetRolesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RoleInfo>>([]);

        public Task<RoleGrants> GetRoleGrantsAsync(
            ConnectionInfo connection, string database, string roleName, CancellationToken ct)
            => Task.FromResult(RoleGrants.Of([]));

        public Task<IReadOnlyList<SchemaObjectInfo>> GetTablespacesAsync(
            ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult(DemoCatalog.Tablespaces());

        public Task InvalidateAsync(Guid connectionId) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
