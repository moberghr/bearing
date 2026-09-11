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
/// The object kinds the explorer gained in #119 — sequences, types, extensions, policies — plus the
/// Functions/Procedures split. What is asserted here is how the <b>tree</b> behaves given a shape;
/// resolution against a real catalog belongs in <c>Bearing.Data.Tests</c> against pagila (§4.6).
/// </summary>
public class DatabaseObjectKindTests
{
    private static ConnectionInfo Conn() => new()
    {
        Id = Guid.NewGuid(),
        Name = "srv",
        ProviderId = "postgres",
        Host = "h",
        Port = 5432,
        Database = "app",
        User = "u",
    };

    /// <summary>Expand a database node and wait for the late kind groups to append themselves.</summary>
    private static async Task<DatabaseNodeViewModel> Expanded(ISchemaBrowser browser)
    {
        var db = new DatabaseNodeViewModel(Conn(), "app", isConnected: true, browser);
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        db.ObjectKindsLoaded = () => landed.TrySetResult();
        await db.EnsureChildrenAsync();
        // The groups are fetched after the tree is handed back (#76's pattern), so nothing in the app waits
        // on them — which is exactly why the node raises an event a test can.
        await Task.WhenAny(landed.Task, Task.Delay(5000));
        return db;
    }

    private static IReadOnlyList<string> Groups(DatabaseNodeViewModel db)
        => db.Children.OfType<SchemaGroupNodeViewModel>().Select(g => g.Title).ToList();

    /// <summary>
    /// A group by title, wherever the current shape puts it. These tests are about what a <em>row</em> says,
    /// not about where its group sits, so they look through the buckets #132 introduced rather than pinning a
    /// depth — the arrangement itself is asserted deliberately, in its own tests.
    /// </summary>
    private static SchemaGroupNodeViewModel Group(SchemaNodeViewModel node, string title)
        => AllGroups(node).Single(g => g.Title == title);

    private static IEnumerable<SchemaGroupNodeViewModel> AllGroups(SchemaNodeViewModel node)
        => node.Children.OfType<SchemaGroupNodeViewModel>()
            .SelectMany(g => new[] { g }.Concat(AllGroups(g)));

    // ---- the groups -----------------------------------------------------------------------------------

    [Fact]
    public async Task Each_kind_gets_its_own_collapsed_group()
    {
        var db = await Expanded(new KindBrowser());

        // Simple mode (#132): the relations stay inline with Views, Functions and Procedures beside them,
        // and every rarer kind moves inside one bucket. Seventeen sibling rows became at most five.
        Assert.Equal(["Views", "Functions", "Procedures", "Other objects"],
            db.Children.OfType<SchemaGroupNodeViewModel>().Select(g => g.Title).Where(t => t != "Schemas"));

        // The kinds are still each their own group — one level further in, and still in kind order.
        Assert.Equal(["Sequences", "Types", "Policies"],
            Group(db, "Other objects").Children.OfType<SchemaGroupNodeViewModel>()
                .Select(g => g.Title).Where(t => t is "Sequences" or "Types" or "Policies"));

        // Collapsed, like Views and Functions already were: these are the "look it up when you need it" half
        // of a database, and expanding them by default would bury the tables.
        Assert.All(AllGroups(db), g => Assert.False(g.IsExpanded));
    }

    [Fact]
    public async Task The_kind_groups_come_after_the_relations()
    {
        // Appended rather than interleaved: the tables are what nearly every expand is for, and rows
        // arriving above them later would shift the list under someone already reading it.
        var db = await Expanded(new KindBrowser());

        var firstGroup = db.Children.ToList().FindIndex(c => c is SchemaGroupNodeViewModel);
        Assert.All(db.Children.Take(firstGroup), c => Assert.IsType<RelationNodeViewModel>(c));
    }

    [Fact]
    public async Task An_empty_kind_is_left_out_rather_than_shown_empty()
    {
        var db = await Expanded(new KindBrowser { Kinds = DatabaseObjectKinds.Empty });

        Assert.DoesNotContain("Sequences", Groups(db));
        Assert.DoesNotContain("Types", Groups(db));
        Assert.DoesNotContain("Extensions", Groups(db));
        Assert.DoesNotContain("Policies", Groups(db));
    }

    [Fact]
    public async Task A_failed_kind_read_leaves_the_tree_it_already_built()
    {
        // Silent on failure, like the sizes (#76): these groups are an addition, and a permission error must
        // not turn an expanded tree into an error message.
        var db = await Expanded(new KindBrowser { Throw = true });

        Assert.Contains(db.Children, c => c is RelationNodeViewModel);
        Assert.DoesNotContain("Sequences", Groups(db));
    }

    // ---- functions and procedures ---------------------------------------------------------------------

    [Fact]
    public async Task Procedures_are_their_own_group_rather_than_filed_under_functions()
    {
        // RoutineKind already distinguished them and the tree did not, so a CALL-only routine sat under
        // "Functions" — and CALL versus SELECT is a difference you need at the point of use.
        var db = await Expanded(new KindBrowser());

        Assert.Equal(["gross_revenue", "median"], Group(db, "Functions").Children.Select(c => c.Title));
        Assert.Equal(["rebuild_totals"], Group(db, "Procedures").Children.Select(c => c.Title));
    }

    [Fact]
    public async Task Aggregates_and_window_functions_stay_with_the_functions()
    {
        // They are called the same way a function is. The split is about how you invoke the thing, not about
        // how pg_proc classifies it.
        var db = await Expanded(new KindBrowser());
        Assert.Contains("median", Group(db, "Functions").Children.Select(c => c.Title));
    }

    [Fact]
    public async Task A_database_with_no_procedures_grows_no_procedures_group()
    {
        var db = await Expanded(new KindBrowser { Routines = [Function("only_a_function")] });

        Assert.Contains("Functions", Groups(db));
        Assert.DoesNotContain("Procedures", Groups(db));
    }

    // ---- the rows -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sequence_row_says_where_it_is_and_what_it_feeds()
    {
        var db = await Expanded(new KindBrowser());
        var rows = Group(db, "Sequences").Children.Cast<SequenceNodeViewModel>().ToList();

        var payment = rows.Single(r => r.Title == "payment_id_seq");
        Assert.Contains("at 8", payment.Detail);
        Assert.Contains("owned by shop.payment.id", payment.Detail);

        // Never read from: "not yet used", not 0 — rendering it as zero would claim the first id is gone.
        var receipt = rows.Single(r => r.Title == "receipt_no_seq");
        Assert.Contains("not yet used", receipt.Detail);
        Assert.Contains("by 10", receipt.Detail);
        Assert.Contains("cycles", receipt.Detail);
    }

    [Fact]
    public async Task A_sequence_hands_over_statements_rather_than_running_them()
    {
        // Setting a sequence is how a primary key breaks, so the menu copies SQL for the editor and the
        // write guard instead of doing it behind a click.
        var db = await Expanded(new KindBrowser());
        var payment = Group(db, "Sequences").Children.Cast<SequenceNodeViewModel>()
            .Single(r => r.Title == "payment_id_seq");

        Assert.True(payment.IsSequence);
        Assert.Equal("select nextval('shop.payment_id_seq')", payment.NextvalSql);
        Assert.Equal("select setval('shop.payment_id_seq', 8)", payment.SetvalSql);
    }

    [Fact]
    public async Task A_sequence_with_an_awkward_name_is_quoted_in_the_copied_sql()
    {
        var db = await Expanded(new KindBrowser
        {
            Kinds = DatabaseObjectKinds.Empty with
            {
                Sequences = [new SequenceInfo(1, "Odd Schema", "order", "bigint", 3, 1, 1, 99, false, null)],
            },
        });
        var row = (SequenceNodeViewModel)Group(db, "Sequences").Children[0];

        // `order` is a keyword and the schema has a space: unquoted, the paste would not parse.
        Assert.Equal("""select nextval('"Odd Schema"."order"')""", row.NextvalSql);
    }

    [Fact]
    public async Task A_type_row_says_what_it_holds()
    {
        var db = await Expanded(new KindBrowser());
        var rows = Group(db, "Types").Children.ToList();

        var state = rows.Single(r => r.Title == "payment_state");
        Assert.StartsWith("enum · ", state.Detail);
        Assert.Contains("'pending'", state.Detail);
        // An enum's labels are exactly what you look up mid-query, so they are on the row itself.
        Assert.Contains("'refunded'", state.Detail);

        Assert.StartsWith("domain · ", rows.Single(r => r.Title == "positive_amount").Detail);
        Assert.StartsWith("composite · ", rows.Single(r => r.Title == "address").Detail);
    }

    [Fact]
    public async Task A_type_offers_its_ddl()
    {
        var db = await Expanded(new KindBrowser());
        var state = (TypeNodeViewModel)Group(db, "Types").Children.Single(r => r.Title == "payment_state");

        Assert.True(state.CanShowDefinition);
        Assert.Equal(
            "CREATE TYPE shop.payment_state AS ENUM ('pending', 'settled', 'refunded');",
            await state.LoadDefinitionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_extension_row_says_its_version()
    {
        var db = await Expanded(new KindBrowser());
        var rows = Group(db, "Extensions").Children.ToList();

        Assert.Equal(["pgcrypto", "pg_stat_statements"], rows.Select(r => r.Title));
        Assert.Equal("1.3 · in public", rows[0].Detail);
    }

    [Fact]
    public async Task A_policy_row_says_what_it_covers_and_whether_it_restricts()
    {
        var db = await Expanded(new KindBrowser());
        var rows = Group(db, "Policies").Children.ToList();

        var own = rows.Single(r => r.Title.StartsWith("payment_own_store"));
        Assert.Contains("ALL", own.Detail);
        Assert.Contains("permissive", own.Detail);
        Assert.Contains("to app_user", own.Detail);

        // Restrictive is the one that can only ever remove access, so the row has to distinguish it.
        var refunds = rows.Single(r => r.Title.StartsWith("payment_no_refunds"));
        Assert.Contains("restrictive", refunds.Detail);
        Assert.Contains("UPDATE", refunds.Detail);
    }

    [Fact]
    public async Task A_policy_in_the_database_group_names_its_table()
    {
        // Two policies of the same name on different tables would otherwise be indistinguishable in a
        // per-database list.
        var db = await Expanded(new KindBrowser());

        Assert.All(Group(db, "Policies").Children, r => Assert.Contains(" on ", r.Title));
    }

    [Fact]
    public async Task A_policy_offers_both_of_its_expressions()
    {
        var db = await Expanded(new KindBrowser());
        var refunds = (PolicyNodeViewModel)Group(db, "Policies").Children
            .Single(r => r.Title.StartsWith("payment_no_refunds"));

        var definition = await refunds.LoadDefinitionAsync(CancellationToken.None);
        Assert.Contains("WITH CHECK", definition);
        Assert.Contains("restrictive", definition);
    }

    // ---- policies under their table -------------------------------------------------------------------

    [Fact]
    public async Task A_table_s_policies_appear_under_the_table_itself()
    {
        // Beside the constraints and triggers, because that is where you are standing when a query returns
        // fewer rows than you expect.
        var relation = await ExpandedRelation(DemoCatalog.PaymentId);

        var policies = relation.Children.OfType<SchemaGroupNodeViewModel>().Single(g => g.Title == "Policies");
        Assert.Equal(2, policies.Children.Count);
        Assert.All(policies.Children, c => Assert.IsType<PolicyNodeViewModel>(c));
    }

    [Fact]
    public async Task A_table_without_policies_grows_no_policies_folder()
    {
        var relation = await ExpandedRelation(DemoCatalog.StoreId);

        Assert.DoesNotContain(
            "Policies",
            relation.Children.OfType<SchemaGroupNodeViewModel>().Select(g => g.Title));
    }

    [Fact]
    public async Task Under_its_own_table_a_policy_row_does_not_repeat_the_table_name()
    {
        // It is already implied by where the row is; repeating it is noise on a narrow panel — and the
        // schema-tree capture showed exactly that, `payment_own_st(` clipped where the whole name would
        // have fitted.
        //
        // This assertion used to say " on shop.payment" while the test's *name* said it should not: written
        // from the design, then made to match what the code did. The capture is what caught the gap.
        var relation = await ExpandedRelation(DemoCatalog.PaymentId);
        var policies = relation.Children.OfType<SchemaGroupNodeViewModel>().Single(g => g.Title == "Policies");

        Assert.Equal(["payment_own_store", "payment_no_refunds"], policies.Children.Select(c => c.Title));

        // …while the per-database group still qualifies them, because there the table is the distinguisher.
        Assert.All(
            Group(await Expanded(new KindBrowser()), "Policies").Children,
            row => Assert.Contains(" on ", row.Title));
    }

    private static async Task<RelationNodeViewModel> ExpandedRelation(long tableId)
    {
        var browser = new KindBrowser();
        var snapshot = DemoCatalog.Snapshot();
        var table = snapshot.Tables.Single(t => t.Id == tableId);
        var relation = new RelationNodeViewModel(Conn(), "app", table, snapshot, browser, "shop");
        await relation.EnsureChildrenAsync();
        return relation;
    }

    // ---- the fake --------------------------------------------------------------------------------------

    private static RoutineInfo Function(string name)
        => new(3001, "shop", name, RoutineKind.Function, "()", "numeric");

    /// <summary>
    /// A browser over the demo catalog, with routines covering all four <see cref="RoutineKind"/>s and the
    /// #119 kinds coming from <see cref="DemoCatalog.ObjectKinds"/> — so the fixture the demo ships and the
    /// fixture the tree is tested against are the same one (§4.6).
    /// </summary>
    private sealed class KindBrowser : ISchemaBrowser
    {
        public DatabaseObjectKinds Kinds { get; init; } = DemoCatalog.ObjectKinds();
        public bool Throw { get; init; }

        public IReadOnlyList<RoutineInfo> Routines { get; init; } =
        [
            new(3001, "shop", "gross_revenue", RoutineKind.Function, "(from_date date)", "numeric"),
            new(3002, "shop", "rebuild_totals", RoutineKind.Procedure, "()", ""),
            new(3003, "shop", "median", RoutineKind.Aggregate, "(numeric)", "numeric"),
        ];

        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(["app"]);

        public Task<DatabaseObjects> GetObjectsAsync(ConnectionInfo connection, string database, CancellationToken ct)
            => Task.FromResult(new DatabaseObjects(DemoCatalog.Snapshot(), Routines));

        public Task<DatabaseObjectKinds> GetDatabaseObjectKindsAsync(
            ConnectionInfo connection, string database, CancellationToken ct)
            => Throw
                ? Task.FromException<DatabaseObjectKinds>(new InvalidOperationException("permission denied"))
                : Task.FromResult(Kinds);

        /// <summary>#120's roles. Empty here: these fixtures exist for other questions, and a Roles group
        /// they never assert on would only add noise to the trees they build.</summary>
        public Task<IReadOnlyList<RoleInfo>> GetRolesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RoleInfo>>([]);

        public Task<RoleGrants> GetRoleGrantsAsync(
            ConnectionInfo connection, string database, string roleName, CancellationToken ct)
            => Task.FromResult(RoleGrants.Of([]));

        /// <summary>Tablespaces (#119 follow-up). Empty here for the same reason the roles are.</summary>
        public Task<IReadOnlyList<SchemaObjectInfo>> GetTablespacesAsync(
            ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SchemaObjectInfo>>([]);

        public Task<TableDetails> GetTableDetailsAsync(
            ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => Task.FromResult(DemoCatalog.DetailsOf(tableId));

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

        public Task InvalidateAsync(Guid connectionId) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
