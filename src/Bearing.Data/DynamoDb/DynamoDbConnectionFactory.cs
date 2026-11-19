using System.Collections.Concurrent;
using System.Net;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Bearing.Core.Data;

namespace Bearing.Data.DynamoDb;

/// <summary>
/// Builds the DynamoDB client for one connection's settings and holds what one session learns about its
/// tables. The sibling of <see cref="MySql.MySqlConnectionFactory"/> and <see cref="Postgres.NpgsqlConnectionFactory"/>,
/// with one difference that matters: there is no password and no pool. Region is
/// <see cref="ConnectionInfo.Host"/>, the AWS profile is <see cref="ConnectionInfo.User"/> (blank = the SDK's
/// default credential chain) and the endpoint is <c>Options["Endpoint"]</c>.
/// <para>
/// <b>Bearing never reads, stores, prompts for or logs an AWS secret</b> (§1.1). Credentials are an opaque
/// <see cref="AWSCredentials"/> the SDK resolves itself — lazily, on the first request, so building a client
/// connects to nothing, probes no metadata service and reads no token cache. No Bearing code calls
/// <c>GetCredentials</c>.
/// </para>
/// <para>
/// <b>The endpoint rule.</b> <c>https://</c> to any host is accepted. <c>http://</c> is accepted only for a
/// loopback host (<c>localhost</c>, <c>127.0.0.0/8</c>, <c>::1</c>) — DynamoDB Local — because a signed
/// request over plain HTTP to anywhere else sends the signature, and every item read, across a network.
/// Anything else, including a string that is not a URL, is refused with a sentence. <c>Options</c> travels in
/// the shared project.json, which is why the rule lives here and not in the dialog (§1.4's posture: never
/// quietly weaken transport security). With a loopback endpoint and no profile the client signs with fixed
/// placeholder keys — DynamoDB Local ignores the signature — so local use needs no AWS setup at all.
/// </para>
/// <para>
/// <b>A setting that cannot be honoured fails the connect, not the constructor.</b> The constructor never
/// throws for one; the sentence is thrown by <see cref="TestConnectionAsync"/> and by every later use of
/// <see cref="Api"/>, so it reaches the user through the same "Could not connect to …" the other engines'
/// failures do, and nothing is built from a setting that was refused.
/// </para>
/// </summary>
public sealed class DynamoDbConnectionFactory : IDbConnectionFactory
{
    /// <summary>The key in <see cref="ConnectionInfo.Options"/> the connection editor's Endpoint field fills.</summary>
    public const string EndpointOption = "Endpoint";

    /// <summary>
    /// What a loopback connection with no profile signs with. Not a secret and not a credential for anything:
    /// DynamoDB Local accepts any value, and a real endpoint would refuse these (which is the point — they
    /// cannot be mistaken for, or used as, an identity). Letters only, and that is measured, not style:
    /// DynamoDB Local 3.1 refuses an access key id holding anything else (a hyphen gave "The Access Key ID or
    /// security token is invalid") — it keys its in-memory database by the id.
    /// </summary>
    internal const string PlaceholderAccessKey = "bearinglocal";
    internal const string PlaceholderSecretKey = "bearinglocal";

    private readonly IDynamoDbApi? _api;
    private readonly Exception? _failure;
    private readonly string? _profile;
    private readonly ConcurrentDictionary<string, TableDescription> _tables = new(StringComparer.Ordinal);

    public DynamoDbConnectionFactory(ConnectionInfo info) : this(info, profilesLocation: null)
    {
    }

    /// <param name="profilesLocation">A credentials file to read profiles from instead of the SDK's own
    /// locations. For tests only: nothing in the app passes it.</param>
    internal DynamoDbConnectionFactory(ConnectionInfo info, string? profilesLocation)
    {
        _profile = string.IsNullOrWhiteSpace(info.User) ? null : info.User.Trim();
        Region = info.Host.Trim();
        StatementTimeoutSeconds = SessionPolicy.TimeoutSeconds(info);
        try
        {
            _api = Build(info, _profile, profilesLocation);
        }
        catch (Exception e) when (e is DynamoDbConfigurationException or DynamoDbSignInException)
        {
            // Held, not thrown: see the class remarks.
            _failure = e;
        }
    }

    /// <summary>A factory over an API someone else made — the fake, in the executor's and reader's tests.</summary>
    internal DynamoDbConnectionFactory(ConnectionInfo info, IDynamoDbApi api)
    {
        _profile = string.IsNullOrWhiteSpace(info.User) ? null : info.User.Trim();
        Region = info.Host.Trim();
        StatementTimeoutSeconds = SessionPolicy.TimeoutSeconds(info);
        _api = api;
    }

    /// <summary>The profile this connection signs in with, trimmed; null for the SDK's default chain.</summary>
    internal string? Profile => _profile;

    /// <summary>The region, trimmed — what the one database node is named after.</summary>
    internal string Region { get; }

    /// <summary>The connection's statement timeout in seconds (<see cref="SessionPolicy.TimeoutSeconds"/>);
    /// <see cref="SessionPolicy.NoTimeout"/> when none. A client-side cancel of the page read, the SQL Server
    /// arrangement (§1.9a): DynamoDB has no session to carry a server-side one.</summary>
    internal int StatementTimeoutSeconds { get; }

    /// <summary>The DynamoDB calls this connection may make. Throws the sentence for a setting that was
    /// refused (an endpoint, a region, a profile the SDK cannot find) — on every use, not just the first.</summary>
    internal IDynamoDbApi Api
        => _failure is not null
            ? throw new InvalidOperationException(_failure.Message, _failure)
            : _api ?? throw new ObjectDisposedException(nameof(DynamoDbConnectionFactory));

    /// <summary>
    /// <c>ListTables(Limit 1)</c> — the cheapest call that needs the credentials, the region and the endpoint
    /// to be right, and no table to exist. A sign-in failure comes out as a <see cref="DynamoDbSignInException"/>
    /// naming the profile; a refused setting as the <see cref="DynamoDbConfigurationException"/> sentence.
    /// </summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        if (_failure is not null)
        {
            throw _failure;
        }

        try
        {
            await Api.ListTablesAsync(new ListTablesRequest { Limit = 1 }, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var surfaced = DynamoDbErrors.ForCaller(e, _profile);
            if (ReferenceEquals(surfaced, e))
            {
                throw;
            }
            throw surfaced;
        }
    }

    /// <summary>
    /// <c>DescribeTable</c>, answered from this session's cache after the first time. A table's key schema
    /// does not change under a session often enough to pay a round trip per statement (the scan check and the
    /// column layout both ask); <see cref="ForgetTable"/> is for the callers that learn it did.
    /// <para>
    /// <b>Only a settled description is kept</b> (<see cref="IsSettled"/>): a table being created or updated, or
    /// one with an index still being built, is read again next time rather than remembered in a state DynamoDB is
    /// about to leave. A table DynamoDB says is gone is forgotten.
    /// </para>
    /// </summary>
    internal async Task<TableDescription> DescribeTableAsync(string tableName, CancellationToken ct)
    {
        if (_tables.TryGetValue(tableName, out var cached))
        {
            return cached;
        }

        DescribeTableResponse response;
        try
        {
            response = await Api.DescribeTableAsync(new DescribeTableRequest { TableName = tableName }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (DynamoDbErrors.IsResourceNotFound(e))
        {
            ForgetTable(tableName);
            throw;
        }

        if (IsSettled(response.Table))
        {
            _tables[tableName] = response.Table;
        }
        return response.Table;
    }

    /// <summary>
    /// True when nothing about the description is mid-change: the table's status, and every global index's,
    /// is <c>ACTIVE</c> (or not reported — DynamoDB Local and older answers leave it out).
    /// </summary>
    internal static bool IsSettled(TableDescription? table)
    {
        if (table is null) return false;

        var status = table.TableStatus?.Value;
        if (!string.IsNullOrEmpty(status) && status != "ACTIVE") return false;

        return (table.GlobalSecondaryIndexes ?? [])
            .Select(index => index.IndexStatus?.Value)
            .All(indexStatus => string.IsNullOrEmpty(indexStatus) || indexStatus == "ACTIVE");
    }

    /// <summary>Drop one table's cached description (it was dropped, or its indexes changed).</summary>
    internal void ForgetTable(string tableName) => _tables.TryRemove(tableName, out _);

    /// <summary>Drop every cached description — a refresh of the schema.</summary>
    internal void ForgetTables() => _tables.Clear();

    public ValueTask DisposeAsync()
    {
        _api?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- Building ----------------------------------------------------------------------------------

    /// <summary>The endpoint the record names, validated: null when none is set; otherwise an absolute
    /// <c>http</c> or <c>https</c> URL, <c>http</c> only for loopback. Throws the refusal sentence.
    /// Pure — no network, no SDK.</summary>
    internal static Uri? ResolveEndpoint(ConnectionInfo info)
    {
        var text = info.Options.TryGetValue(EndpointOption, out var value) ? value?.Trim() : null;
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new DynamoDbConfigurationException(
                $"The endpoint \"{text}\" is not a valid http:// or https:// URL. "
                + "Use something like http://localhost:8000 for DynamoDB Local, or an https:// endpoint.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(uri.Host))
        {
            throw new DynamoDbConfigurationException(
                $"The endpoint \"{text}\" uses http:// to a host that is not this machine. "
                + "http:// is accepted only for localhost, 127.0.0.0/8 and ::1; use https:// for any other host.");
        }

        return uri;
    }

    /// <summary>Loopback means this machine and nothing a DNS record could repoint: the name
    /// <c>localhost</c>, or an address in <c>127.0.0.0/8</c> or <c>::1</c>. A hostname that merely resolves
    /// to one does not count — it is a claim about the resolver.</summary>
    internal static bool IsLoopbackHost(string host)
    {
        var name = host.Trim('[', ']');
        return string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(name, out var address) && IPAddress.IsLoopback(address));
    }

    private static IDynamoDbApi Build(ConnectionInfo info, string? profile, string? profilesLocation)
    {
        var endpoint = ResolveEndpoint(info);
        var region = info.Host.Trim();
        if (region.Length == 0)
        {
            throw new DynamoDbConfigurationException(
                "The AWS region is empty. Enter the region the tables live in, for example eu-west-1.");
        }

        var config = new AmazonDynamoDBConfig();
        if (endpoint is null)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }
        else
        {
            // A ServiceURL replaces the region's endpoint, so the signing region has to be named on its own.
            config.ServiceURL = endpoint.AbsoluteUri;
            config.AuthenticationRegion = region;
        }

        AmazonDynamoDBClient client;
        if (profile is not null)
        {
            var chain = profilesLocation is null
                ? new CredentialProfileStoreChain()
                : new CredentialProfileStoreChain(profilesLocation);
            if (!chain.TryGetAWSCredentials(profile, out var credentials))
            {
                throw new DynamoDbSignInException(profile, profileMissing: true);
            }
            client = new AmazonDynamoDBClient(credentials, config);
        }
        else if (endpoint is not null && IsLoopbackHost(endpoint.Host))
        {
            client = new AmazonDynamoDBClient(new BasicAWSCredentials(PlaceholderAccessKey, PlaceholderSecretKey), config);
        }
        else
        {
            // The SDK's default credential chain, resolved by the SDK on the first request.
            client = new AmazonDynamoDBClient(config);
        }

        return new AmazonDynamoDbApi(client);
    }
}
