using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Connections;
using Bearing.App.Results;
using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.DynamoDb;
using Bearing.Data.MySql;
using Bearing.Data.Postgres;
using Bearing.Data.SqlServer;
using Bearing.Results;
using Bearing.Sessions;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The App-side per-engine table: the dialect that shapes a connection's SQL, the literal style, the Entra
/// audience and the endpoint hint. The pairing it asserts is the one nothing else can — a provider and its
/// dialect live in projects that deliberately do not reference each other (§2.2), so a shared id is the only
/// link and a typo in one of them would otherwise surface as a wrong page suffix at runtime.
/// </summary>
public class ProviderTraitsTests
{
    [Fact]
    public void Every_registered_provider_has_its_own_arm()
    {
        // The fallback is Postgres, so a provider nobody added an arm for would silently generate Postgres
        // SQL. This is the test that turns that into a build-time-visible failure instead.
        foreach (var provider in new ProviderRegistry().All)
            Assert.Equal(provider.Id, ProviderTraits.For(provider.Id).ProviderId);
    }

    [Fact]
    public void The_registry_and_the_traits_pair_all_four_engines_by_id()
    {
        // Both sides, named: the registry's four ids, and for each the arm whose dialect carries the same id.
        // A provider registered without an arm would otherwise pass the loop above only by also being missing.
        Assert.Equal(
            new[] { PostgresProvider.ProviderId, SqlServerProvider.ProviderId, MySqlProvider.ProviderId, DynamoDbProvider.ProviderId },
            new ProviderRegistry().All.Select(p => p.Id));

        var arms = new Dictionary<string, ProviderTraits>
        {
            [PostgresProvider.ProviderId] = ProviderTraits.Postgres,
            [SqlServerProvider.ProviderId] = ProviderTraits.SqlServer,
            [MySqlProvider.ProviderId] = ProviderTraits.MySql,
            [DynamoDbProvider.ProviderId] = ProviderTraits.DynamoDb,
        };
        foreach (var (id, arm) in arms)
        {
            Assert.Same(arm, ProviderTraits.For(id));
            Assert.Equal(id, arm.Dialect.Id);
        }
    }

    [Fact]
    public void A_traits_dialect_shares_its_providers_id()
    {
        Assert.Equal(PostgresProvider.ProviderId, ProviderTraits.Postgres.Dialect.Id);
        Assert.Equal(SqlServerProvider.ProviderId, ProviderTraits.SqlServer.Dialect.Id);
        Assert.Equal(MySqlProvider.ProviderId, ProviderTraits.MySql.Dialect.Id);
        Assert.Equal(DynamoDbProvider.ProviderId, ProviderTraits.DynamoDb.Dialect.Id);
    }

    [Fact]
    public void A_dynamodb_connection_writes_partiql_and_withholds_editing_and_ddl()
    {
        var traits = ProviderTraits.For("DynamoDB");

        Assert.Same(ProviderTraits.DynamoDb, traits);
        Assert.Same(PartiQlDialect.Instance, traits.Dialect);
        // PartiQL's strings and booleans are the Postgres forms.
        Assert.Equal(SqlLiteralStyle.Postgres, traits.Literals);
        Assert.Equal(
            "Read-only — editing isn't available on Amazon DynamoDB connections yet.",
            traits.GridEditUnavailable);
        Assert.False(traits.OffersTableDdl);
        // Read on every engine (the connection editor shows it), so it is real copy, not a placeholder.
        Assert.Contains("region", traits.EndpointHint!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("profile", traits.EndpointHint!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("loopback", traits.EndpointHint!);
    }

    [Fact]
    public void The_relational_engines_keep_editing_and_ddl()
    {
        foreach (var traits in new[] { ProviderTraits.Postgres, ProviderTraits.SqlServer, ProviderTraits.MySql })
        {
            Assert.Null(traits.GridEditUnavailable);
            Assert.True(traits.OffersTableDdl);
        }
    }

    [Fact]
    public void A_mysql_connection_writes_mysql_text()
    {
        var traits = ProviderTraits.For("MySQL");

        Assert.Same(MySqlDialect.Instance, traits.Dialect);
        Assert.Equal(SqlLiteralStyle.MySql, traits.Literals);
        // Azure Database for MySQL is in the same service family as the Postgres one, so the same audience.
        Assert.Equal(ProviderTraits.Postgres.EntraResource, traits.EntraResource);
        Assert.True(traits.EntraTakesUser);
    }

    [Fact]
    public void Resolution_is_case_insensitive_like_the_registry()
    {
        Assert.Same(ProviderTraits.SqlServer, ProviderTraits.For("SqlServer"));
        Assert.Same(ProviderTraits.SqlServer, ProviderTraits.For("SQLSERVER"));
    }

    [Fact]
    public void An_unknown_or_missing_provider_falls_back_to_postgres()
    {
        // Deliberate: these decide text on paths that must not fail (paging a grid, rendering a preview),
        // and Postgres is what all of them did when there was one engine.
        Assert.Same(ProviderTraits.Postgres, ProviderTraits.For("duckdb"));
        Assert.Same(ProviderTraits.Postgres, ProviderTraits.For((string?)null));
        Assert.Same(ProviderTraits.Postgres, ProviderTraits.For((ConnectionInfo?)null));
    }

    [Fact]
    public void A_connections_traits_come_from_its_provider_id()
    {
        var info = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "c",
            ProviderId = SqlServerProvider.ProviderId,
        };

        var traits = ProviderTraits.For(info);

        Assert.Same(SqlServerDialect.Instance, traits.Dialect);
        Assert.Equal(SqlLiteralStyle.TSql, traits.Literals);
    }

    [Fact]
    public void The_entra_audience_is_per_engine_and_the_postgres_one_is_unchanged()
    {
        // A token minted for Azure Database for PostgreSQL is rejected by Azure SQL and vice versa, so this
        // is not a cosmetic string. The Postgres value must not move: an existing Entra connection has to
        // keep minting exactly the token it minted before a second engine existed.
        Assert.Equal("https://ossrdbms-aad.database.windows.net", ProviderTraits.Postgres.EntraResource);
        Assert.Equal(EntraTokenProvider.DefaultResource, ProviderTraits.Postgres.EntraResource);
        Assert.Equal("https://database.windows.net/", ProviderTraits.SqlServer.EntraResource);
    }
}

/// <summary>The two editor facts the traits carry: whether the scan confirmation is offered, and what bounds a
/// write where only Bearing refuses.</summary>
public class ProviderTraitsEditorFactsTests
{
    [Theory]
    [InlineData("postgres", false)]
    [InlineData("sqlserver", false)]
    [InlineData("mysql", false)]
    [InlineData("dynamodb", true)]
    public void Only_dynamodb_asks_the_full_scan_question(string providerId, bool asks)
        => Assert.Equal(asks, ProviderTraits.For(providerId).AsksFullScanQuestion);

    /// <summary>Declared, not inferred from the scan question: the <c>bearing</c> command picks how an export
    /// reads to the end from this flag alone.</summary>
    [Theory]
    [InlineData("postgres", false)]
    [InlineData("sqlserver", false)]
    [InlineData("mysql", false)]
    [InlineData("dynamodb", true)]
    [InlineData("no-such-engine", false)]
    public void Only_dynamodb_reads_continue_by_token(string providerId, bool continues)
        => Assert.Equal(continues, ProviderTraits.For(providerId).ReadsContinueByToken);

    /// <summary>Only an engine whose tables declare no columns beyond their keys samples on expand; every other is
    /// never asked (the review's "Couldn't sample attributes" under a Postgres table).</summary>
    [Theory]
    [InlineData("postgres", false)]
    [InlineData("sqlserver", false)]
    [InlineData("mysql", false)]
    [InlineData("dynamodb", true)]
    [InlineData("no-such-engine", false)]
    public void Only_dynamodb_samples_attributes(string providerId, bool samples)
        => Assert.Equal(samples, ProviderTraits.For(providerId).SamplesAttributes);

    /// <summary>The options-bag keys that name the service reached are the provider's to declare, so shared code
    /// (<c>SameNetwork</c>) names none of them.</summary>
    [Fact]
    public void Only_dynamodb_has_an_option_that_names_the_service()
    {
        Assert.Equal([Bearing.Data.DynamoDb.DynamoDbConnectionFactory.EndpointOption], ProviderTraits.DynamoDb.NetworkOptions);
        Assert.Empty(ProviderTraits.Postgres.NetworkOptions);
        Assert.Empty(ProviderTraits.SqlServer.NetworkOptions);
        Assert.Empty(ProviderTraits.MySql.NetworkOptions);
    }

    [Fact]
    public void An_unknown_engine_asks_no_scan_question()
        => Assert.False(ProviderTraits.For("no-such-engine").AsksFullScanQuestion);

    [Fact]
    public void Dynamodb_names_iam_as_what_stops_a_write_and_no_other_engine_carries_the_sentence()
    {
        Assert.Contains("IAM policy", ProviderTraits.DynamoDb.WriteBoundary);
        Assert.Null(ProviderTraits.Postgres.WriteBoundary);
        Assert.Null(ProviderTraits.SqlServer.WriteBoundary);
        Assert.Null(ProviderTraits.MySql.WriteBoundary);
    }
}

/// <summary>Which credential kinds a given engine can offer.</summary>
public class CredentialKindOptionsTests
{
    [Fact]
    public void Integrated_is_offered_only_where_the_driver_can_negotiate_it()
    {
        Assert.DoesNotContain(CredentialKind.Integrated,
            CredentialKindOptions.For(new PostgresProvider()).Select(o => o.Kind));
        Assert.Contains(CredentialKind.Integrated,
            CredentialKindOptions.For(new SqlServerProvider()).Select(o => o.Kind));
    }

    [Fact]
    public void The_two_universal_kinds_come_first_and_in_a_stable_order()
    {
        // The dialog stores the selected index nowhere, but a new connection defaults to the first entry, so
        // the order is behaviour: stored password has to stay the head of the list. Only these two are
        // universal — Entra and integrated are each gated on the driver being able to honour them.
        Assert.Equal(
            new[] { CredentialKind.StoredPassword, CredentialKind.Prompt, CredentialKind.EntraToken },
            CredentialKindOptions.For(new PostgresProvider()).Select(o => o.Kind));
        Assert.Equal(
            new[] { CredentialKind.StoredPassword, CredentialKind.Prompt },
            CredentialKindOptions.For(new SqlServerProvider()).Select(o => o.Kind).Take(2));
    }

    [Fact]
    public void Entra_is_offered_wherever_the_driver_has_somewhere_to_put_the_token()
    {
        // Npgsql takes it as the password; SqlClient takes it on SqlConnection.AccessToken, which
        // SqlServerConnectionFactory now sets. Both offer the entry, by two different mechanisms — the gate
        // is "can the factory honour this", not "does the engine's cloud speak Entra", which is why it stays
        // a provider flag rather than becoming a constant here.
        Assert.Contains(CredentialKind.EntraToken,
            CredentialKindOptions.For(new PostgresProvider()).Select(o => o.Kind));
        Assert.Contains(CredentialKind.EntraToken,
            CredentialKindOptions.For(new SqlServerProvider()).Select(o => o.Kind));
    }

    [Fact]
    public void Only_the_stored_password_kind_keeps_a_password()
    {
        // What drives the password box and the no-keychain warning. Integrated resolves no secret at all, so
        // warning it about an unreachable keychain would be warning about something it never touches.
        Assert.True(CredentialKindOptions.KeepsAStoredPassword(CredentialKind.StoredPassword));
        Assert.False(CredentialKindOptions.KeepsAStoredPassword(CredentialKind.Integrated));
        Assert.False(CredentialKindOptions.KeepsAStoredPassword(CredentialKind.Prompt));
        Assert.False(CredentialKindOptions.KeepsAStoredPassword(CredentialKind.EntraToken));
    }

    [Fact]
    public void Amazon_dynamodb_offers_the_aws_profile_kind_and_nothing_else()
    {
        // The factory signs in through the SDK and has nowhere to put a password, a token or an OS identity, so
        // the dropdown must not offer a kind it would silently ignore (§1.9a).
        var kinds = CredentialKindOptions.For(new DynamoDbProvider());

        var only = Assert.Single(kinds);
        Assert.Equal(CredentialKind.AwsProfile, only.Kind);
        Assert.Equal("AWS profile", only.Label);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public void The_aws_profile_kind_is_offered_by_no_other_engine(string providerId)
    {
        var provider = new ProviderRegistry().All.Single(p => p.Id == providerId);

        Assert.DoesNotContain(CredentialKind.AwsProfile, CredentialKindOptions.For(provider).Select(o => o.Kind));
        // And the two password kinds stay where they were, first and in order.
        Assert.Equal(
            new[] { CredentialKind.StoredPassword, CredentialKind.Prompt },
            CredentialKindOptions.For(provider).Select(o => o.Kind).Take(2));
    }

    [Fact]
    public void The_password_kinds_follow_the_password_flag_and_the_other_kinds_follow_theirs()
    {
        // Every combination of the four flags, against a provider that only answers them: the list is a pure
        // function of the flags, so a kind cannot appear for an engine that did not claim it.
        for (var bits = 0; bits < 16; bits++)
        {
            var provider = new FlagsProvider(
                password: (bits & 1) != 0, entra: (bits & 2) != 0, integrated: (bits & 4) != 0, aws: (bits & 8) != 0);

            var expected = new List<CredentialKind>();
            if (provider.SupportsPasswordCredentials)
            {
                expected.Add(CredentialKind.StoredPassword);
                expected.Add(CredentialKind.Prompt);
            }
            if (provider.SupportsEntraToken) expected.Add(CredentialKind.EntraToken);
            if (provider.SupportsIntegratedAuth) expected.Add(CredentialKind.Integrated);
            if (provider.SupportsAwsProfile) expected.Add(CredentialKind.AwsProfile);

            Assert.Equal(expected, CredentialKindOptions.For(provider).Select(o => o.Kind));
        }
    }

    [Fact]
    public void The_aws_profile_kind_keeps_no_password()
        => Assert.False(CredentialKindOptions.KeepsAStoredPassword(CredentialKind.AwsProfile));

    /// <summary>Answers the credential flags and nothing else — the list under test reads only those.</summary>
    private sealed class FlagsProvider(bool password, bool entra, bool integrated, bool aws) : IDbProvider
    {
        public string Id => "flags";
        public string DisplayName => "Flags";
        public IReadOnlyList<ConnectionField> ConnectionFields { get; } = Array.Empty<ConnectionField>();
        public bool EnforcesReadOnlyOnServer => false;
        public bool SupportsSessionTimeZone => false;
        public bool SupportsIntegratedAuth => integrated;
        public bool SupportsEntraToken => entra;
        public bool SupportsPasswordCredentials => password;
        public bool SupportsAwsProfile => aws;
        public bool SupportsManualCommit => false;
        public bool SupportsTlsModes => false;
        public bool SupportsServerActivity => false;

        public IServerActivity CreateServerActivity(IDbConnectionFactory factory) => throw new NotSupportedException();
        public DbErrorKind Classify(QueryError error) => DbErrorKind.Unknown;
        public DbErrorKind ClassifyException(Exception exception) => DbErrorKind.Unknown;
        public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password)
            => throw new NotSupportedException();
        public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory) => throw new NotSupportedException();
        public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory) => throw new NotSupportedException();
        public ITransactionScope CreateTransactionScope(IDbConnectionFactory factory) => throw new NotSupportedException();
    }

    [Fact]
    public void Windows_authentication_is_named_as_such_in_the_dropdown()
        => Assert.Contains("Windows",
            CredentialKindOptions.For(new SqlServerProvider())
                .Single(o => o.Kind == CredentialKind.Integrated).Label);
}
