using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.Demo;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// A table's constraints, indexes, keys and references in the schema tree (#46). The tree used to expand
/// straight to columns and stop, so there was no way to see what a table points at, what points back, or what
/// it is indexed on — the three questions you ask right before writing a join or diagnosing a slow query.
/// </summary>
public class RelationDetailTests
{
    private static ConnectionInfo Conn()
        => new() { Id = Guid.NewGuid(), Name = "srv", ProviderId = "postgres", Database = "demo", User = "u" };

    private static RelationNodeViewModel Node(long tableId, ISchemaBrowser browser)
    {
        var snapshot = DemoCatalog.Snapshot();
        var table = snapshot.Tables.Single(t => t.Id == tableId);
        return new RelationNodeViewModel(Conn(), DemoCatalog.Database, table, snapshot, browser, DemoCatalog.Schema);
    }

    private static SchemaGroupNodeViewModel? Folder(RelationNodeViewModel node, string title)
        => node.Children.OfType<SchemaGroupNodeViewModel>().FirstOrDefault(f => f.Title == title);

    // ---- the folders ----------------------------------------------------------------------------

    [Fact]
    public async Task Columns_stay_inline_above_the_folders()
    {
        // Behind a Columns folder they would cost a click in the case nearly every expand is for.
        var node = Node(DemoCatalog.PaymentId, new DetailBrowser());

        await node.EnsureChildrenAsync();

        var columns = node.Children.OfType<ColumnNodeViewModel>().ToList();
        Assert.Equal(["id", "store_id", "amount", "note"], columns.Select(c => c.Title));
        // And they come first: the folders sit after the columns, not among them.
        Assert.True(
            node.Children.IndexOf(columns[^1]) < node.Children.IndexOf(node.Children.OfType<SchemaGroupNodeViewModel>().First()),
            "a folder was interleaved with the columns");
    }

    [Fact]
    public async Task A_table_gets_a_folder_per_kind_of_thing_it_has()
    {
        var node = Node(DemoCatalog.StoreId, new DetailBrowser());

        await node.EnsureChildrenAsync();

        // DBeaver's order, minus the folders this table has nothing in: store declares no foreign keys.
        Assert.Equal(
            ["Constraints", "References", "Indexes", "Triggers"],
            node.Children.OfType<SchemaGroupNodeViewModel>().Select(f => f.Title));
    }

    [Fact]
    public async Task An_empty_folder_is_left_out_entirely()
    {
        // A table with no triggers should not have to say so, and a folder that opens onto nothing is worse
        // than no folder.
        var node = Node(DemoCatalog.PaymentId, new DetailBrowser());

        await node.EnsureChildrenAsync();

        Assert.Null(Folder(node, "Triggers"));
        Assert.NotNull(Folder(node, "Indexes"));
    }

    [Fact]
    public async Task A_folder_says_how_many_things_are_in_it()
    {
        var node = Node(DemoCatalog.StoreId, new DetailBrowser());

        await node.EnsureChildrenAsync();

        var indexes = Folder(node, "Indexes")!;
        Assert.Equal("2", indexes.Detail);
        Assert.Equal(2, indexes.Children.Count);
    }

    [Fact]
    public async Task The_two_foreign_key_directions_are_separate_folders()
    {
        // The distinction worth having: outgoing answers "what does this row point at", incoming answers
        // "what breaks if I delete it". ForeignKeysTouching returns both, so this is a partition.
        var payment = Node(DemoCatalog.PaymentId, new DetailBrowser());
        var store = Node(DemoCatalog.StoreId, new DetailBrowser());

        await payment.EnsureChildrenAsync();
        await store.EnsureChildrenAsync();

        // payment declares the key, so it is outgoing there and incoming on store.
        Assert.NotNull(Folder(payment, "Foreign Keys"));
        Assert.Null(Folder(payment, "References"));
        Assert.Null(Folder(store, "Foreign Keys"));
        Assert.NotNull(Folder(store, "References"));
    }

    [Fact]
    public async Task A_foreign_key_is_not_also_listed_as_a_constraint()
    {
        // It is one object; listing it twice under one table makes both counts lie.
        var node = Node(DemoCatalog.PaymentId, new DetailBrowser());

        await node.EnsureChildrenAsync();

        var constraints = Folder(node, "Constraints")!.Children.Select(c => c.Title).ToList();
        Assert.DoesNotContain("payment_store_id_fkey", constraints);
        Assert.Equal(["payment_store_id_fkey"], Folder(node, "Foreign Keys")!.Children.Select(c => c.Title));
    }

    [Fact]
    public async Task A_view_gets_no_folders_at_all()
    {
        var node = Node(DemoCatalog.ReceiptViewId, new DetailBrowser());

        await node.EnsureChildrenAsync();

        Assert.Empty(node.Children.OfType<SchemaGroupNodeViewModel>());
        Assert.NotEmpty(node.Children.OfType<ColumnNodeViewModel>());
    }

    [Fact]
    public async Task A_failed_read_never_quotes_the_connection_string()
    {
        // The details read opens a connection, and an Npgsql connect failure quotes the whole connection
        // string — password included. That text goes straight into a tree row, so it has to be scrubbed
        // (§1.1) the way every other failure on this path already is.
        var node = Node(DemoCatalog.PaymentId, new LeakyDetailBrowser());

        await node.EnsureChildrenAsync();

        var message = Assert.Single(node.Children.OfType<MessageNodeViewModel>());
        Assert.DoesNotContain("hunter2", message.Title);
        Assert.Contains("Password=***", message.Title);
        // The host stays: SafeErrorText keeps the endpoint on purpose, since that is the useful half of every
        // network, TLS and DNS error and it is the server the user configured themselves.
        Assert.Contains("db.example.com", message.Title);
    }

    [Fact]
    public async Task A_failed_read_still_leaves_the_columns_and_the_keys()
    {
        // The details need a round trip; the columns and both key directions come from the snapshot. Losing
        // all of them because the server said no would be a worse tree than before the folders existed.
        var node = Node(DemoCatalog.PaymentId, new ThrowingDetailBrowser());

        await node.EnsureChildrenAsync();

        Assert.NotEmpty(node.Children.OfType<ColumnNodeViewModel>());
        Assert.NotNull(Folder(node, "Foreign Keys"));
        Assert.Null(Folder(node, "Indexes"));
        var message = Assert.Single(node.Children.OfType<MessageNodeViewModel>());
        Assert.Contains("catalog is unreachable", message.Title);
    }

    [Fact]
    public async Task Expanding_a_table_reads_its_details_once()
    {
        var browser = new DetailBrowser();
        var node = Node(DemoCatalog.PaymentId, browser);

        await node.EnsureChildrenAsync();
        await node.EnsureChildrenAsync();

        Assert.Equal(1, browser.DetailCalls);
    }

    // ---- what the rows say ---------------------------------------------------------------------

    [Fact]
    public async Task An_outgoing_key_reads_as_the_join_it_would_become()
    {
        var node = Node(DemoCatalog.PaymentId, new DetailBrowser());
        await node.EnsureChildrenAsync();

        var fk = Assert.Single(Folder(node, "Foreign Keys")!.Children);
        Assert.Equal("payment_store_id_fkey", fk.Title);
        Assert.Equal("store_id → shop.store(id)", fk.Detail);
    }

    [Fact]
    public async Task An_incoming_reference_names_the_table_that_points_here()
    {
        var node = Node(DemoCatalog.StoreId, new DetailBrowser());
        await node.EnsureChildrenAsync();

        var reference = Assert.Single(Folder(node, "References")!.Children);
        Assert.Equal("shop.payment(store_id) → id", reference.Detail);
    }

    [Fact]
    public async Task A_check_constraint_shows_its_expression()
    {
        // The columns are no answer for a CHECK — the expression is the whole content of the row.
        var node = Node(DemoCatalog.PaymentId, new DetailBrowser());
        await node.EnsureChildrenAsync();

        var check = Folder(node, "Constraints")!.Children.Single(c => c.Title == "payment_amount_positive");
        Assert.Equal("check · CHECK ((amount > (0)::numeric))", check.Detail);
    }

    [Fact]
    public async Task A_key_constraint_shows_its_columns()
    {
        var node = Node(DemoCatalog.StoreId, new DetailBrowser());
        await node.EnsureChildrenAsync();

        var constraints = Folder(node, "Constraints")!.Children;
        Assert.Equal("primary key · id", constraints.Single(c => c.Title == "store_pkey").Detail);
        Assert.Equal("unique · name", constraints.Single(c => c.Title == "store_name_key").Detail);
    }

    [Fact]
    public async Task An_invalid_index_says_so_loudly()
    {
        // What a failed CREATE INDEX CONCURRENTLY leaves behind. The planner ignores it, which is exactly the
        // thing you are hunting when a query is slow despite "having an index".
        var node = Node(DemoCatalog.PaymentId, new DetailBrowser());
        await node.EnsureChildrenAsync();

        var index = Folder(node, "Indexes")!.Children.Single(i => i.Title == "payment_note_idx");
        Assert.Contains("INVALID", index.Detail);
        Assert.Equal("⚠", index.Glyph);
    }

    [Fact]
    public async Task An_expression_index_falls_back_to_its_definition()
    {
        // No resolvable column ordinals, so a column list would be empty and the row would say nothing.
        var node = Node(DemoCatalog.DocumentId, new DetailBrowser());
        await node.EnsureChildrenAsync();

        var index = Folder(node, "Indexes")!.Children.Single(i => i.Title == "document_channel_idx");
        Assert.Contains("USING btree", index.Detail);
        Assert.Contains("body ->>", index.Detail);
    }

    [Fact]
    public async Task A_trigger_shows_when_it_fires_and_whether_it_is_off()
    {
        var store = Node(DemoCatalog.StoreId, new DetailBrowser());
        var document = Node(DemoCatalog.DocumentId, new DetailBrowser());
        await store.EnsureChildrenAsync();
        await document.EnsureChildrenAsync();

        var enabled = Assert.Single(Folder(store, "Triggers")!.Children);
        Assert.Equal("after insert or update", enabled.Detail);

        // A disabled trigger looks identical to an enabled one everywhere else in the catalog.
        var disabled = Assert.Single(Folder(document, "Triggers")!.Children);
        Assert.Equal("before update · disabled", disabled.Detail);
        Assert.Equal("◌", disabled.Glyph);
    }

    [Fact]
    public async Task A_detail_row_can_show_the_definition_it_already_has()
    {
        // Fetched with the table's details, so there is no second round trip to make.
        var node = Node(DemoCatalog.StoreId, new DetailBrowser());
        await node.EnsureChildrenAsync();

        var index = Folder(node, "Indexes")!.Children.First();
        Assert.True(index.CanShowDefinition);
        Assert.StartsWith("CREATE UNIQUE INDEX", await index.LoadDefinitionAsync(CancellationToken.None));
    }

    // ---- the pure helper, at the edges ----------------------------------------------------------

    [Fact]
    public void An_unresolvable_ordinal_is_shown_rather_than_dropped()
    {
        // Dropping it would silently understate a key — a two-column unique constraint reading as one column
        // is a wrong answer, where "#7" is a visible gap.
        var snapshot = DemoCatalog.Snapshot();

        Assert.Equal("id, #7", RelationDetailText.Columns(snapshot, DemoCatalog.StoreId, [1, 7]));
    }

    [Fact]
    public void A_key_pointing_at_an_unknown_table_still_renders()
    {
        // A foreign key can reference a table in a schema the snapshot filtered out. Its columns cannot be
        // named either, so they come out as ordinals — a row that is honest about what it does not know,
        // rather than one that looks like a key on nothing.
        var snapshot = DemoCatalog.Snapshot();
        var fk = new ForeignKeyInfo(9, "orphan_fkey", DemoCatalog.PaymentId, [2], 999_999, [1]);

        Assert.Equal("store_id → (table 999999)(#1)", RelationDetailText.Outgoing(snapshot, fk));
    }

    [Fact]
    public void A_self_referencing_key_is_both_a_key_and_a_reference()
    {
        // It is what a row points at *and* what would break — leaving it out of one folder is how a
        // parent_id gets missed.
        var tree = new SchemaSnapshot("db", ["s"],
            [new TableInfo(1, "s", "node", RelationKind.Table)],
            [
                new ColumnInfo(1, 1, "id", "int4", NotNull: true, IsPrimaryKey: true),
                new ColumnInfo(1, 2, "parent_id", "int4", NotNull: false, IsPrimaryKey: false),
            ],
            [new ForeignKeyInfo(7, "node_parent_fkey", 1, [2], 1, [1])]);

        var (outgoing, incoming) = RelationDetailText.SplitByDirection(tree, 1);

        Assert.Single(outgoing);
        Assert.Single(incoming);
    }

    // ---- generated DDL --------------------------------------------------------------------------

    [Fact]
    public void An_index_a_constraint_owns_is_not_re_issued_by_the_ddl()
    {
        // Skipped by ownership rather than by shape. Comparing column sets dropped genuinely separate indexes
        // that happened to cover the same columns, and kept the index behind an exclusion constraint — which
        // is neither primary nor unique, so every other test let it through and the DDL then failed on a
        // duplicate name.
        var snapshot = DemoCatalog.Snapshot();
        var table = snapshot.Tables.Single(t => t.Id == DemoCatalog.StoreId);
        var details = new TableDetails(
            [new ConstraintInfo(1, "store_room_excl", ConstraintKind.Exclusion, [2, 3],
                "EXCLUDE USING gist (name WITH =, active WITH =)")],
            [
                new IndexInfo(2, "store_room_excl", IsUnique: false, IsPrimary: false, IsValid: true, [2, 3],
                    "CREATE INDEX store_room_excl ON shop.store USING gist (name, active)",
                    BackedByConstraint: true),
                // Same columns as the constraint, but its own index — it must survive.
                new IndexInfo(3, "store_lookup_idx", IsUnique: false, IsPrimary: false, IsValid: true, [2, 3],
                    "CREATE INDEX store_lookup_idx ON shop.store USING btree (name, active)"),
            ],
            []);

        var ddl = Bearing.Sql.TableDdlGenerator.CreateTable(table, snapshot, details);

        Assert.Contains("constraint \"store_room_excl\" EXCLUDE USING gist", ddl);
        Assert.DoesNotContain("CREATE INDEX store_room_excl", ddl);
        Assert.Contains("CREATE INDEX store_lookup_idx", ddl);
    }

    [Fact]
    public void The_generated_ddl_now_carries_constraints_and_indexes()
    {
        // The generator's own note admitted this hole: "indexes, defaults, checks are omitted".
        var snapshot = DemoCatalog.Snapshot();
        var table = snapshot.Tables.Single(t => t.Id == DemoCatalog.PaymentId);

        var ddl = Bearing.Sql.TableDdlGenerator.CreateTable(table, snapshot, DemoCatalog.DetailsOf(DemoCatalog.PaymentId));

        Assert.Contains("constraint \"payment_amount_positive\" CHECK ((amount > (0)::numeric))", ddl);
        Assert.Contains("CREATE INDEX payment_store_id_idx", ddl);
        // The index behind the primary key is implied by the key itself; re-issuing it would fail.
        Assert.DoesNotContain("payment_pkey ON", ddl);
    }

    [Fact]
    public void A_unique_constraint_does_not_also_emit_its_index()
    {
        var snapshot = DemoCatalog.Snapshot();
        var table = snapshot.Tables.Single(t => t.Id == DemoCatalog.StoreId);

        var ddl = Bearing.Sql.TableDdlGenerator.CreateTable(table, snapshot, DemoCatalog.DetailsOf(DemoCatalog.StoreId));

        Assert.Contains("constraint \"store_name_key\" UNIQUE (name)", ddl);
        Assert.DoesNotContain("CREATE UNIQUE INDEX store_name_key", ddl);
    }

    [Fact]
    public void Without_a_details_read_the_ddl_is_what_it_always_was()
    {
        // Null means "not read" — a caller with no server, or a read that failed. It must not become an
        // assertion that the table has no constraints.
        var snapshot = DemoCatalog.Snapshot();
        var table = snapshot.Tables.Single(t => t.Id == DemoCatalog.PaymentId);

        var ddl = Bearing.Sql.TableDdlGenerator.CreateTable(table, snapshot);

        Assert.Contains("create table \"shop\".\"payment\"", ddl);
        Assert.Contains("primary key (\"id\")", ddl);
        Assert.DoesNotContain("CREATE INDEX", ddl);
        Assert.DoesNotContain("constraint", ddl);
    }

    // ---- doubles --------------------------------------------------------------------------------

    /// <summary>A browser that answers from the demo catalog and counts the detail reads.</summary>
    private class DetailBrowser : ISchemaBrowser
    {
        public int DetailCalls;

        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([DemoCatalog.Database]);

        public Task<DatabaseObjects> GetObjectsAsync(ConnectionInfo connection, string database, CancellationToken ct)
            => Task.FromResult(new DatabaseObjects(DemoCatalog.Snapshot(), DemoCatalog.Routines()));

        public Task<string> GetViewDefinitionAsync(ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => Task.FromResult("select 1");

        public virtual Task<TableDetails> GetTableDetailsAsync(
            ConnectionInfo connection, string database, long tableId, CancellationToken ct)
        {
            Interlocked.Increment(ref DetailCalls);
            return Task.FromResult(DemoCatalog.DetailsOf(tableId));
        }

        public Task<string> GetRoutineDefinitionAsync(ConnectionInfo connection, string database, long routineId, CancellationToken ct)
            => Task.FromResult("create function …");

        public Task InvalidateAsync(Guid connectionId) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A browser whose failure carries a credential, as a real driver's does.</summary>
    private sealed class LeakyDetailBrowser : DetailBrowser
    {
        public override Task<TableDetails> GetTableDetailsAsync(
            ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => throw new InvalidOperationException(
                "Failed to connect: Host=db.example.com;Username=u;Password=hunter2;Database=app");
    }

    private sealed class ThrowingDetailBrowser : DetailBrowser
    {
        public override Task<TableDetails> GetTableDetailsAsync(
            ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => throw new InvalidOperationException("catalog is unreachable");
    }
}
