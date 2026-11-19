using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Bearing.Data.DynamoDb;

/// <summary>
/// The three DynamoDB calls Bearing makes, and nothing else — a seam over <see cref="AmazonDynamoDBClient"/>
/// so the executor and the metadata reader are tested against a hand-rolled fake (§4.1: no mocking library,
/// and the SDK's own <c>IAmazonDynamoDB</c> is some forty members wide). The SDK's request and response
/// types are used as they are: they are plain bags with public setters, which makes the fake the simplest
/// thing that works, and the type never leaves this project (§2.1).
/// <para>
/// <b>Reads only.</b> <c>ExecuteStatement</c> is the one door to PartiQL, writes included; what may go
/// through it is decided before it is called (the write guard, <c>WriteRefusal</c>, the external policy),
/// never by this seam.
/// </para>
/// </summary>
internal interface IDynamoDbApi : IDisposable
{
    Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken ct);

    Task<ListTablesResponse> ListTablesAsync(ListTablesRequest request, CancellationToken ct);

    Task<DescribeTableResponse> DescribeTableAsync(DescribeTableRequest request, CancellationToken ct);
}

/// <summary>
/// <see cref="IDynamoDbApi"/> over a real <see cref="AmazonDynamoDBClient"/>. Holds no logic: every call is a
/// pass-through, so what the SDK does with credentials (see <see cref="DynamoDbConnectionFactory"/>), retries and
/// signing is exactly what the SDK does, and the errors it raises reach <c>DynamoDbErrors</c> untouched.
/// </summary>
internal sealed class AmazonDynamoDbApi : IDynamoDbApi
{
    private readonly AmazonDynamoDBClient _client;

    public AmazonDynamoDbApi(AmazonDynamoDBClient client) => _client = client;

    public Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken ct)
        => _client.ExecuteStatementAsync(request, ct);

    public Task<ListTablesResponse> ListTablesAsync(ListTablesRequest request, CancellationToken ct)
        => _client.ListTablesAsync(request, ct);

    public Task<DescribeTableResponse> DescribeTableAsync(DescribeTableRequest request, CancellationToken ct)
        => _client.DescribeTableAsync(request, ct);

    public void Dispose() => _client.Dispose();
}
