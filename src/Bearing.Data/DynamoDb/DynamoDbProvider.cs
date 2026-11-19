using Bearing.Core.Data;
using Bearing.Data.SqlServer;

namespace Bearing.Data.DynamoDb;

/// <summary>
/// Amazon DynamoDB, read through PartiQL (<c>ExecuteStatement</c>) over the AWS SDK. Not a relational engine,
/// so several of the interface's features are answered "no" here rather than approximated (§1.1: never offer a
/// setting or make a claim nobody arranged) — manual commit, TLS modes, session time zone, server activity,
/// passwords. The SQL text belongs to <c>Bearing.Sql</c>'s <c>PartiQlDialect</c>, paired with this provider by
/// <see cref="Id"/>.
/// <para>
/// Sign-in is an AWS profile and nothing else (<see cref="CredentialKind.AwsProfile"/>): the AWS SDK resolves it
/// in this process, and no Bearing code reads, stores, prompts for or logs an AWS secret (§1.1, §1.9c). Region is
/// <see cref="ConnectionInfo.Host"/>, the profile <see cref="ConnectionInfo.User"/>, the endpoint
/// <c>Options["Endpoint"]</c> — see <see cref="DynamoDbConnectionFactory"/>.
/// </para>
/// </summary>
public sealed class DynamoDbProvider : IDbProvider
{
    public const string ProviderId = "dynamodb";

    public string Id => ProviderId;
    public string DisplayName => "Amazon DynamoDB";

    /// <summary>The hint under the Endpoint field (user decision 2026-11-19). Blank already means the SDK's own
    /// <c>https://dynamodb.&lt;region&gt;.amazonaws.com</c>.</summary>
    public const string EndpointHint = "Leave blank for AWS; set only for DynamoDB Local or a VPC/FIPS endpoint.";

    /// <summary>The editor's sources, read lazily: the profiles come from the user's AWS config file.</summary>
    public DynamoDbProvider() : this(AwsConfigProfiles.ReadDefault) { }

    /// <summary>
    /// A provider whose AWS-profile suggestions come from <paramref name="profiles"/> — how a test keeps the
    /// editor off the real <c>~/.aws</c>. It is called each time the editor shows this engine's fields and must
    /// not throw (<see cref="AwsConfigProfiles.Read"/> doesn't).
    /// </summary>
    public DynamoDbProvider(Func<IReadOnlyList<AwsConfigProfile>> profiles)
    {
        ConnectionFields = new[]
        {
            new ConnectionField("Host", "Region", ConnectionFieldKind.Text, Required: true,
                Suggestions: RegionSuggestions),
            new ConnectionField("User", "AWS profile", ConnectionFieldKind.Text, Required: false,
                Suggestions: () => ProfileSuggestions(profiles())),
            new ConnectionField(DynamoDbConnectionFactory.EndpointOption, "Endpoint", ConnectionFieldKind.Text,
                Required: false, Hint: EndpointHint),
        };
    }

    /// <summary>Region (required), AWS profile (blank = the SDK's default credential chain) and an optional
    /// endpoint for DynamoDB Local or a VPC endpoint. No port, database or password: none of them exists here.
    /// Region and profile are free text with suggestions: the SDK's named regions (the code is what is saved,
    /// the name only shown) and the profiles in the AWS config file, each carrying its region for an empty
    /// Region field.</summary>
    public IReadOnlyList<ConnectionField> ConnectionFields { get; }

    private static IReadOnlyList<ConnectionSuggestion> RegionSuggestions()
        => RegionList.Value;

    private static readonly Lazy<IReadOnlyList<ConnectionSuggestion>> RegionList = new(()
        => AwsRegions.All.Select(r => new ConnectionSuggestion(r.Code, r.Name)).ToList());

    /// <summary>One suggestion per profile; a profile that sets a region offers it for the Region field
    /// (<see cref="ConnectionSuggestion.Fills"/>), which the editor writes only into an empty one.</summary>
    internal static IReadOnlyList<ConnectionSuggestion> ProfileSuggestions(IReadOnlyList<AwsConfigProfile> profiles)
        => profiles
            .Select(p => p.Region is { Length: > 0 } region
                ? new ConnectionSuggestion(p.Name, region, new Dictionary<string, string> { ["Host"] = region })
                : new ConnectionSuggestion(p.Name))
            .ToList();

    /// <summary>
    /// False: DynamoDB has no session to make read-only. Bearing refuses writes before they are sent
    /// (<c>WriteRefusal</c>); what stops one at the server is an IAM policy without write actions.
    /// </summary>
    public bool EnforcesReadOnlyOnServer => false;

    /// <summary>False: DynamoDB has no session time zone; its values carry none.</summary>
    public bool SupportsSessionTimeZone => false;

    public bool SupportsIntegratedAuth => false;

    public bool SupportsEntraToken => false;

    /// <inheritdoc />
    public bool SupportsPasswordCredentials => false;

    /// <inheritdoc />
    public bool SupportsAwsProfile => true;

    /// <summary>False: <c>ExecuteStatement</c> runs one statement and commits it; there is no transaction to hold
    /// open across calls (<c>ExecuteTransaction</c> is one call, and out of scope).</summary>
    public bool SupportsManualCommit => false;

    /// <summary>False: the SDK speaks HTTPS to AWS and there is no mode to choose. The one plain-HTTP case, a
    /// loopback endpoint for DynamoDB Local, is decided by <see cref="DynamoDbConnectionFactory"/>'s rule.</summary>
    public bool SupportsTlsModes => false;

    /// <summary>False: an SSH forward needs a host and port, and this engine has a region and an HTTPS endpoint.
    /// Rewriting the region to a tunnel's loopback end would build a client for a region named "127.0.0.1".</summary>
    public bool SupportsSshTunnel => false;

    /// <summary>False: DynamoDB has no view of other clients' requests to show or cancel.</summary>
    public bool SupportsServerActivity => false;

    public DbErrorKind Classify(QueryError error) => DynamoDbErrors.ClassifyState(error.SqlState);

    public DbErrorKind ClassifyException(Exception exception) => DynamoDbErrors.Classify(exception);

    /// <summary>The password is ignored: an AWS profile has none for Bearing to hold.</summary>
    public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password)
        => new DynamoDbConnectionFactory(info);

    public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory)
        => new DynamoDbMetadataReader((DynamoDbConnectionFactory)factory);

    public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory)
        => new DynamoDbQueryExecutor((DynamoDbConnectionFactory)factory);

    public ITransactionScope CreateTransactionScope(IDbConnectionFactory factory)
        => throw new NotSupportedException(
            $"Amazon DynamoDB cannot hold a transaction open across statements; check {nameof(IDbProvider)}.{nameof(SupportsManualCommit)} first.");

    public IServerActivity CreateServerActivity(IDbConnectionFactory factory) => UnsupportedServerActivity.Instance;
}
