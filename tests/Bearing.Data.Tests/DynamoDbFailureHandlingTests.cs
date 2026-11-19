using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Data.DynamoDb;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// How the DynamoDB provider tells failures apart, and what it keeps (review fixes SF1, SF2, SF4, SF5, SF7, S2,
/// S4, S5, W1a): a table that is gone, a policy that refuses and a failure that may pass are different answers,
/// only settled descriptions are cached, a stale one is dropped, SDK text is scrubbed at every rethrow, and a
/// continuation only ever continues a read.
/// </summary>
public class DynamoDbFailureHandlingTests
{
    private const string KeyId = "AKIAIOSFODNN7EXAMPLE";

    // ---- SF1: a describe failure while loading the table list ------------------------------------

    [Fact]
    public async Task A_describe_that_may_pass_fails_the_table_list_naming_the_table_and_a_scrubbed_reason()
    {
        var api = new SwitchableApi(Orders());
        api.AddTable("Other", "pk");
        api.DescribeFails["Orders"] = Throttled($"Rate exceeded for {KeyId}");
        var reader = Reader(api);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => reader.LoadSnapshotAsync("eu-west-1", CancellationToken.None));

        Assert.StartsWith("Keys not loaded for \"Orders\": Rate exceeded for ***", failure.Message);
        Assert.Contains("the next read tries again", failure.Message);
        Assert.DoesNotContain(KeyId, failure.Message);
        Assert.Null(failure.InnerException);
    }

    /// <summary>Nothing about the failed read is kept: once DynamoDB answers, the next read has the keys.</summary>
    [Fact]
    public async Task After_a_failure_that_passed_the_next_table_list_has_the_keys()
    {
        var api = new SwitchableApi(Orders());
        api.DescribeFails["Orders"] = Throttled("Rate exceeded");
        var reader = Reader(api);
        await Assert.ThrowsAnyAsync<Exception>(() => reader.LoadSnapshotAsync("eu-west-1", CancellationToken.None));

        api.DescribeFails.Clear();
        var snapshot = await reader.LoadSnapshotAsync("eu-west-1", CancellationToken.None);

        var table = Assert.Single(snapshot.Tables);
        Assert.Contains(snapshot.ColumnsOf(table.Id), c => c is { Ordinal: 1, Name: "customer", IsPrimaryKey: true });
    }

    [Fact]
    public async Task A_table_dropped_between_the_list_and_the_describe_is_not_listed()
    {
        var api = new SwitchableApi(Orders());
        api.AddTable("Gone", "pk");
        api.DescribeFails["Gone"] = new ResourceNotFoundException("Requested resource not found");

        var snapshot = await Reader(api).LoadSnapshotAsync("eu-west-1", CancellationToken.None);

        Assert.Equal(["Orders"], snapshot.Tables.Select(t => t.Name));
    }

    // ---- SF4 / SF7: sizes ------------------------------------------------------------------------

    [Fact]
    public async Task A_table_with_an_index_whose_size_was_not_reported_has_no_size_and_the_total_is_incomplete()
    {
        var orders = Orders();
        orders.GlobalSecondaryIndexes[0].IndexSizeBytes = null;
        var api = new SwitchableApi(orders);

        var sizes = await Reader(api).GetRelationSizesAsync(CancellationToken.None);
        var database = Assert.Single(await Reader(api).GetDatabaseSizesAsync(CancellationToken.None));

        Assert.Empty(sizes);
        Assert.Null(database.Bytes);
        Assert.Equal(DynamoDbMetadataReader.SizesIncompleteReason, database.NotVisibleReason);
    }

    [Fact]
    public async Task An_item_count_of_zero_is_not_reported()
    {
        var orders = Orders();
        orders.ItemCount = 0;

        var size = Assert.Single(await Reader(new SwitchableApi(orders)).GetRelationSizesAsync(CancellationToken.None));

        Assert.Null(size.EstimatedRows);
        Assert.Equal(10_000L, size.TableBytes);
    }

    // ---- S5: one table's size -------------------------------------------------------------------

    [Fact]
    public async Task One_table_s_size_describes_that_table_only()
    {
        var api = new SwitchableApi(Orders());
        api.AddTable("Other", "pk");

        var size = await Reader(api).GetRelationSizeAsync(DynamoDbMetadataReader.StableId("Orders"), CancellationToken.None);

        Assert.Equal(42L, size!.EstimatedRows);
        Assert.Equal(["Orders"], api.Inner.Described);
    }

    // ---- SF5: what the describe cache keeps ------------------------------------------------------

    [Fact]
    public async Task A_description_with_an_index_still_being_built_is_read_again_next_time()
    {
        var orders = Orders();
        orders.GlobalSecondaryIndexes[0].IndexStatus = IndexStatus.CREATING;
        var api = new SwitchableApi(orders);
        var factory = new DynamoDbConnectionFactory(FakeDynamoDbApi.Info(), api);

        await factory.DescribeTableAsync("Orders", CancellationToken.None);
        await factory.DescribeTableAsync("Orders", CancellationToken.None);
        orders.GlobalSecondaryIndexes[0].IndexStatus = IndexStatus.ACTIVE;
        await factory.DescribeTableAsync("Orders", CancellationToken.None);
        await factory.DescribeTableAsync("Orders", CancellationToken.None);

        Assert.Equal(3, api.Inner.Described.Count);
    }

    [Theory]
    [InlineData("ResourceNotFoundException")]
    [InlineData("ValidationException")]
    public async Task A_statement_that_finds_its_table_gone_or_rejected_drops_the_cached_description(string code)
    {
        var api = new SwitchableApi(Orders());
        api.Inner.AddTable("T", [FakeDynamoDbApi.Item(("pk", S("a")))], "pk");
        var executor = Executor(api);

        await executor.ExecuteAsync("select * from \"T\"", new QueryOptions(), CancellationToken.None);
        await executor.ExecuteAsync("select * from \"T\"", new QueryOptions(), CancellationToken.None);
        Assert.Single(api.Inner.Described, "T");

        api.ExecuteFails = new AmazonDynamoDBException("Requested resource not found") { ErrorCode = code };
        var failed = Assert.Single(await executor.ExecuteAsync("select * from \"T\"", new QueryOptions(), CancellationToken.None));
        Assert.False(failed.Success);

        api.ExecuteFails = null;
        await executor.ExecuteAsync("select * from \"T\"", new QueryOptions(), CancellationToken.None);
        Assert.Equal(2, api.Inner.Described.Count(name => name == "T"));
    }

    [Fact]
    public async Task A_throttled_statement_keeps_the_cached_description()
    {
        var api = new SwitchableApi(Orders());
        api.Inner.AddTable("T", [FakeDynamoDbApi.Item(("pk", S("a")))], "pk");
        var executor = Executor(api);
        await executor.ExecuteAsync("select * from \"T\"", new QueryOptions(), CancellationToken.None);

        api.ExecuteFails = Throttled("Rate exceeded");
        await executor.ExecuteAsync("select * from \"T\"", new QueryOptions(), CancellationToken.None);
        api.ExecuteFails = null;
        await executor.ExecuteAsync("select * from \"T\"", new QueryOptions(), CancellationToken.None);

        Assert.Single(api.Inner.Described, "T");
    }

    // ---- SF2: the key attributes a result leads with ---------------------------------------------

    [Fact]
    public async Task A_describe_that_may_pass_fails_the_select_with_its_own_error_rather_than_reorder_the_columns()
    {
        var api = new SwitchableApi(Orders());
        api.Inner.AddTable("T", [FakeDynamoDbApi.Item(("zeta", S("z")), ("pk", S("p")))], "pk");
        api.DescribeFails["T"] = Throttled("Rate exceeded");

        var result = Assert.Single(await Executor(api).ExecuteAsync("select * from \"T\"", new QueryOptions(), CancellationToken.None));

        Assert.False(result.Success);
        Assert.Equal("ThrottlingException", result.Error!.SqlState);
        Assert.Contains("Rate exceeded", result.Error.Message);
    }

    [Fact]
    public async Task A_refused_describe_still_shows_the_rows_in_alphabetical_columns()
    {
        var api = new SwitchableApi(Orders());
        api.Inner.AddTable("T", [FakeDynamoDbApi.Item(("zeta", S("z")), ("pk", S("p")))], "pk");
        api.DescribeFails["T"] = new AmazonDynamoDBException("not authorized") { ErrorCode = "AccessDeniedException" };

        var result = Assert.Single(await Executor(api).ExecuteAsync("select * from \"T\"", new QueryOptions(), CancellationToken.None));

        Assert.True(result.Success);
        Assert.Equal(["pk", "zeta"], result.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task A_describe_that_may_pass_fails_a_streamed_read_scrubbed()
    {
        var api = new SwitchableApi(Orders());
        api.Inner.AddTable("T", [FakeDynamoDbApi.Item(("pk", S("p")))], "pk");
        api.DescribeFails["T"] = Throttled($"Rate exceeded for {KeyId}");

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in Executor(api).StreamRowsAsync("select * from \"T\"", new QueryOptions(), CancellationToken.None))
            {
            }
        });

        Assert.Contains("Rate exceeded for ***", failure.Message);
        Assert.DoesNotContain(KeyId, failure.Message);
    }

    // ---- S2: scrubbed at every rethrow -----------------------------------------------------------

    [Fact]
    public async Task The_connection_test_surfaces_a_service_message_scrubbed()
    {
        var api = new SwitchableApi(Orders())
        {
            ListFails = new AmazonDynamoDBException($"The key {KeyId} is not valid here") { ErrorCode = "UnknownOperationException" },
        };
        var factory = new DynamoDbConnectionFactory(FakeDynamoDbApi.Info(), api);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Equal("The key *** is not valid here", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task The_schema_read_surfaces_a_service_message_scrubbed()
    {
        var api = new SwitchableApi(Orders())
        {
            ListFails = new AmazonDynamoDBException($"Denied for {KeyId}") { ErrorCode = "UnknownOperationException" },
        };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => Reader(api).LoadSnapshotAsync("eu-west-1", CancellationToken.None));

        Assert.Equal("Denied for ***", failure.Message);
    }

    [Fact]
    public async Task A_streamed_page_surfaces_a_service_message_scrubbed()
    {
        var api = new SwitchableApi(Orders());
        api.Inner.AddTable("T", [FakeDynamoDbApi.Item(("pk", S("p")))], "pk");
        api.ExecuteFails = new AmazonDynamoDBException($"Bad request from {KeyId}") { ErrorCode = "UnknownOperationException" };

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in Executor(api).StreamRowsAsync("select * from \"T\"", new QueryOptions(), CancellationToken.None))
            {
            }
        });

        Assert.Equal("Bad request from ***", failure.Message);
    }

    // ---- S4: a continuation continues a read ----------------------------------------------------

    [Fact]
    public async Task A_continuation_holding_a_delete_is_refused_and_sends_nothing()
    {
        var api = new SwitchableApi(Orders());
        api.Inner.AddTable("T", Enumerable.Range(0, 5).Select(i => FakeDynamoDbApi.Item(("pk", N(i)))), "pk");
        var executor = Executor(api);
        var first = Assert.Single(await executor.ExecuteAsync("select * from \"T\"", new QueryOptions { MaxRows = 2 }, CancellationToken.None));
        var forged = (DynamoDbContinuation.Decode(first.Continuation!) with { Statement = "delete from \"T\" where pk = 1" }).Encode();
        var sent = api.Inner.Requests.Count;

        var result = await executor.ExecuteContinuationAsync(forged, 2, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DynamoDbQueryExecutor.ContinuationNotARead, result.Error!.Message);
        Assert.Equal(sent, api.Inner.Requests.Count);
    }

    // ---- W1a: the budget notice names no host's button --------------------------------------------

    [Fact]
    public void The_budget_notice_says_what_happened_and_names_no_way_to_continue()
    {
        var notice = DynamoDbQueryExecutor.BudgetNotice(3);

        Assert.Equal(
            $"This page stopped after {DynamoDbQueryExecutor.RequestBudget} requests to DynamoDB with 3 rows: "
            + "the read has not reached the end yet.",
            notice);
        Assert.DoesNotContain("Load more", notice);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static DynamoDbMetadataReader Reader(IDynamoDbApi api)
        => new(new DynamoDbConnectionFactory(FakeDynamoDbApi.Info(), api));

    private static DynamoDbQueryExecutor Executor(IDynamoDbApi api)
        => new(new DynamoDbConnectionFactory(FakeDynamoDbApi.Info(), api));

    private static AmazonDynamoDBException Throttled(string message)
        => new(message) { ErrorCode = "ThrottlingException" };

    private static AttributeValue S(string s) => new() { S = s };

    private static AttributeValue N(int n) => new() { N = n.ToString(CultureInfo.InvariantCulture) };

    /// <summary>Partition key <c>customer</c>, sort key <c>placed</c>; one global index, one local, both sized.</summary>
    private static TableDescription Orders() => new()
    {
        TableName = "Orders",
        KeySchema = [new KeySchemaElement("customer", KeyType.HASH), new KeySchemaElement("placed", KeyType.RANGE)],
        AttributeDefinitions =
        [
            new AttributeDefinition("customer", ScalarAttributeType.S),
            new AttributeDefinition("placed", ScalarAttributeType.N),
            new AttributeDefinition("status", ScalarAttributeType.S),
            new AttributeDefinition("total", ScalarAttributeType.N),
        ],
        GlobalSecondaryIndexes =
        [
            new GlobalSecondaryIndexDescription
            {
                IndexName = "ByStatus",
                KeySchema = [new KeySchemaElement("status", KeyType.HASH)],
                Projection = new Projection { ProjectionType = ProjectionType.ALL },
                IndexStatus = IndexStatus.ACTIVE,
                IndexSizeBytes = 2048,
            },
        ],
        LocalSecondaryIndexes =
        [
            new LocalSecondaryIndexDescription
            {
                IndexName = "ByTotal",
                KeySchema = [new KeySchemaElement("customer", KeyType.HASH), new KeySchemaElement("total", KeyType.RANGE)],
                Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                IndexSizeBytes = 512,
            },
        ],
        TableSizeBytes = 10_000,
        ItemCount = 42,
    };

    /// <summary>
    /// The in-memory fake (<see cref="FakeDynamoDbApi"/>), with failures a test can switch on and off between
    /// calls — per table for <c>DescribeTable</c>, and for every <c>ExecuteStatement</c> or <c>ListTables</c>.
    /// </summary>
    private sealed class SwitchableApi : IDynamoDbApi
    {
        public SwitchableApi(TableDescription orders) => Inner.Descriptions["Orders"] = orders;

        public FakeDynamoDbApi Inner { get; } = new();

        public Dictionary<string, Exception> DescribeFails { get; } = new(StringComparer.Ordinal);

        public Exception? ExecuteFails { get; set; }

        public Exception? ListFails { get; init; }

        public void AddTable(string name, params string[] keys) => Inner.AddTable(name, [], keys);

        public Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken ct)
            => ExecuteFails is { } failure
                ? Task.FromException<ExecuteStatementResponse>(failure)
                : Inner.ExecuteStatementAsync(request, ct);

        public Task<ListTablesResponse> ListTablesAsync(ListTablesRequest request, CancellationToken ct)
            => ListFails is { } failure
                ? Task.FromException<ListTablesResponse>(failure)
                : Inner.ListTablesAsync(request, ct);

        public Task<DescribeTableResponse> DescribeTableAsync(DescribeTableRequest request, CancellationToken ct)
        {
            if (!DescribeFails.TryGetValue(request.TableName, out var failure)) return Inner.DescribeTableAsync(request, ct);

            Inner.Described.Add(request.TableName);
            return Task.FromException<DescribeTableResponse>(failure);
        }

        public void Dispose() => Inner.Dispose();
    }
}
