using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.DynamoDb;
using Xunit;

namespace Bearing.Testing;

/// <summary>
/// The DynamoDB Local the DynamoDb* live suites use — <see cref="MySqlTestServer"/>'s sibling. Default is
/// <c>http://localhost:18000</c>, which is where <c>build/test-db-dynamodb.sh</c> puts it; override with
/// <c>BEARING_TEST_DYNAMODB_ENDPOINT</c>. Unreachable means skipped (§4.2) — and a skip is not a pass.
/// <para>
/// There is no credential: a loopback endpoint with a blank profile signs with fixed placeholder keys
/// (<see cref="DynamoDbConnectionFactory"/>), and DynamoDB Local ignores the signature. The same placeholder
/// keys build the <see cref="Client"/> a test uses to make and drop its own fixture tables, so the provider
/// under test is never also the thing that sets the test up.
/// </para>
/// </summary>
public static class DynamoDbTestServer
{
    public const string DefaultEndpoint = "http://localhost:18000";

    /// <summary>Any region will do — DynamoDB Local has one — but it is also what the one database node is named.</summary>
    public const string Region = "us-east-1";

    public static string Endpoint
        => Environment.GetEnvironmentVariable("BEARING_TEST_DYNAMODB_ENDPOINT") is { Length: > 0 } set
            ? set
            : DefaultEndpoint;

    /// <summary>A <see cref="ConnectionInfo"/> for provider <c>dynamodb</c> aimed at the test server, with a
    /// fresh id per call: the region in <c>Host</c>, a blank profile, the endpoint in the options bag.</summary>
    public static ConnectionInfo Info(string name = "dynamodb-test") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = DynamoDbProvider.ProviderId,
        Host = Region,
        User = "",
        CredentialKind = CredentialKind.AwsProfile,
        Options = new Dictionary<string, string> { [DynamoDbConnectionFactory.EndpointOption] = Endpoint },
    };

    public static DynamoDbProvider Provider { get; } = new();

    public static IDbConnectionFactory Factory(ConnectionInfo? info = null)
        => Provider.CreateConnectionFactory(info ?? Info(), null);

    /// <summary>The SDK's own client, for the fixture work a test does around the provider (create a table,
    /// put items, drop it). Not the provider's client, and the caller disposes it.</summary>
    public static AmazonDynamoDBClient Client()
        => new(
            new BasicAWSCredentials(
                DynamoDbConnectionFactory.PlaceholderAccessKey, DynamoDbConnectionFactory.PlaceholderSecretKey),
            new AmazonDynamoDBConfig { ServiceURL = Endpoint, AuthenticationRegion = Region });

    /// <summary>A table name no other test, and no earlier run against a long-lived server, has used.</summary>
    public static string UniqueTableName(string stem) => $"bearing_test_{stem}_{Guid.NewGuid():N}";

    private static readonly object Gate = new();
    private static Task<string?>? _probe;

    /// <summary>
    /// Skip unless the server answers. Probed once per run: a refused connection is retried by the SDK with
    /// backoff, so a per-test probe would add seconds to every skipped test on a box with no container.
    /// </summary>
    public static async Task RequireAsync()
    {
        Task<string?> probe;
        lock (Gate) probe = _probe ??= ProbeAsync();

        var reason = await probe.ConfigureAwait(false);
        Skip.If(reason is not null, $"No DynamoDB Local reachable for integration test at {Endpoint} — "
            + $"{reason?.TrimEnd('.', ' ')}. Start one with ./build/test-db-dynamodb.sh.");
    }

    private static async Task<string?> ProbeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var factory = Factory();

            return await factory.TestConnectionAsync(timeout.Token).ConfigureAwait(false)
                ? null
                : "the server refused a test connection without raising an error.";
        }
        catch (OperationCanceledException)
        {
            return "it did not answer within 10 seconds.";
        }
        catch (Exception ex)
        {
            return SafeErrorText.Of(ex);
        }
    }
}
