using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Bearing.Core.Schema;
using Bearing.Data.DynamoDb;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The schema tree's data from a fake API (§4.1): one database named after the region, an empty schema,
/// declared attributes as columns with the partition key at ordinal 1, indexes with their keys and projection,
/// and the service's approximate sizes. The live proof is <c>DynamoDbLiveTests</c> (SC7).
/// </summary>
public class DynamoDbMetadataReaderTests
{
    [Fact]
    public async Task The_one_database_is_named_after_the_region()
    {
        var reader = Reader(new FakeDynamoDbApi());

        Assert.Equal(["eu-west-1"], await reader.GetDatabasesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_table_s_columns_are_its_declared_attributes_partition_key_first()
    {
        var api = new FakeDynamoDbApi();
        api.Descriptions["Orders"] = Orders();

        var snapshot = await Reader(api).LoadSnapshotAsync("eu-west-1", CancellationToken.None);

        var table = Assert.Single(snapshot.Tables);
        Assert.Equal(("", "Orders", RelationKind.Table), (table.Schema, table.Name, table.Kind));
        Assert.Equal(DynamoDbMetadataReader.StableId("Orders"), table.Id);
        Assert.Equal([""], snapshot.Schemas);
        Assert.Equal(
            [
                (1, "customer", "string", true, true),
                (2, "placed", "number", true, true),
                (3, "status", "string", false, false),
                (4, "total", "number", false, false),
            ],
            snapshot.ColumnsOf(table.Id).Select(c => (c.Ordinal, c.Name, c.DataType, c.IsPrimaryKey, c.NotNull)));
    }

    [Fact]
    public async Task Table_details_list_the_primary_key_each_global_and_each_local_index_with_their_keys()
    {
        var api = new FakeDynamoDbApi();
        api.Descriptions["Orders"] = Orders();
        var reader = Reader(api);

        var details = await reader.GetTableDetailsAsync(DynamoDbMetadataReader.StableId("Orders"), CancellationToken.None);

        Assert.Equal(
            [
                ("PRIMARY", true, true, "1,2"),
                ("ByStatus", false, true, "3,2"),
                ("ByTotal", false, true, "1,4"),
            ],
            details.Indexes.Select(i => (i.Name, i.IsPrimary, i.IsValid, string.Join(",", i.Ordinals))));
        Assert.Equal("primary key (partition key \"customer\" string, sort key \"placed\" number)", details.Indexes[0].Definition);
        Assert.Equal(
            "global secondary index \"ByStatus\" (partition key \"status\" string, sort key \"placed\" number) projection INCLUDE (\"total\")",
            details.Indexes[1].Definition);
        Assert.Equal(
            "local secondary index \"ByTotal\" (partition key \"customer\" string, sort key \"total\" number) projection KEYS_ONLY",
            details.Indexes[2].Definition);
        Assert.Equal(2048, details.Indexes[1].SizeBytes);
        Assert.Empty(details.Constraints);
        Assert.Empty(details.Triggers);
    }

    [Fact]
    public async Task A_global_index_still_being_built_is_listed_as_not_valid()
    {
        var api = new FakeDynamoDbApi();
        var orders = Orders();
        orders.GlobalSecondaryIndexes[0].IndexStatus = IndexStatus.CREATING;
        api.Descriptions["Orders"] = orders;

        var details = await Reader(api).GetTableDetailsAsync(DynamoDbMetadataReader.StableId("Orders"), CancellationToken.None);

        Assert.False(details.Indexes[1].IsValid);
        Assert.EndsWith("— creating", details.Indexes[1].Definition);
    }

    [Fact]
    public async Task A_table_id_resolves_on_a_fresh_reader_and_an_unknown_one_reads_as_empty()
    {
        var api = new FakeDynamoDbApi();
        api.Descriptions["Orders"] = Orders();

        var details = await Reader(api).GetTableDetailsAsync(DynamoDbMetadataReader.StableId("Orders"), CancellationToken.None);
        var missing = await Reader(api).GetTableDetailsAsync(DynamoDbMetadataReader.StableId("Gone"), CancellationToken.None);

        Assert.Equal(3, details.Indexes.Count);
        Assert.Same(TableDetails.Empty, missing);
    }

    [Fact]
    public async Task Every_page_of_the_table_list_is_read()
    {
        var api = new FakeDynamoDbApi { ListPageSize = 2 };
        foreach (var name in new[] { "a", "b", "c", "d", "e" }) api.AddTable(name, [], "pk");

        var snapshot = await Reader(api).LoadSnapshotAsync("eu-west-1", CancellationToken.None);

        Assert.Equal(["a", "b", "c", "d", "e"], snapshot.Tables.Select(t => t.Name));
        Assert.Equal(3, api.Listed.Count);
        Assert.Equal([null, "b", "d"], api.Listed.Select(r => r.ExclusiveStartTableName));
    }

    [Fact]
    public async Task A_table_that_cannot_be_described_keeps_its_row_without_columns()
    {
        var api = new FakeDynamoDbApi { DescribeFails = new AmazonDynamoDBException("not authorized") { ErrorCode = "AccessDeniedException" } };
        api.AddTable("T", [], "pk");

        var snapshot = await Reader(api).LoadSnapshotAsync("eu-west-1", CancellationToken.None);

        var table = Assert.Single(snapshot.Tables);
        Assert.Empty(snapshot.ColumnsOf(table.Id));
    }

    [Fact]
    public async Task A_sign_in_failure_while_listing_fails_the_read_with_the_profile_sentence()
    {
        var api = new FakeDynamoDbApi();
        var reader = new DynamoDbMetadataReader(new DynamoDbConnectionFactory(
            FakeDynamoDbApi.Info(profile: "dev-sso"), new FailingListApi(api)));

        var failure = await Assert.ThrowsAsync<DynamoDbSignInException>(
            () => reader.LoadSnapshotAsync("eu-west-1", CancellationToken.None));

        Assert.Contains("aws sso login --profile dev-sso", failure.Message);
    }

    [Fact]
    public async Task Sizes_are_the_service_s_approximate_bytes_and_item_count_and_a_missing_one_is_skipped()
    {
        var api = new FakeDynamoDbApi();
        api.Descriptions["Orders"] = Orders();
        api.AddTable("Fresh", [], "pk");

        var sizes = await Reader(api).GetRelationSizesAsync(CancellationToken.None);
        var databases = await Reader(api).GetDatabaseSizesAsync(CancellationToken.None);

        var size = Assert.Single(sizes);
        Assert.Equal(
            (DynamoDbMetadataReader.StableId("Orders"), 10_000L + 2048 + 512, 10_000L, 2048L + 512, 42L),
            (size.TableId, size.TotalBytes, size.TableBytes, size.IndexBytes, size.EstimatedRows));
        var database = Assert.Single(databases);
        Assert.Null(database.Bytes);
        Assert.Equal(DynamoDbMetadataReader.SizesIncompleteReason, database.NotVisibleReason);
    }

    [Fact]
    public async Task Routines_roles_tablespaces_and_object_kinds_are_empty()
    {
        var reader = Reader(new FakeDynamoDbApi());

        Assert.Empty(await reader.GetRoutinesAsync(CancellationToken.None));
        Assert.Empty(await reader.GetRolesAsync(CancellationToken.None));
        Assert.Empty(await reader.GetTablespacesAsync(CancellationToken.None));
        Assert.Same(DatabaseObjectKinds.Empty, await reader.GetDatabaseObjectsAsync(CancellationToken.None));
        Assert.Same(RoleGrants.NotVisible, await reader.GetRoleGrantsAsync("anyone", CancellationToken.None));
    }

    // ---- attribute sample (U2) ---------------------------------------------------------------------

    [Fact]
    public async Task A_sample_is_one_bounded_request_listing_the_undeclared_attributes_by_name_with_their_types()
    {
        var api = new FakeDynamoDbApi();
        api.AddTable(
            "Orders",
            [
                FakeDynamoDbApi.Item(
                    ("customer", S("c1")),
                    ("placed", N("1")),
                    ("status", S("open")),
                    ("zeta", S("text")),
                    ("amount", N("12.5")),
                    ("flag", new AttributeValue { BOOL = true }),
                    ("blob", new AttributeValue { B = new MemoryStream([1]) }),
                    ("doc", new AttributeValue { M = new Dictionary<string, AttributeValue> { ["s"] = S("x") } }),
                    ("list", new AttributeValue { L = [S("a")] }),
                    ("ss", new AttributeValue { SS = ["a"] }),
                    ("ns", new AttributeValue { NS = ["1"] }),
                    ("bs", new AttributeValue { BS = [new MemoryStream([1])] }),
                    ("gone", new AttributeValue { NULL = true }),
                    ("v", S("x"))),
                FakeDynamoDbApi.Item(("customer", S("c2")), ("placed", N("2")), ("v", N("2"))),
                FakeDynamoDbApi.Item(("customer", S("c3")), ("placed", N("3")), ("late", S("never read"))),
            ],
            "customer", "placed");
        // The index keys (status, total) are declared too: the tree lists them already.
        api.Descriptions["Orders"] = Orders();

        var sample = await Reader(api).SampleAttributesAsync(OrdersTable, 2, CancellationToken.None);

        var request = Assert.Single(api.Requests);
        Assert.Equal("select * from \"Orders\"", request.Statement);
        Assert.Equal(2, request.Limit);
        Assert.Null(request.NextToken);
        Assert.NotNull(sample);
        Assert.Null(sample.Refused);
        Assert.Equal(2, sample.ItemsRead);
        Assert.Equal(
            [
                ("amount", "number"), ("blob", "binary"), ("bs", "binary set"), ("doc", "map"), ("flag", "boolean"),
                ("gone", "null"), ("list", "list"), ("ns", "number set"), ("ss", "string set"),
                ("v", DynamoDbMetadataReader.MixedType), ("zeta", "string"),
            ],
            sample.Attributes.Select(a => (a.Name, a.TypeName)));
    }

    [Fact]
    public async Task A_sample_is_kept_until_the_next_snapshot_and_a_refused_one_is_not()
    {
        var api = new FakeDynamoDbApi();
        api.AddTable("T", [FakeDynamoDbApi.Item(("pk", S("a")), ("v", S("x")))], "pk");
        // Described, but the read says it is not there (the fake holds no items for it).
        api.Descriptions["Missing"] = new TableDescription { TableName = "Missing", KeySchema = [new KeySchemaElement("pk", KeyType.HASH)] };
        var reader = Reader(api);

        var first = await reader.SampleAttributesAsync(Table("T"), 20, CancellationToken.None);
        var again = await reader.SampleAttributesAsync(Table("T"), 20, CancellationToken.None);

        Assert.Same(first, again);
        Assert.Single(api.Requests);

        await reader.LoadSnapshotAsync("eu-west-1", CancellationToken.None);
        await reader.SampleAttributesAsync(Table("T"), 20, CancellationToken.None);

        Assert.Equal(2, api.Requests.Count);

        // Refused (gone): asked again on the next expand.
        await reader.SampleAttributesAsync(Table("Missing"), 20, CancellationToken.None);
        await reader.SampleAttributesAsync(Table("Missing"), 20, CancellationToken.None);

        Assert.Equal(4, api.Requests.Count);
    }

    [Fact]
    public async Task A_policy_refusing_the_read_is_a_refused_sample_with_the_scrubbed_reason_not_an_empty_list()
    {
        var api = new FakeDynamoDbApi
        {
            ExecuteFails = new AmazonDynamoDBException(
                "User AKIAABCDEFGHIJKLMNOP is not authorized to perform: dynamodb:PartiQLSelect")
            { ErrorCode = "AccessDeniedException" },
        };
        api.AddTable("T", [], "pk");

        var sample = await Reader(api).SampleAttributesAsync(Table("T"), 20, CancellationToken.None);

        Assert.NotNull(sample);
        Assert.Empty(sample.Attributes);
        Assert.Equal("User *** is not authorized to perform: dynamodb:PartiQLSelect", sample.Refused);
    }

    [Fact]
    public async Task A_table_that_is_gone_and_a_failure_that_may_pass_are_refused_samples_with_their_reasons()
    {
        var api = new FakeDynamoDbApi();
        api.Descriptions["Dropped"] = new TableDescription
        {
            TableName = "Dropped",
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
        };
        var throttled = new FakeDynamoDbApi
        {
            ExecuteFails = new ProvisionedThroughputExceededException("Rate of requests exceeds the allowed throughput."),
        };
        throttled.AddTable("T", [], "pk");

        var gone = await Reader(api).SampleAttributesAsync(Table("Dropped"), 20, CancellationToken.None);
        var failed = await Reader(throttled).SampleAttributesAsync(Table("T"), 20, CancellationToken.None);

        Assert.Equal(DynamoDbMetadataReader.SampleTableGone, gone?.Refused);
        Assert.Equal("Rate of requests exceeds the allowed throughput.", failed?.Refused);
        Assert.Equal(0, failed?.ItemsRead);
    }

    [Fact]
    public async Task A_table_whose_description_is_refused_is_still_sampled_with_nothing_left_out()
    {
        var api = new FakeDynamoDbApi
        {
            DescribeFails = new AmazonDynamoDBException("not authorized") { ErrorCode = "AccessDeniedException" },
        };
        api.AddTable("T", [FakeDynamoDbApi.Item(("pk", S("a")), ("v", N("1")))], "pk");

        var sample = await Reader(api).SampleAttributesAsync(Table("T"), 20, CancellationToken.None);

        Assert.Equal([("pk", "string"), ("v", "number")], sample!.Attributes.Select(a => (a.Name, a.TypeName)));
    }

    [Fact]
    public async Task A_sign_in_failure_while_sampling_is_thrown_as_the_profile_sentence()
    {
        var api = new FakeDynamoDbApi
        {
            ExecuteFails = new AmazonDynamoDBException("The security token included in the request is expired")
            { ErrorCode = "ExpiredTokenException" },
        };
        api.AddTable("T", [], "pk");
        var reader = new DynamoDbMetadataReader(new DynamoDbConnectionFactory(FakeDynamoDbApi.Info(profile: "dev-sso"), api));

        var failure = await Assert.ThrowsAsync<DynamoDbSignInException>(
            () => reader.SampleAttributesAsync(Table("T"), 20, CancellationToken.None));

        Assert.Contains("aws sso login --profile dev-sso", failure.Message);
    }

    private static readonly TableInfo OrdersTable = Table("Orders");

    private static TableInfo Table(string name) => new(DynamoDbMetadataReader.StableId(name), "", name, RelationKind.Table);

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(string value) => new() { N = value };

    private static DynamoDbMetadataReader Reader(FakeDynamoDbApi api)
        => new(new DynamoDbConnectionFactory(FakeDynamoDbApi.Info(), api));

    /// <summary>Partition key <c>customer</c>, sort key <c>placed</c>; a GSI on <c>status</c> + <c>placed</c>
    /// projecting <c>total</c>; an LSI on <c>customer</c> + <c>total</c>, keys only.</summary>
    private static TableDescription Orders() => new()
    {
        TableName = "Orders",
        KeySchema = [new KeySchemaElement("placed", KeyType.RANGE), new KeySchemaElement("customer", KeyType.HASH)],
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
                KeySchema = [new KeySchemaElement("status", KeyType.HASH), new KeySchemaElement("placed", KeyType.RANGE)],
                Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["total"] },
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

    /// <summary>The fake, except that listing tables fails the way an expired SSO session does.</summary>
    private sealed class FailingListApi(FakeDynamoDbApi inner) : IDynamoDbApi
    {
        public Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken ct)
            => inner.ExecuteStatementAsync(request, ct);

        public Task<ListTablesResponse> ListTablesAsync(ListTablesRequest request, CancellationToken ct)
            => Task.FromException<ListTablesResponse>(
                new AmazonDynamoDBException("The security token included in the request is expired") { ErrorCode = "ExpiredTokenException" });

        public Task<DescribeTableResponse> DescribeTableAsync(DescribeTableRequest request, CancellationToken ct)
            => inner.DescribeTableAsync(request, ct);

        public void Dispose() => inner.Dispose();
    }
}
