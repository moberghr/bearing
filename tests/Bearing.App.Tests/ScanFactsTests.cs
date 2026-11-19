using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Sessions;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The one look-up both hosts' full-scan checks go through (<see cref="ScanFacts.ClassifyAsync"/>, ARCH1): what it
/// reads, that it reads one table's size and never a region's (S5), that its budget stops the wait without
/// cancelling the shared schema load, and that a caller-supplied session is used as it is.
/// </summary>
public class ScanFactsTests
{
    private const long OrdersId = 1;

    private static readonly SchemaSnapshot Tables = new(
        "eu-west-1",
        [""],
        [
            new TableInfo(OrdersId, "", "Orders", RelationKind.Table),
            new TableInfo(2, "", "Invoices", RelationKind.Table),
        ],
        [
            new ColumnInfo(OrdersId, 1, "customerId", "S", NotNull: true, IsPrimaryKey: true),
            new ColumnInfo(OrdersId, 2, "orderDate", "S", NotNull: true, IsPrimaryKey: true),
            new ColumnInfo(OrdersId, 3, "status", "S", NotNull: false, IsPrimaryKey: false),
            new ColumnInfo(2, 1, "invoiceId", "S", NotNull: true, IsPrimaryKey: true),
        ],
        [],
        searchPath: [""]);

    private static readonly TableDetails OrdersDetails = new(
        [],
        [
            new IndexInfo(10, "PRIMARY", IsUnique: true, IsPrimary: true, IsValid: true, [1, 2], "primary key"),
            new IndexInfo(11, "byStatus", IsUnique: false, IsPrimary: false, IsValid: true, [3, 2], "gsi"),
        ],
        [],
        []);

    private static ConnectionInfo Conn() => new()
    {
        Id = Guid.NewGuid(),
        Name = "orders-prod",
        ProviderId = "postgres",
        Host = "eu-west-1",
        Port = 0,
        Database = "eu-west-1",
        User = "u",
    };

    private static FakeProvider Provider() => new()
    {
        Snapshot = Tables,
        Details = OrdersDetails,
        Sizes =
        [
            new RelationSize(OrdersId, 0, 0, 0, 0, EstimatedRows: 12_345),
            new RelationSize(2, 0, 0, 0, 0, EstimatedRows: 99),
        ],
    };

    private static System.Collections.Generic.IReadOnlyList<ScanTarget> Targets(string sql)
        => ScanGuard.TargetsFor(PartiQlDialect.Instance, sql)!;

    [Fact]
    public async Task A_scan_names_its_item_count_from_a_one_table_read_never_the_whole_region()
    {
        var provider = Provider();
        await using var sessions = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var info = Conn();

        var verdicts = await ScanFacts.ClassifyAsync(
            sessions, info, Targets("select * from \"Orders\"; select * from \"Invoices\" where invoiceId = 'i'"),
            TimeSpan.FromSeconds(5), withItemCounts: true, connected: null, CancellationToken.None);

        Assert.Equal([ScanVerdictKind.FullScan, ScanVerdictKind.Keyed], verdicts.Select(v => v.Kind));
        Assert.Equal(12_345, verdicts[0].ApproximateItemCount);

        var metadata = (FakeMetadata)(await sessions.GetOrConnectAsync(info, CancellationToken.None)).Metadata;
        Assert.Equal(0, metadata.AllSizeReads);
        // Only the scanned table: the keyed read on Invoices names no count.
        Assert.Equal([OrdersId], metadata.OneSizeReads);
    }

    [Fact]
    public async Task Without_item_counts_no_size_is_read()
    {
        var provider = Provider();
        await using var sessions = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var info = Conn();

        var verdict = Assert.Single(await ScanFacts.ClassifyAsync(
            sessions, info, Targets("select * from \"Orders\""),
            TimeSpan.FromSeconds(5), withItemCounts: false, connected: null, CancellationToken.None));

        Assert.Equal(ScanVerdictKind.FullScan, verdict.Kind);
        Assert.Null(verdict.ApproximateItemCount);
        var metadata = (FakeMetadata)(await sessions.GetOrConnectAsync(info, CancellationToken.None)).Metadata;
        Assert.Empty(metadata.OneSizeReads);
        Assert.Equal(0, metadata.AllSizeReads);
    }

    [Fact]
    public async Task An_index_target_reads_its_table_s_details_and_classifies_on_the_index_key()
    {
        await using var sessions = new ConnectionSessionManager(Provider(), () => null, runSweepTimer: false);

        var verdict = Assert.Single(await ScanFacts.ClassifyAsync(
            sessions, Conn(), Targets("select * from \"Orders\".\"byStatus\" where status = 'open'"),
            TimeSpan.FromSeconds(5), withItemCounts: true, connected: null, CancellationToken.None));

        Assert.Equal(ScanVerdictKind.Keyed, verdict.Kind);
        Assert.Equal("status", verdict.PartitionKey);
    }

    /// <summary>The budget stops the wait, not the load: the schema load the run will join keeps going, so the
    /// run does not start it again (the App's arrangement, now both hosts').</summary>
    [Fact]
    public async Task A_schema_load_past_the_budget_is_undetermined_and_is_not_cancelled()
    {
        var provider = Provider();
        provider.MetadataGate = new TaskCompletionSource();
        await using var sessions = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var info = Conn();

        try
        {
            var verdict = Assert.Single(await ScanFacts.ClassifyAsync(
                sessions, info, Targets("select * from \"Orders\" where customerId = 'a'"),
                TimeSpan.FromMilliseconds(100), withItemCounts: true, connected: null, CancellationToken.None));

            Assert.Equal(ScanVerdictKind.Undetermined, verdict.Kind);
            Assert.Equal(ScanUncertainty.TablesNotLoaded, verdict.Uncertainty);
        }
        finally
        {
            provider.MetadataGate.TrySetResult();
        }

        var session = await sessions.GetOrConnectAsync(info, CancellationToken.None);
        var snapshot = await sessions.EnsureSchemaAsync(session, CancellationToken.None);

        Assert.Same(Tables, snapshot);
        // The load the check started finished and was adopted — not cancelled and read a second time.
        Assert.Equal(1, ((FakeMetadata)session.Metadata).LoadCount);
    }

    /// <summary>The <c>bearing</c> command connects first (so a failed sign-in reads as one) and hands the session
    /// over; the helper uses it rather than connecting again.</summary>
    [Fact]
    public async Task A_session_the_caller_connected_is_used_as_it_is()
    {
        var provider = Provider();
        await using var sessions = new ConnectionSessionManager(provider, () => null, runSweepTimer: false);
        var info = Conn();
        using var lease = await sessions.AcquireAsync(info, CancellationToken.None);

        var verdict = Assert.Single(await ScanFacts.ClassifyAsync(
            sessions, info, Targets("select * from \"Orders\""),
            TimeSpan.FromSeconds(5), withItemCounts: false, lease.Session, CancellationToken.None));

        Assert.Equal(ScanVerdictKind.FullScan, verdict.Kind);
        Assert.Equal(1, provider.FactoriesCreated);
    }

    [Fact]
    public async Task The_caller_s_own_cancellation_is_thrown_not_classified()
    {
        await using var sessions = new ConnectionSessionManager(Provider(), () => null, runSweepTimer: false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScanFacts.ClassifyAsync(
            sessions, Conn(), Targets("select * from \"Orders\""),
            TimeSpan.FromSeconds(5), withItemCounts: true, connected: null, cancelled.Token));
    }
}
