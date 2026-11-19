using Bearing.Core.Data;
using Bearing.Data.DynamoDb;
using Bearing.Data.SqlServer;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// What the DynamoDB provider answers before anything connects: its id and name, the fields the editor shows,
/// the capability flags the dialog and the run path read (§1.9a: a new engine answers every flag), and the
/// two features it refuses outright.
/// </summary>
public class DynamoDbProviderTests
{
    private readonly DynamoDbProvider _provider = new();

    [Fact]
    public void Its_id_is_dynamodb_and_it_is_named_amazon_dynamodb()
    {
        Assert.Equal("dynamodb", _provider.Id);
        Assert.Equal("Amazon DynamoDB", _provider.DisplayName);
    }

    [Fact]
    public void The_fields_are_region_profile_and_endpoint_with_no_port_database_or_password()
    {
        Assert.Equal(
            [("Host", "Region", true), ("User", "AWS profile", false), ("Endpoint", "Endpoint", false)],
            _provider.ConnectionFields.Select(f => (f.Key, f.Label, f.Required)));
        Assert.All(_provider.ConnectionFields, f => Assert.Equal(ConnectionFieldKind.Text, f.Kind));
    }

    [Fact]
    public void It_answers_every_capability_flag_for_an_engine_signed_in_by_aws_profile()
    {
        Assert.False(_provider.SupportsPasswordCredentials);
        Assert.True(_provider.SupportsAwsProfile);
        Assert.False(_provider.SupportsManualCommit);
        Assert.False(_provider.SupportsTlsModes);
        Assert.False(_provider.EnforcesReadOnlyOnServer);
        Assert.False(_provider.SupportsSessionTimeZone);
        Assert.False(_provider.SupportsIntegratedAuth);
        Assert.False(_provider.SupportsEntraToken);
        Assert.False(_provider.SupportsServerActivity);
    }

    [Fact]
    public async Task A_transaction_scope_is_refused_naming_the_flag_to_check()
    {
        await using var factory = _provider.CreateConnectionFactory(FakeDynamoDbApi.Info(), password: null);

        var refused = Assert.Throws<NotSupportedException>(() => _provider.CreateTransactionScope(factory));

        Assert.Contains(nameof(IDbProvider.SupportsManualCommit), refused.Message);
    }

    [Fact]
    public async Task Server_activity_is_the_unsupported_one()
    {
        await using var factory = _provider.CreateConnectionFactory(FakeDynamoDbApi.Info(), password: null);

        Assert.IsType<UnsupportedServerActivity>(_provider.CreateServerActivity(factory));
    }

    [Fact]
    public async Task It_builds_its_own_factory_reader_and_executor_and_ignores_a_password()
    {
        await using var factory = _provider.CreateConnectionFactory(FakeDynamoDbApi.Info(), password: "ignored");

        Assert.IsType<DynamoDbConnectionFactory>(factory);
        Assert.IsType<DynamoDbMetadataReader>(_provider.CreateMetadataReader(factory));
        Assert.IsType<DynamoDbQueryExecutor>(_provider.CreateQueryExecutor(factory));
    }

    [Fact]
    public void A_timeout_recorded_by_the_executor_classifies_canceled()
    {
        var error = new QueryError(DynamoDbErrors.TimeoutSentence(1), DynamoDbErrors.TimeoutCode, null);

        Assert.Equal(DbErrorKind.Canceled, _provider.Classify(error));
    }
}
