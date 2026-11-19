using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Bearing.Core.Data;
using Bearing.Data.DynamoDb;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The DynamoDB executor against a hand-rolled fake API (§4.1): the page rules (row cap, no token, the
/// ten-request budget), continuation, the fixed column layout and its overflow column, the statement timeout,
/// writes, and the operations this engine does not offer (SC11). The live proof is <c>DynamoDbLiveTests</c>.
/// </summary>
public class DynamoDbQueryExecutorTests
{
    // ---- SC11: the request budget ------------------------------------------------------------------

    [Fact]
    public async Task A_filter_matching_nothing_stops_after_exactly_ten_requests_with_a_continuation()
    {
        var api = new FakeDynamoDbApi();
        api.AddTable("Orders", Enumerable.Range(0, 10_000).Select(i => FakeDynamoDbApi.Item(("pk", N(i)))), "pk");
        api.Filter = _ => false;

        var result = Single(await Executor(api).ExecuteAsync(
            "select * from \"Orders\" where note = 'never'", new QueryOptions { MaxRows = 100 }, CancellationToken.None));

        Assert.True(result.Success);
        Assert.Equal(DynamoDbQueryExecutor.RequestBudget, api.Requests.Count);
        Assert.Empty(result.Rows);
        Assert.True(result.Truncated);
        Assert.NotNull(result.Continuation);
        Assert.Equal(DynamoDbQueryExecutor.BudgetNotice(0), result.Message);
        Assert.Equal(["pk", DynamoDbValues.OtherAttributes], result.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task Every_request_asks_for_no_more_items_than_the_page_still_has_room_for()
    {
        var api = new FakeDynamoDbApi();
        api.AddTable("T", Enumerable.Range(0, 1_000).Select(i => FakeDynamoDbApi.Item(("pk", N(i)))), "pk");
        api.Filter = item => int.Parse(item["pk"].N, CultureInfo.InvariantCulture) % 2 == 0;

        var result = Single(await Executor(api).ExecuteAsync(
            "select * from T where x = 1", new QueryOptions { MaxRows = 3 }, CancellationToken.None));

        Assert.Equal(3, result.Rows.Count);
        // 0..2 evaluated (0 and 2 match), then one item at a time until the third match.
        Assert.Equal([3, 1, 1], api.Requests.Select(r => r.Limit));
        Assert.Null(result.Message);
    }

    // ---- SC11: paging by continuation -------------------------------------------------------------

    [Fact]
    public async Task Two_hundred_fifty_items_page_as_100_100_50_and_every_key_arrives_once()
    {
        var api = new FakeDynamoDbApi();
        api.AddTable("T", Enumerable.Range(0, 250).Select(i => FakeDynamoDbApi.Item(("pk", S($"k{i:000}")), ("v", N(i)))), "pk");
        var executor = Executor(api);

        var first = Single(await executor.ExecuteAsync("select * from \"T\"", new QueryOptions { MaxRows = 100 }, CancellationToken.None));
        Assert.Equal(100, first.Rows.Count);
        Assert.True(first.Truncated);
        Assert.NotNull(first.Continuation);

        var second = await executor.ExecuteContinuationAsync(first.Continuation!, 100, CancellationToken.None);
        Assert.Equal(100, second.Rows.Count);
        Assert.True(second.Truncated);
        Assert.NotNull(second.Continuation);

        var third = await executor.ExecuteContinuationAsync(second.Continuation!, 100, CancellationToken.None);
        Assert.Equal(50, third.Rows.Count);
        Assert.False(third.Truncated);
        Assert.Null(third.Continuation);

        var keys = first.Rows.Concat(second.Rows).Concat(third.Rows).Select(r => (string)r[0]!).ToList();
        Assert.Equal(250, keys.Count);
        Assert.Equal(250, keys.Distinct().Count());

        // Each continuation re-sent the statement with DynamoDB's token, so no item was read twice.
        Assert.Equal(250, api.ItemsEvaluated);
        Assert.All(api.Requests.Skip(1), r => Assert.False(string.IsNullOrEmpty(r.NextToken)));
        Assert.All(api.Requests, r => Assert.Equal("select * from \"T\"", r.Statement));
    }

    [Fact]
    public async Task A_continuation_keeps_the_first_page_columns_and_its_statement_index()
    {
        var api = new FakeDynamoDbApi();
        api.AddTable("T", Enumerable.Range(0, 4).Select(i => FakeDynamoDbApi.Item(("pk", N(i)), ("b", S("x")))), "pk");
        var executor = Executor(api);

        var results = await executor.ExecuteAsync(
            "select * from T where pk = 0; select * from T", new QueryOptions { MaxRows = 2 }, CancellationToken.None);
        var next = await executor.ExecuteContinuationAsync(results[1].Continuation!, 2, CancellationToken.None);

        Assert.Equal(results[1].Columns, next.Columns);
        Assert.Equal(1, next.StatementIndex);
    }

    [Fact]
    public async Task A_continuation_that_is_not_one_this_executor_issued_is_an_error_and_sends_nothing()
    {
        var api = new FakeDynamoDbApi();

        var result = await Executor(api).ExecuteContinuationAsync("not-a-token", 100, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(new DynamoDbContinuationException().Message, result.Error!.Message);
        Assert.Empty(api.Requests);
    }

    // ---- columns -----------------------------------------------------------------------------------

    [Fact]
    public async Task Columns_are_the_key_attributes_partition_first_then_the_rest_alphabetically()
    {
        var api = new FakeDynamoDbApi();
        api.AddTable("T", [FakeDynamoDbApi.Item(("zeta", S("z")), ("sk", N(1)), ("alpha", S("a")), ("pk", S("p")))], "pk", "sk");

        var result = Single(await Executor(api).ExecuteAsync("select * from T", new QueryOptions(), CancellationToken.None));

        Assert.Equal(["pk", "sk", "alpha", "zeta"], result.Columns.Select(c => c.Name));
        Assert.Equal(["p", 1m, "a", "z"], result.Rows[0]);
        Assert.Null(result.Continuation);
    }

    [Fact]
    public async Task A_table_that_cannot_be_described_gives_alphabetical_columns_only()
    {
        var api = new FakeDynamoDbApi { DescribeFails = new ResourceNotFoundException("gone") };
        api.AddTable("T", [FakeDynamoDbApi.Item(("pk", S("p")), ("alpha", S("a")))], "pk");

        var result = Single(await Executor(api).ExecuteAsync("select * from T", new QueryOptions(), CancellationToken.None));

        Assert.True(result.Success);
        Assert.Equal(["alpha", "pk"], result.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task A_projection_leads_with_only_the_key_attributes_it_returned()
    {
        var api = new FakeDynamoDbApi();
        api.AddTable("T", [FakeDynamoDbApi.Item(("pk", S("p")), ("sk", N(1)), ("b", S("x")), ("a", S("y")))], "pk", "sk");
        api.Project = ["b", "sk", "a"];

        var result = Single(await Executor(api).ExecuteAsync("select b, sk, a from T", new QueryOptions(), CancellationToken.None));

        Assert.Equal(["sk", "a", "b"], result.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task An_attribute_first_seen_on_page_two_arrives_in_the_other_attributes_column()
    {
        var api = new FakeDynamoDbApi();
        api.AddTable("T",
        [
            FakeDynamoDbApi.Item(("pk", S("a")), ("x", N(1))),
            FakeDynamoDbApi.Item(("pk", S("b")), ("x", N(2))),
            FakeDynamoDbApi.Item(("pk", S("c")), ("x", N(3)), ("late", S("new"))),
        ], "pk");
        var executor = Executor(api);

        var first = Single(await executor.ExecuteAsync("select * from T", new QueryOptions { MaxRows = 2 }, CancellationToken.None));
        var second = await executor.ExecuteContinuationAsync(first.Continuation!, 2, CancellationToken.None);

        Assert.Equal(["pk", "x", DynamoDbValues.OtherAttributes], first.Columns.Select(c => c.Name));
        Assert.Equal(first.Columns, second.Columns);
        Assert.Equal(["c", 3m, "{\"late\":\"new\"}"], second.Rows[0]);
    }

    [Fact]
    public async Task A_result_that_fits_in_one_page_has_no_other_attributes_column()
    {
        var api = new FakeDynamoDbApi();
        api.AddTable("T", [FakeDynamoDbApi.Item(("pk", S("a"))), FakeDynamoDbApi.Item(("pk", S("b")), ("late", S("x")))], "pk");

        var result = Single(await Executor(api).ExecuteAsync("select * from T", new QueryOptions { MaxRows = 100 }, CancellationToken.None));

        Assert.Equal(["pk", "late"], result.Columns.Select(c => c.Name));
        Assert.DoesNotContain(result.Columns, c => c.Name == DynamoDbValues.OtherAttributes);
    }

    /// <summary>
    /// The trap a continuing projection sets: the first page fixes <c>n</c> as a number column, and a later page
    /// brings text in it. The grid must never receive a cell whose type contradicts its column, and nothing may
    /// vanish — so a continuing result carries the overflow column whatever its projection, and the misfit lands
    /// there as JSON.
    /// </summary>
    [Fact]
    public async Task A_misfit_on_a_later_page_of_a_projection_never_reaches_a_column_of_another_type()
    {
        var api = new FakeDynamoDbApi();
        api.AddTable("T",
        [
            FakeDynamoDbApi.Item(("pk", S("a")), ("n", N(1)), ("flag", Bool(true))),
            FakeDynamoDbApi.Item(("pk", S("b")), ("n", N(2)), ("flag", Bool(false))),
            FakeDynamoDbApi.Item(("pk", S("c")), ("n", S("not a number")), ("flag", N(7))),
            FakeDynamoDbApi.Item(("pk", S("d")), ("n", N("1e200")), ("flag", Bool(true))),
        ], "pk");
        api.Project = ["n", "flag"];
        var executor = Executor(api);

        var first = Single(await executor.ExecuteAsync("select n, flag from T", new QueryOptions { MaxRows = 2 }, CancellationToken.None));
        var second = await executor.ExecuteContinuationAsync(first.Continuation!, 2, CancellationToken.None);

        Assert.Equal(["flag", "n", DynamoDbValues.OtherAttributes], first.Columns.Select(c => c.Name));
        Assert.Equal([typeof(bool), typeof(decimal), typeof(string)], first.Columns.Select(c => c.ClrType));
        foreach (var row in first.Rows.Concat(second.Rows))
        {
            for (var i = 0; i < row.Length; i++)
            {
                Assert.True(row[i] is null || second.Columns[i].ClrType.IsInstanceOfType(row[i]),
                    $"cell {row[i]} ({row[i]?.GetType().Name}) in a {second.Columns[i].ClrType.Name} column");
            }
        }

        Assert.Equal([null, null, "{\"flag\":7,\"n\":\"not a number\"}"], second.Rows[0]);
        Assert.Equal([true, null, "{\"n\":1e200}"], second.Rows[1]);
    }

    // ---- writes and other statements ---------------------------------------------------------------

    [Theory]
    [InlineData("insert into T value {'pk': 'a'}", "INSERT")]
    [InlineData("UPDATE T SET x = 1 WHERE pk = 'a'", "UPDATE")]
    [InlineData("delete from T where pk = 'a'", "DELETE")]
    public async Task A_write_reports_its_verb_and_no_row_count(string sql, string verb)
    {
        var api = new FakeDynamoDbApi();
        api.AddTable("T", [], "pk");

        var result = Single(await Executor(api).ExecuteAsync(sql, new QueryOptions(), CancellationToken.None));

        Assert.True(result.Success);
        Assert.Empty(result.Columns);
        Assert.Equal(0, result.RowCount);
        Assert.Equal($"{verb} executed. DynamoDB does not report how many items it changed.", result.Message);
        Assert.DoesNotMatch(@"\d", result.Message!);
        var request = Assert.Single(api.Requests);
        Assert.Equal(sql, request.Statement);
        Assert.Null(request.Limit);
    }

    [Fact]
    public async Task A_batch_sets_each_statement_index_and_stops_at_the_first_failure()
    {
        var api = new FakeDynamoDbApi();
        api.AddTable("A", [FakeDynamoDbApi.Item(("pk", S("a")))], "pk");
        api.AddTable("B", [FakeDynamoDbApi.Item(("pk", S("b")))], "pk");

        var results = await Executor(api).ExecuteAsync(
            "select * from A; -- the next one\nselect * from B; select * from Missing; select * from A",
            new QueryOptions(), CancellationToken.None);

        Assert.Equal([0, 1, 2], results.Select(r => r.StatementIndex));
        Assert.True(results[0].Success);
        Assert.True(results[1].Success);
        Assert.False(results[2].Success);
        Assert.Equal("ResourceNotFoundException", results[2].Error!.SqlState);
        Assert.Equal(3, api.Requests.Count);
    }

    [Fact]
    public async Task Script_variables_are_refused_and_nothing_is_sent()
    {
        var api = new FakeDynamoDbApi();
        var options = new QueryOptions { Parameters = [new QueryParameter("id", "1", QueryParameterKind.Number)] };

        var result = Single(await Executor(api).ExecuteAsync("select * from T", options, CancellationToken.None));

        Assert.Equal(DynamoDbQueryExecutor.VariablesUnavailable, result.Error!.Message);
        Assert.Empty(api.Requests);
    }

    // ---- SC11: the statement timeout ---------------------------------------------------------------

    [Fact]
    public async Task A_one_second_statement_timeout_cancels_a_slow_read_with_an_error_naming_it()
    {
        var api = new FakeDynamoDbApi { Delay = Timeout.InfiniteTimeSpan };
        api.AddTable("T", [FakeDynamoDbApi.Item(("pk", S("a")))], "pk");
        var executor = Executor(api, timeoutSeconds: 1);

        var started = DateTime.UtcNow;
        var result = Single(await executor.ExecuteAsync("select * from T", new QueryOptions(), CancellationToken.None));

        Assert.False(result.Success);
        Assert.Contains("statement timeout (1 s)", result.Error!.Message);
        Assert.Equal(DbErrorKind.Canceled, new DynamoDbProvider().Classify(result.Error));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task The_users_own_cancellation_is_thrown_not_reported_as_a_timeout()
    {
        var api = new FakeDynamoDbApi { Delay = Timeout.InfiniteTimeSpan };
        api.AddTable("T", [FakeDynamoDbApi.Item(("pk", S("a")))], "pk");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Executor(api, timeoutSeconds: 30).ExecuteAsync("select * from T", new QueryOptions(), cts.Token));
    }

    [Fact]
    public async Task A_sign_in_failure_is_classified_authentication_and_names_the_profile()
    {
        var api = new FakeDynamoDbApi
        {
            ExecuteFails = new AmazonDynamoDBException("The security token included in the request is expired")
            {
                ErrorCode = "ExpiredTokenException",
            },
        };

        var result = Single(await Executor(api, profile: "dev-sso").ExecuteAsync("select * from T", new QueryOptions(), CancellationToken.None));

        Assert.Equal(DbErrorKind.Authentication, new DynamoDbProvider().Classify(result.Error!));
        Assert.Contains("aws sso login --profile dev-sso", result.Error!.Message);
    }

    // ---- what this engine does not offer -----------------------------------------------------------

    [Fact]
    public async Task Offset_paging_counting_and_generated_writes_are_not_available_and_send_nothing()
    {
        var api = new FakeDynamoDbApi();
        IQueryExecutor executor = Executor(api);

        var page = await executor.ExecutePageAsync("select * from T", CancellationToken.None);
        var count = await executor.CountAsync("select count(*) from T", CancellationToken.None);
        var writes = await executor.ExecuteWriteAsync([new SqlWriteCommand("update T set x = 1", [])], CancellationToken.None);

        Assert.Equal(DynamoDbQueryExecutor.OffsetPagingUnavailable, page.Error!.Message);
        Assert.Null(count);
        Assert.Equal(DynamoDbQueryExecutor.GeneratedWritesUnavailable, Assert.Single(writes).Error!.Message);
        Assert.Empty(api.Requests);
    }

    // ---- streaming ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_stream_follows_the_token_to_the_end_in_batches_with_the_columns_on_every_batch()
    {
        var api = new FakeDynamoDbApi { PageSize = 7 };
        api.AddTable("T", Enumerable.Range(0, 25).Select(i => FakeDynamoDbApi.Item(("pk", N(i)))), "pk");

        var batches = new List<RowBatch>();
        await foreach (var batch in Executor(api).StreamRowsAsync(
                           "select * from T", new QueryOptions { MaxRows = null, BatchRows = 10 }, CancellationToken.None))
        {
            batches.Add(batch);
        }

        Assert.Equal([10, 10, 5], batches.Select(b => b.Rows.Count));
        // More was coming when the columns were fixed, so the overflow column is there — as on a page.
        Assert.All(batches, b => Assert.Equal(["pk", DynamoDbValues.OtherAttributes], b.Columns.Select(c => c.Name)));
        Assert.False(batches[^1].Truncated);
        Assert.Equal(25, batches.SelectMany(b => b.Rows).Select(r => r[0]).Distinct().Count());
    }

    [Fact]
    public async Task A_stream_stopped_by_its_row_cap_says_so_on_the_tail_batch()
    {
        var api = new FakeDynamoDbApi();
        api.AddTable("T", Enumerable.Range(0, 25).Select(i => FakeDynamoDbApi.Item(("pk", N(i)))), "pk");

        var batches = new List<RowBatch>();
        await foreach (var batch in Executor(api).StreamRowsAsync(
                           "select * from T", new QueryOptions { MaxRows = 12, BatchRows = 10 }, CancellationToken.None))
        {
            batches.Add(batch);
        }

        Assert.Equal(12, batches.Sum(b => b.Rows.Count));
        Assert.True(batches[^1].Truncated);
        Assert.Equal(12, api.ItemsEvaluated);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static DynamoDbQueryExecutor Executor(FakeDynamoDbApi api, int timeoutSeconds = 0, string profile = "")
        => new(new DynamoDbConnectionFactory(FakeDynamoDbApi.Info(timeoutSeconds, profile), api));

    private static QueryResult Single(IReadOnlyList<QueryResult> results) => Assert.Single(results);

    private static AttributeValue S(string s) => new() { S = s };

    private static AttributeValue N(int n) => new() { N = n.ToString(CultureInfo.InvariantCulture) };

    private static AttributeValue N(string n) => new() { N = n };

    private static AttributeValue Bool(bool b) => new() { BOOL = b };
}

/// <summary>
/// A DynamoDB that holds its tables in memory and pages <c>ExecuteStatement</c> the way the service does: each
/// request evaluates at most <c>Limit</c> items (or <see cref="PageSize"/>) from where the token says, returns
/// the ones <see cref="Filter"/> keeps, and hands back a token while items remain. Records every request.
/// Understands only <c>… from T …</c> for the table; everything else about the statement is the test's to set.
/// </summary>
internal sealed class FakeDynamoDbApi : IDynamoDbApi
{
    private readonly Dictionary<string, (List<Dictionary<string, AttributeValue>> Items, string[] Keys)> _tables =
        new(StringComparer.Ordinal);

    public List<ExecuteStatementRequest> Requests { get; } = [];
    public List<ListTablesRequest> Listed { get; } = [];
    public List<string> Described { get; } = [];

    /// <summary>Which items a SELECT keeps; all of them by default.</summary>
    public Func<Dictionary<string, AttributeValue>, bool> Filter { get; set; } = _ => true;

    /// <summary>The attributes a SELECT returns; every attribute when null.</summary>
    public string[]? Project { get; set; }

    /// <summary>Items one request evaluates when it sends no <c>Limit</c> (the service's 1 MB, in items).</summary>
    public int PageSize { get; init; } = 1_000;

    /// <summary>Table names per <c>ListTables</c> page.</summary>
    public int ListPageSize { get; init; } = 100;

    public TimeSpan Delay { get; init; } = TimeSpan.Zero;
    public Exception? ExecuteFails { get; init; }
    public Exception? DescribeFails { get; init; }

    /// <summary>Descriptions for <c>DescribeTable</c> beyond the key schema <see cref="AddTable"/> gives.</summary>
    public Dictionary<string, TableDescription> Descriptions { get; } = new(StringComparer.Ordinal);

    public int ItemsEvaluated { get; private set; }

    public static ConnectionInfo Info(int timeoutSeconds = 0, string profile = "") => new()
    {
        Id = Guid.NewGuid(),
        Name = "ddb",
        ProviderId = DynamoDbProvider.ProviderId,
        Host = "eu-west-1",
        Port = 0,
        User = profile,
        CredentialKind = CredentialKind.AwsProfile,
        StatementTimeoutSeconds = timeoutSeconds,
    };

    public static Dictionary<string, AttributeValue> Item(params (string Name, AttributeValue Value)[] attributes)
        => attributes.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal);

    /// <summary>A table whose key schema is <paramref name="keys"/>: partition key, then an optional sort key.
    /// Declared sort key first, to prove the reader orders by key type rather than position.</summary>
    public void AddTable(string name, IEnumerable<Dictionary<string, AttributeValue>> items, params string[] keys)
    {
        _tables[name] = (items.ToList(), keys);

        var schema = keys
            .Select((key, i) => new KeySchemaElement(key, i == 0 ? KeyType.HASH : KeyType.RANGE))
            .Reverse()
            .ToList();

        Descriptions[name] = new TableDescription
        {
            TableName = name,
            KeySchema = schema,
            AttributeDefinitions = keys.Select(k => new AttributeDefinition(k, ScalarAttributeType.S)).ToList(),
        };
    }

    public async Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken ct)
    {
        Requests.Add(request);

        if (Delay != TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (ExecuteFails is not null) throw ExecuteFails;

        var table = TableOf(request.Statement)
            ?? throw new ResourceNotFoundException("Requested resource not found") { ErrorCode = "ResourceNotFoundException" };

        if (PartiQlBatch.LeadVerb(request.Statement) != "SELECT")
        {
            return new ExecuteStatementResponse { Items = [] };
        }

        var start = string.IsNullOrEmpty(request.NextToken) ? 0 : int.Parse(request.NextToken, CultureInfo.InvariantCulture);
        var evaluate = Math.Min(request.Limit ?? PageSize, table.Items.Count - start);
        var matched = table.Items
            .Skip(start)
            .Take(evaluate)
            .Where(Filter)
            .Select(Projected)
            .ToList();
        var next = start + evaluate;
        ItemsEvaluated += evaluate;

        return new ExecuteStatementResponse
        {
            Items = matched,
            NextToken = next < table.Items.Count ? next.ToString(CultureInfo.InvariantCulture) : null,
        };
    }

    public Task<ListTablesResponse> ListTablesAsync(ListTablesRequest request, CancellationToken ct)
    {
        Listed.Add(request);

        var names = _tables.Keys.Union(Descriptions.Keys).Order(StringComparer.Ordinal).ToList();
        var from = request.ExclusiveStartTableName is { } after ? names.IndexOf(after) + 1 : 0;
        var page = names.Skip(from).Take(ListPageSize).ToList();

        return Task.FromResult(new ListTablesResponse
        {
            TableNames = page,
            LastEvaluatedTableName = from + page.Count < names.Count ? page[^1] : null,
        });
    }

    public Task<DescribeTableResponse> DescribeTableAsync(DescribeTableRequest request, CancellationToken ct)
    {
        Described.Add(request.TableName);

        if (DescribeFails is not null) return Task.FromException<DescribeTableResponse>(DescribeFails);

        return Descriptions.TryGetValue(request.TableName, out var table)
            ? Task.FromResult(new DescribeTableResponse { Table = table })
            : Task.FromException<DescribeTableResponse>(new ResourceNotFoundException("Requested resource not found"));
    }

    public void Dispose()
    {
    }

    private (List<Dictionary<string, AttributeValue>> Items, string[] Keys)? TableOf(string statement)
    {
        var target = PartiQlBatch.Target(statement);

        return target is not null && _tables.TryGetValue(target.Table, out var table) ? table : null;
    }

    private Dictionary<string, AttributeValue> Projected(Dictionary<string, AttributeValue> item)
        => Project is null
            ? item
            : item
                .Where(kv => Project.Contains(kv.Key, StringComparer.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
}
