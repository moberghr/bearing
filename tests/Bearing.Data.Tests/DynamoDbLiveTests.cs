using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Data.DynamoDb;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The DynamoDB provider against a real DynamoDB Local (<c>./build/test-db-dynamodb.sh</c>), the live half of
/// SC7–SC10: the schema tree's data, the value mappings, paging by continuation and the overflow column. They
/// are <see cref="SkippableFactAttribute"/>s that skip without the container (§4.2) — and a skip is not a pass.
/// <para>
/// Every test makes its own uniquely named table(s) and drops them in a <c>finally</c> (§4.7) — here
/// <see cref="LiveTable"/>'s <c>await using</c>, which is one. Fixtures are made and filled through the SDK's own
/// client, not the provider's, so the code under test never also sets the test up.
/// </para>
/// </summary>
public class DynamoDbLiveTests
{
    private static readonly DynamoDbProvider Provider = DynamoDbTestServer.Provider;

    // ---- SC7: schema -------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_table_with_both_keys_a_global_and_a_local_index_reaches_the_snapshot_with_its_keys_flagged()
    {
        await DynamoDbTestServer.RequireAsync();
        await using var table = await LiveTable.CreateAsync(
            "schema",
            keys: [("customer", ScalarAttributeType.S, KeyType.HASH), ("placed", ScalarAttributeType.N, KeyType.RANGE)],
            configure: request =>
            {
                request.AttributeDefinitions.Add(new AttributeDefinition("status", ScalarAttributeType.S));
                request.AttributeDefinitions.Add(new AttributeDefinition("total", ScalarAttributeType.N));
                request.GlobalSecondaryIndexes =
                [
                    new GlobalSecondaryIndex
                    {
                        IndexName = "ByStatus",
                        KeySchema =
                        [
                            new KeySchemaElement("status", KeyType.HASH),
                            new KeySchemaElement("placed", KeyType.RANGE),
                        ],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                ];
                request.LocalSecondaryIndexes =
                [
                    new LocalSecondaryIndex
                    {
                        IndexName = "ByTotal",
                        KeySchema =
                        [
                            new KeySchemaElement("customer", KeyType.HASH),
                            new KeySchemaElement("total", KeyType.RANGE),
                        ],
                        Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                    },
                ];
            });
        await table.PutAsync(
            Item(("customer", S("c1")), ("placed", N("1")), ("status", S("open")), ("total", N("10"))),
            Item(("customer", S("c1")), ("placed", N("2")), ("status", S("paid")), ("total", N("20"))));
        await using var factory = DynamoDbTestServer.Factory();
        var reader = Provider.CreateMetadataReader(factory);

        var snapshot = await reader.LoadSnapshotAsync(DynamoDbTestServer.Region, CancellationToken.None);

        var info = Assert.Single(snapshot.Tables, t => t.Name == table.Name);
        Assert.Equal(("", RelationKind.Table), (info.Schema, info.Kind));
        Assert.Equal(DynamoDbMetadataReader.StableId(table.Name), info.Id);
        Assert.Equal(
            [
                (1, "customer", "string", true, true),
                (2, "placed", "number", true, true),
                (3, "status", "string", false, false),
                (4, "total", "number", false, false),
            ],
            snapshot.ColumnsOf(info.Id).Select(c => (c.Ordinal, c.Name, c.DataType, c.IsPrimaryKey, c.NotNull)));

        var details = await reader.GetTableDetailsAsync(info.Id, CancellationToken.None);

        Assert.Equal(
            [
                ("PRIMARY", true, "1,2"),
                ("ByStatus", false, "3,2"),
                ("ByTotal", false, "1,4"),
            ],
            details.Indexes.Select(i => (i.Name, i.IsPrimary, string.Join(",", i.Ordinals))));
        Assert.All(details.Indexes, i => Assert.True(i.IsValid));
        Assert.Equal("primary key (partition key \"customer\" string, sort key \"placed\" number)", details.Indexes[0].Definition);
        Assert.Equal(
            "global secondary index \"ByStatus\" (partition key \"status\" string, sort key \"placed\" number) projection ALL",
            details.Indexes[1].Definition);
        Assert.Equal(
            "local secondary index \"ByTotal\" (partition key \"customer\" string, sort key \"total\" number) projection KEYS_ONLY",
            details.Indexes[2].Definition);
    }

    [SkippableFact]
    public async Task The_region_database_reports_a_size_entry_for_the_table_and_nothing_else_the_engine_lacks()
    {
        await DynamoDbTestServer.RequireAsync();
        await using var table = await LiveTable.CreateAsync("sizes", keys: [("id", ScalarAttributeType.S, KeyType.HASH)]);
        await table.PutAsync(Item(("id", S("a"))), Item(("id", S("b"))));
        await using var factory = DynamoDbTestServer.Factory();
        var reader = Provider.CreateMetadataReader(factory);

        var databases = await reader.GetDatabasesAsync(CancellationToken.None);
        var sizes = await reader.GetRelationSizesAsync(CancellationToken.None);
        var databaseSizes = await reader.GetDatabaseSizesAsync(CancellationToken.None);

        Assert.Equal([DynamoDbTestServer.Region], databases);
        var size = Assert.Single(sizes, s => s.TableId == DynamoDbMetadataReader.StableId(table.Name));
        Assert.True(size.TotalBytes >= 0);
        Assert.Equal(DynamoDbTestServer.Region, Assert.Single(databaseSizes).Database);

        Assert.Empty(await reader.GetRoutinesAsync(CancellationToken.None));
        Assert.Empty(await reader.GetRolesAsync(CancellationToken.None));
        Assert.Empty(await reader.GetTablespacesAsync(CancellationToken.None));
        Assert.True((await reader.GetDatabaseObjectsAsync(CancellationToken.None)).IsEmpty);
    }

    // ---- SC8: values -------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_keyed_select_returns_key_columns_first_then_alphabetical_with_each_type_mapped()
    {
        await DynamoDbTestServer.RequireAsync();
        await using var table = await LiveTable.CreateAsync(
            "values",
            keys: [("id", ScalarAttributeType.S, KeyType.HASH), ("seq", ScalarAttributeType.N, KeyType.RANGE)]);
        await table.PutAsync(
            Item(
                ("id", S("a")),
                ("seq", N("1")),
                ("zeta", S("text")),
                ("num", N("12.5")),
                ("flag", new AttributeValue { BOOL = true }),
                ("blob", Bin(1, 2, 3)),
                ("map", new AttributeValue { M = new Dictionary<string, AttributeValue> { ["s"] = S("x"), ["n"] = N("1") } }),
                ("list", new AttributeValue { L = [S("a"), N("2"), new AttributeValue { BOOL = true }] }),
                ("ss", new AttributeValue { SS = ["b", "a"] }),
                ("ns", new AttributeValue { NS = ["10", "2"] }),
                ("bs", new AttributeValue { BS = [new MemoryStream([3]), new MemoryStream([1, 2])] })),
            Item(("id", S("a")), ("seq", N("2"))));
        await using var factory = DynamoDbTestServer.Factory();
        var executor = Provider.CreateQueryExecutor(factory);

        var result = await SelectAsync(executor, $"select * from \"{table.Name}\" where \"id\" = 'a'");

        Assert.Equal(
            ["id", "seq", "blob", "bs", "flag", "list", "map", "ns", "num", "ss", "zeta"],
            result.Columns.Select(c => c.Name));
        Assert.Equal(
            ["string", "number", "binary", "string", "bool", "string", "string", "string", "number", "string", "string"],
            result.Columns.Select(c => c.DataTypeName));
        Assert.Equal(2, result.Rows.Count);
        Assert.Null(result.Continuation);

        var row = result.Rows[0];
        Assert.Equal("a", row[0]);
        Assert.Equal(1m, row[1]);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(row[2]));
        Assert.Equal("""["AQI=","Aw=="]""", row[3]);
        Assert.Equal(true, row[4]);
        Assert.Equal("""["a",2,true]""", row[5]);
        Assert.Equal("""{"n":1,"s":"x"}""", row[6]);
        Assert.Equal("[2,10]", row[7]);
        Assert.Equal(12.5m, row[8]);
        Assert.Equal("""["a","b"]""", row[9]);
        Assert.Equal("text", row[10]);

        // The second item has only its keys: every other attribute is missing, and missing is null.
        Assert.Equal("a", result.Rows[1][0]);
        Assert.Equal(2m, result.Rows[1][1]);
        Assert.All(result.Rows[1].Skip(2), cell => Assert.Null(cell));
    }

    // ---- SC9: paging -------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Two_hundred_fifty_items_page_as_100_100_50_and_every_key_arrives_once()
    {
        await DynamoDbTestServer.RequireAsync();
        await using var table = await LiveTable.CreateAsync("paging", keys: [("id", ScalarAttributeType.S, KeyType.HASH)]);
        await table.PutAsync(Enumerable.Range(0, 250).Select(i => Item(("id", S($"k{i:000}")), ("v", N(i.ToString())))).ToArray());
        await using var factory = DynamoDbTestServer.Factory();
        var executor = Provider.CreateQueryExecutor(factory);

        var first = await SelectAsync(executor, $"select * from \"{table.Name}\"", maxRows: 100);

        Assert.Equal(100, first.Rows.Count);
        Assert.True(first.Truncated);
        Assert.NotNull(first.Continuation);

        var second = await executor.ExecuteContinuationAsync(first.Continuation!, 100, CancellationToken.None);

        Assert.True(second.Success, second.Error?.Message);
        Assert.Equal(100, second.Rows.Count);
        Assert.True(second.Truncated);
        Assert.NotNull(second.Continuation);

        var third = await executor.ExecuteContinuationAsync(second.Continuation!, 100, CancellationToken.None);

        Assert.True(third.Success, third.Error?.Message);
        Assert.Equal(50, third.Rows.Count);
        Assert.False(third.Truncated);
        Assert.Null(third.Continuation);

        var keys = first.Rows.Concat(second.Rows).Concat(third.Rows).Select(r => (string)r[0]!).ToList();
        Assert.Equal(250, keys.Count);
        Assert.Equal(250, keys.Distinct().Count());
    }

    // ---- SC10: overflow ----------------------------------------------------------------------------

    [SkippableFact]
    public async Task An_attribute_first_seen_on_page_two_arrives_in_the_overflow_column_as_json()
    {
        await DynamoDbTestServer.RequireAsync();
        await using var table = await LiveTable.CreateAsync(
            "overflow",
            keys: [("id", ScalarAttributeType.S, KeyType.HASH), ("seq", ScalarAttributeType.N, KeyType.RANGE)]);

        // A query on one partition comes back in sort-key order, so "late" is on page two by construction —
        // a scan's order is the service's, and could not promise that.
        await table.PutAsync(Enumerable.Range(1, 150)
            .Select(i => i <= 100
                ? Item(("id", S("p")), ("seq", N(i.ToString())), ("v", S("early")))
                : Item(("id", S("p")), ("seq", N(i.ToString())), ("v", S("late")), ("late", S("x"))))
            .ToArray());
        await using var factory = DynamoDbTestServer.Factory();
        var executor = Provider.CreateQueryExecutor(factory);

        var first = await SelectAsync(executor, $"select * from \"{table.Name}\" where \"id\" = 'p'", maxRows: 100);

        Assert.Equal(100, first.Rows.Count);
        Assert.NotNull(first.Continuation);
        Assert.Equal(["id", "seq", "v", DynamoDbValues.OtherAttributes], first.Columns.Select(c => c.Name));
        Assert.All(first.Rows, row => Assert.Null(row[3]));

        var second = await executor.ExecuteContinuationAsync(first.Continuation!, 100, CancellationToken.None);

        Assert.True(second.Success, second.Error?.Message);
        Assert.Equal(first.Columns, second.Columns);
        Assert.Equal(50, second.Rows.Count);
        Assert.Null(second.Continuation);
        Assert.All(second.Rows, row => Assert.Equal("""{"late":"x"}""", row[3]));
        Assert.Equal(101m, second.Rows[0][1]);
    }

    [SkippableFact]
    public async Task A_result_that_fits_one_page_has_no_overflow_column()
    {
        await DynamoDbTestServer.RequireAsync();
        await using var table = await LiveTable.CreateAsync(
            "onepage",
            keys: [("id", ScalarAttributeType.S, KeyType.HASH), ("seq", ScalarAttributeType.N, KeyType.RANGE)]);
        await table.PutAsync(Enumerable.Range(1, 20)
            .Select(i => Item(("id", S("p")), ("seq", N(i.ToString())), ("v", S("x"))))
            .ToArray());
        await using var factory = DynamoDbTestServer.Factory();
        var executor = Provider.CreateQueryExecutor(factory);

        var result = await SelectAsync(executor, $"select * from \"{table.Name}\" where \"id\" = 'p' and \"seq\" <= 5", maxRows: 100);

        Assert.Equal(5, result.Rows.Count);
        Assert.False(result.Truncated);
        Assert.Null(result.Continuation);
        Assert.Equal(["id", "seq", "v"], result.Columns.Select(c => c.Name));
    }

    // ---- [ASSUMED] comments ------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_statement_with_line_and_block_comments_runs_as_written()
    {
        await DynamoDbTestServer.RequireAsync();
        await using var table = await LiveTable.CreateAsync("comments", keys: [("id", ScalarAttributeType.S, KeyType.HASH)]);
        await table.PutAsync(Item(("id", S("a")), ("v", S("x"))), Item(("id", S("b")), ("v", S("y"))));
        await using var factory = DynamoDbTestServer.Factory();
        var executor = Provider.CreateQueryExecutor(factory);

        var result = await SelectAsync(
            executor,
            $"-- leading comment\nselect * /* block */ from \"{table.Name}\" -- trailing comment");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(["id", "v"], result.Columns.Select(c => c.Name));
    }

    // ---- fixtures ----------------------------------------------------------------------------------

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(string value) => new() { N = value };

    private static AttributeValue Bin(params byte[] bytes) => new() { B = new MemoryStream(bytes) };

    private static Dictionary<string, AttributeValue> Item(params (string Name, AttributeValue Value)[] attributes)
        => attributes.ToDictionary(a => a.Name, a => a.Value);

    /// <summary>The one result of a statement that must succeed.</summary>
    private static async Task<QueryResult> SelectAsync(IQueryExecutor executor, string sql, int maxRows = 10_000)
    {
        var results = await executor.ExecuteAsync(sql, new QueryOptions { MaxRows = maxRows }, CancellationToken.None);
        var result = Assert.Single(results);
        Assert.True(result.Success, result.Error?.Message);

        return result;
    }

    /// <summary>
    /// A table that exists for one test: made on the way in, dropped (and its client disposed) on the way out,
    /// whether the test passed or not. Provisioned on demand, so there is no throughput to size.
    /// </summary>
    private sealed class LiveTable : IAsyncDisposable
    {
        private readonly AmazonDynamoDBClient _client;

        private LiveTable(AmazonDynamoDBClient client, string name)
        {
            _client = client;
            Name = name;
        }

        public string Name { get; }

        public static async Task<LiveTable> CreateAsync(
            string stem,
            (string Name, ScalarAttributeType Type, KeyType Role)[] keys,
            Action<CreateTableRequest>? configure = null)
        {
            var client = DynamoDbTestServer.Client();
            var name = DynamoDbTestServer.UniqueTableName(stem);
            var request = new CreateTableRequest
            {
                TableName = name,
                BillingMode = BillingMode.PAY_PER_REQUEST,
                AttributeDefinitions = keys.Select(k => new AttributeDefinition(k.Name, k.Type)).ToList(),
                KeySchema = keys.Select(k => new KeySchemaElement(k.Name, k.Role)).ToList(),
            };
            configure?.Invoke(request);
            var table = new LiveTable(client, name);

            try
            {
                await client.CreateTableAsync(request);

                return table;
            }
            catch
            {
                await table.DisposeAsync();
                throw;
            }
        }

        public async Task PutAsync(params Dictionary<string, AttributeValue>[] items)
        {
            // BatchWriteItem takes 25 at a time and may hand some back unprocessed.
            foreach (var chunk in items.Chunk(25))
            {
                var pending = new Dictionary<string, List<WriteRequest>>
                {
                    [Name] = chunk.Select(item => new WriteRequest(new PutRequest(item))).ToList(),
                };

                for (var attempt = 0; pending.Count > 0; attempt++)
                {
                    Assert.True(attempt < 10, "DynamoDB Local kept returning unprocessed items.");
                    var response = await _client.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = pending });
                    pending = response.UnprocessedItems is { Count: > 0 } left ? left : [];
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _client.DeleteTableAsync(Name);
            }
            catch (ResourceNotFoundException)
            {
                // Never created, or already gone — either way there is nothing left to drop.
            }
            finally
            {
                _client.Dispose();
            }
        }
    }
}
