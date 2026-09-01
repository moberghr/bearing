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
/// Object sizes in the schema tree (#76). Nothing said how big anything was, so "which table is eating the
/// disk?" and "is this index worth its cost?" meant leaving the app and writing
/// <c>pg_total_relation_size</c> by hand.
/// </summary>
public class ObjectSizeTests
{
    private static ConnectionInfo Conn()
        => new() { Id = Guid.NewGuid(), Name = "srv", ProviderId = "demo", Database = "demo", User = "u" };

    // ---- the formatter ---------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 kB")]
    [InlineData(8192, "8.0 kB")]
    [InlineData(1_048_576, "1.0 MB")]
    [InlineData(1_503_238_553, "1.4 GB")]
    [InlineData(16_384_000_000, "15 GB")]
    public void A_size_reads_the_way_psql_would_report_it(long bytes, string expected)
    {
        // Powers of 1024 with Postgres's own unit names: the user will compare these numbers against what
        // pg_size_pretty told them, and matching it matters more than being pedantic about kB versus KiB.
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Fact]
    public void A_size_never_rounds_up_into_the_unit_above_it()
    {
        // 1023.97 kB formatted to one decimal is "1024.0 kB", which reads as a unit the next one up should
        // have taken.
        Assert.Equal("1.0 MB", ByteSize.Format(1024 * 1024 - 1));
        Assert.Equal("1.0 kB", ByteSize.Format(1024));
    }

    [Fact]
    public void One_decimal_below_ten_and_none_above_keeps_a_column_of_sizes_narrow()
    {
        Assert.Equal("9.8 MB", ByteSize.Format(10_276_045));
        Assert.Equal("98 MB", ByteSize.Format(102_760_448));
    }

    [Fact]
    public void Bytes_never_get_a_decimal()
        => Assert.Equal("512 B", ByteSize.Format(512));

    [Fact]
    public void A_negative_size_is_unknown_rather_than_a_number()
        => Assert.Equal("?", ByteSize.Format(-1));

    [Fact]
    public void A_row_estimate_says_it_is_one()
    {
        // A number presented as exact when ANALYZE last ran a week ago is worse than one presented as
        // approximate.
        Assert.Equal("~1,234 rows", ByteSize.FormatRows(1234));
        Assert.Equal("~0 rows", ByteSize.FormatRows(0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    public void A_never_analysed_table_has_no_row_count_at_all(long? reltuples)
    {
        // reltuples is -1 on a never-analysed table, which has to render as unknown rather than as a count.
        Assert.Null(ByteSize.FormatRows(reltuples));
    }

    // ---- what a row says ------------------------------------------------------------------------

    [Fact]
    public void A_size_lands_on_the_same_line_as_the_rest_of_the_detail()
    {
        // #71 made these rows tighter on purpose, so a size is a field on the line rather than a new one —
        // and it leads, because the 262px panel ellipsizes the line and a size at the end was being cut to
        // "9." (a capture of the tree caught it; the kind and schema are recoverable from the icon and the
        // title, so they are the half that can afford to be clipped).
        var detail = SchemaObjectLabel.WithSize("table · shop", Size(total: 1_638_400, rows: 40));

        Assert.Equal("1.6 MB · ~40 rows · table · shop", detail);
        Assert.StartsWith("1.6 MB", detail);
        Assert.DoesNotContain('\n', detail);
    }

    [Fact]
    public void A_never_analysed_relation_shows_its_size_and_no_row_count()
    {
        var detail = SchemaObjectLabel.WithSize("table · shop", Size(total: 24_576, rows: null));

        Assert.Equal("24 kB · table · shop", detail);
    }

    [Fact]
    public void The_breakdown_splits_heap_from_indexes()
    {
        // The part worth getting right: a table that is mostly indexes is a different problem from one that
        // is mostly heap, and one "size" number hides which you have.
        var breakdown = SchemaObjectLabel.SizeBreakdown(new RelationSize(
            1, TotalBytes: 2_147_483_648, TableBytes: 419_430_400, IndexBytes: 1_717_986_918,
            ToastBytes: 0, EstimatedRows: 9_000_000));

        Assert.Contains("total    2.0 GB", breakdown);
        Assert.Contains("heap     400 MB", breakdown);
        Assert.Contains("indexes  1.6 GB", breakdown);
        Assert.Contains("rows     ~9,000,000 rows", breakdown);
        // No toast line when there is no toast: every table would otherwise carry one saying nothing.
        Assert.DoesNotContain("toast", breakdown);
    }

    [Fact]
    public void The_breakdown_says_unknown_rather_than_zero_for_a_never_analysed_table()
    {
        var breakdown = SchemaObjectLabel.SizeBreakdown(Size(total: 8192, rows: null));

        Assert.Contains("never analysed", breakdown);
        Assert.DoesNotContain("~0 rows", breakdown);
    }

    // ---- the tree -------------------------------------------------------------------------------

    [Fact]
    public async Task A_table_row_picks_up_its_size_after_the_tree_has_rendered()
    {
        // pg_total_relation_size stats files per relation, so the tree must render first and the rows
        // re-label themselves when the read lands (#76).
        var browser = new SizeBrowser();
        var database = new DatabaseNodeViewModel(Conn(), DemoCatalog.Database, isConnected: true, browser);
        var landed = new TaskCompletionSource();

        await database.EnsureChildrenAsync();
        var payment = Relation(database, "payment");
        // The row exists before the size does — that is the whole point of loading them late.
        Assert.Null(payment.Size);

        database.SizesLoaded = landed.SetResult;
        await browser.Release(landed.Task);

        Assert.NotNull(payment.Size);
        Assert.Contains("1.6 MB", payment.Detail);
        Assert.Contains("~40 rows", payment.Detail);
    }

    [Fact]
    public async Task The_row_is_re_labelled_in_place_rather_than_replaced()
    {
        // Replacing nodes would collapse whatever the user had expanded while the sizes were loading.
        var browser = new SizeBrowser();
        var database = new DatabaseNodeViewModel(Conn(), DemoCatalog.Database, isConnected: true, browser);
        var landed = new TaskCompletionSource();

        await database.EnsureChildrenAsync();
        var before = Relation(database, "store");
        database.SizesLoaded = landed.SetResult;
        await browser.Release(landed.Task);

        Assert.Same(before, Relation(database, "store"));
    }

    [Fact]
    public async Task A_size_read_that_fails_leaves_the_tree_alone()
    {
        // Sizes are a nicety: a permission error or a slow catalog must not turn an expanded tree into an
        // error message.
        var database = new DatabaseNodeViewModel(Conn(), DemoCatalog.Database, isConnected: true,
            new ThrowingSizeBrowser());

        await database.EnsureChildrenAsync();

        Assert.NotEmpty(database.Children.OfType<RelationNodeViewModel>());
        Assert.All(database.Children.OfType<RelationNodeViewModel>(), r => Assert.Null(r.Size));
        Assert.Empty(database.Children.OfType<MessageNodeViewModel>());
    }

    [Fact]
    public async Task A_relation_with_no_size_reported_keeps_its_plain_detail()
    {
        // A view has no storage of its own, so it is simply absent from the size read.
        var browser = new SizeBrowser();
        var database = new DatabaseNodeViewModel(Conn(), DemoCatalog.Database, isConnected: true, browser);
        var landed = new TaskCompletionSource();

        await database.EnsureChildrenAsync();
        database.SizesLoaded = landed.SetResult;
        await browser.Release(landed.Task);

        var view = database.Children
            .OfType<SchemaGroupNodeViewModel>()
            .SelectMany(g => g.Children)
            .OfType<RelationNodeViewModel>()
            .Single(r => r.Title.EndsWith("receipt", StringComparison.Ordinal));
        Assert.Null(view.Size);
        Assert.DoesNotContain("B", view.Detail ?? "");
    }

    [Fact]
    public void An_index_row_carries_its_own_size()
    {
        // Without it an index row answers only half of "is this index worth keeping".
        var snapshot = DemoCatalog.Snapshot();
        var index = new IndexInfo(1, "payment_store_id_idx", IsUnique: false, IsPrimary: false, IsValid: true,
            [2], "CREATE INDEX …", SizeBytes: 425_984);

        var node = new IndexNodeViewModel(snapshot, DemoCatalog.PaymentId, index, index.SizeBytes);

        Assert.Contains("416 kB", node.Detail);
        Assert.Contains("store_id", node.Detail);
    }

    [Fact]
    public void An_index_whose_size_was_not_read_says_nothing_about_it()
    {
        var snapshot = DemoCatalog.Snapshot();
        var index = new IndexInfo(1, "idx", IsUnique: false, IsPrimary: false, IsValid: true, [2], "CREATE …");

        var node = new IndexNodeViewModel(snapshot, DemoCatalog.PaymentId, index, index.SizeBytes);

        Assert.Equal("index · store_id", node.Detail);
    }

    [Fact]
    public void An_inaccessible_database_reports_an_unknown_size_rather_than_zero()
    {
        // pg_database_size raises for a database the user cannot connect to, so unknown is a normal outcome.
        var sizes = DemoCatalog.DatabaseSizes();

        Assert.Null(sizes.Single(d => d.Database == "postgres").Bytes);
        Assert.NotNull(sizes.Single(d => d.Database == DemoCatalog.Database).Bytes);
    }

    [Fact]
    public void The_rounding_guard_catches_the_case_it_was_written_for()
    {
        // 1023.6 kB rounds to "1024" at zero decimals — the exact "reads as a unit the next one up should
        // have taken" outcome, which a guard rounding at one decimal let through.
        Assert.Equal("1.0 MB", ByteSize.Format(1_048_192));
        Assert.DoesNotContain("1024", ByteSize.Format(1_048_192));

        // And nothing legitimate was pushed up a unit with it.
        Assert.Equal("999 kB", ByteSize.Format(1_023_000));
    }

    [Fact]
    public async Task A_database_row_shows_its_size_on_the_server()
    {
        // The API existed and nothing called it: the commit said "database sizes" and no row was labelled.
        var browser = new SizeBrowser();
        var server = new ServerNodeViewModel(Conn(), browser);
        var landed = new TaskCompletionSource();
        server.DatabaseSizesLoaded = landed.SetResult;

        await server.EnsureChildrenAsync();
        await landed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var demo = server.Children.OfType<DatabaseNodeViewModel>().Single(d => d.Database == DemoCatalog.Database);
        // 10.66 MB, which takes no decimal because it is over ten — the same as pg_size_pretty's "11 MB".
        Assert.Contains("11 MB", demo.Detail);
        // And it keeps saying it is the connected one.
        Assert.Contains("connected", demo.Detail);
    }

    [Fact]
    public async Task A_database_whose_size_is_unknown_says_nothing_rather_than_zero()
    {
        // pg_database_size raises for a database the user cannot connect to, so null is a normal answer.
        var browser = new SizeBrowser();
        var server = new ServerNodeViewModel(Conn(), browser);
        var landed = new TaskCompletionSource();
        server.DatabaseSizesLoaded = landed.SetResult;

        await server.EnsureChildrenAsync();
        await landed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var other = server.Children.OfType<DatabaseNodeViewModel>().SingleOrDefault(d => d.Database == "postgres");
        if (other is not null) Assert.DoesNotContain("B", other.Detail ?? "");
    }

    // ---- ordering by size -----------------------------------------------------------------------

    [Fact]
    public async Task Sorting_by_size_puts_the_biggest_table_first()
    {
        // "Which table is eating the disk" is the actual question, and a tree sorted by name cannot answer it.
        var browser = new SizeBrowser();
        var database = new DatabaseNodeViewModel(Conn(), DemoCatalog.Database, isConnected: true, browser);
        var landed = new TaskCompletionSource();

        await database.EnsureChildrenAsync();
        database.SizesLoaded = landed.SetResult;
        await browser.Release(landed.Task);

        database.SetRelationOrder(DatabaseNodeViewModel.RelationOrder.Size);

        // document (9 MB) then payment (1.6 MB) then store (80 kB) then metric (24 kB).
        Assert.Equal(
            ["document", "payment", "store", "metric"],
            database.Children.OfType<RelationNodeViewModel>().Select(r => Name(r)));
    }

    [Fact]
    public async Task Sorting_back_by_name_restores_the_order_the_rows_loaded_in()
    {
        // Not merely alphabetical: the load order is schema-rank, then schema, then kind, then name. Sorting
        // on the title alone interleaved the default schema's bare names among the qualified ones and dropped
        // the kind ranking, so the item labelled as the default sort could not restore it.
        var browser = new SizeBrowser();
        var database = new DatabaseNodeViewModel(Conn(), DemoCatalog.Database, isConnected: true, browser);
        var landed = new TaskCompletionSource();
        await database.EnsureChildrenAsync();
        var loaded = database.Children.OfType<RelationNodeViewModel>().ToList();
        database.SizesLoaded = landed.SetResult;
        await browser.Release(landed.Task);

        database.SetRelationOrder(DatabaseNodeViewModel.RelationOrder.Size);
        database.SetRelationOrder(DatabaseNodeViewModel.RelationOrder.Name);

        Assert.Equal(loaded, database.Children.OfType<RelationNodeViewModel>());
    }

    [Fact]
    public async Task Sorting_back_by_name_restores_alphabetical_order()
    {
        var browser = new SizeBrowser();
        var database = new DatabaseNodeViewModel(Conn(), DemoCatalog.Database, isConnected: true, browser);
        var landed = new TaskCompletionSource();
        await database.EnsureChildrenAsync();
        database.SizesLoaded = landed.SetResult;
        await browser.Release(landed.Task);

        database.SetRelationOrder(DatabaseNodeViewModel.RelationOrder.Size);
        database.SetRelationOrder(DatabaseNodeViewModel.RelationOrder.Name);

        var names = database.Children.OfType<RelationNodeViewModel>().Select(r => Name(r)).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names);
    }

    [Fact]
    public async Task Asking_for_size_order_before_the_sizes_arrive_is_honoured_when_they_do()
    {
        // The rows exist long before their sizes, so the request has to survive the wait.
        var browser = new SizeBrowser();
        var database = new DatabaseNodeViewModel(Conn(), DemoCatalog.Database, isConnected: true, browser);
        var landed = new TaskCompletionSource();
        await database.EnsureChildrenAsync();

        database.SetRelationOrder(DatabaseNodeViewModel.RelationOrder.Size);
        database.SizesLoaded = landed.SetResult;
        await browser.Release(landed.Task);

        Assert.Equal("document", Name(database.Children.OfType<RelationNodeViewModel>().First()));
    }

    [Fact]
    public async Task Sorting_by_size_with_no_sizes_yet_leaves_the_rows_alone()
    {
        // A pending read must not look like a table of zero-byte relations.
        var database = new DatabaseNodeViewModel(Conn(), DemoCatalog.Database, isConnected: true,
            new ThrowingSizeBrowser());
        await database.EnsureChildrenAsync();
        var before = database.Children.OfType<RelationNodeViewModel>().Select(r => Name(r)).ToList();

        database.SetRelationOrder(DatabaseNodeViewModel.RelationOrder.Size);

        Assert.Equal(before, database.Children.OfType<RelationNodeViewModel>().Select(r => Name(r)));
    }

    [Fact]
    public async Task Re_ordering_moves_the_rows_rather_than_replacing_them()
    {
        // These nodes hold expanded children; replacing them would collapse whatever the user had open.
        var browser = new SizeBrowser();
        var database = new DatabaseNodeViewModel(Conn(), DemoCatalog.Database, isConnected: true, browser);
        var landed = new TaskCompletionSource();
        await database.EnsureChildrenAsync();
        database.SizesLoaded = landed.SetResult;
        await browser.Release(landed.Task);
        var payment = Relation(database, "payment");

        database.SetRelationOrder(DatabaseNodeViewModel.RelationOrder.Size);

        Assert.Same(payment, Relation(database, "payment"));
    }

    private static string Name(RelationNodeViewModel relation)
        => relation.Title.Contains('.') ? relation.Title.Split('.')[^1] : relation.Title;

    // ---- helpers --------------------------------------------------------------------------------

    private static RelationSize Size(long total, long? rows)
        => new(1, total, total / 2, total / 2, 0, rows);

    private static RelationNodeViewModel Relation(DatabaseNodeViewModel database, string name)
        => database.Children.OfType<RelationNodeViewModel>()
            .Single(r => r.Title.EndsWith(name, StringComparison.Ordinal));

    /// <summary>A browser serving the demo catalog, whose size read the test releases when it chooses.</summary>
    private class SizeBrowser : ISchemaBrowser
    {
        private readonly TaskCompletionSource _gate = new();

        /// <summary>Let the size read finish, then wait for the rows to have been re-labelled.</summary>
        public async Task Release(Task labelled)
        {
            _gate.SetResult();
            await labelled.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([DemoCatalog.Database, "postgres"]);

        public Task<DatabaseObjects> GetObjectsAsync(ConnectionInfo connection, string database, CancellationToken ct)
            => Task.FromResult(new DatabaseObjects(DemoCatalog.Snapshot(), DemoCatalog.Routines()));

        public virtual async Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(
            ConnectionInfo connection, string database, CancellationToken ct)
        {
            await _gate.Task;
            return DemoCatalog.Sizes();
        }

        public Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult(DemoCatalog.DatabaseSizes());

        public Task<string> GetViewDefinitionAsync(ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => Task.FromResult("select 1");

        public Task<TableDetails> GetTableDetailsAsync(ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => Task.FromResult(DemoCatalog.DetailsOf(tableId));

        public Task<string> GetRoutineDefinitionAsync(ConnectionInfo connection, string database, long routineId, CancellationToken ct)
            => Task.FromResult("create function …");

        public Task InvalidateAsync(Guid connectionId) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingSizeBrowser : SizeBrowser
    {
        public override Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(
            ConnectionInfo connection, string database, CancellationToken ct)
            => throw new InvalidOperationException("permission denied for function pg_total_relation_size");
    }
}
