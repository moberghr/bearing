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
