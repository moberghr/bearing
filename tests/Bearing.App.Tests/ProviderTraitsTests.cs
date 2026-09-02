using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Connections;
using Bearing.App.Results;
using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Data.SqlServer;
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
    public void A_traits_dialect_shares_its_providers_id()
    {
        Assert.Equal(PostgresProvider.ProviderId, ProviderTraits.Postgres.Dialect.Id);
        Assert.Equal(SqlServerProvider.ProviderId, ProviderTraits.SqlServer.Dialect.Id);
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

/// <summary>How the app names a connection's endpoint when it has to put it in a message.</summary>
public class ConnectionEndpointTests
{
    [Fact]
    public void An_ordinary_host_keeps_host_colon_port()
        => Assert.Equal("db:5432/pagila", ConnectionEndpoint.Of("db", 5432, "pagila"));

    [Fact]
    public void A_named_instance_drops_the_port()
    {
        // A named instance is resolved by the SQL Browser service, so the port is not part of the address —
        // printing one sends the user to check something that had nothing to do with their failure.
        Assert.Equal(@"SQLPROD\SALES/sales", ConnectionEndpoint.Of(@"SQLPROD\SALES", 1433, "sales"));
        Assert.True(ConnectionEndpoint.IsNamedInstance(@"SQLPROD\SALES"));
        Assert.False(ConnectionEndpoint.IsNamedInstance("sqlprod"));
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
    public void Entra_is_offered_only_where_the_driver_will_take_the_token_as_a_password()
    {
        // Npgsql takes an Entra token as the password, so Postgres offers it. SqlClient does not — it wants
        // SqlConnection.AccessToken — and the factory has no such path in Phase 1, so offering the entry
        // would be promising a login that cannot succeed. Same gate that already governs Integrated.
        Assert.Contains(CredentialKind.EntraToken,
            CredentialKindOptions.For(new PostgresProvider()).Select(o => o.Kind));
        Assert.DoesNotContain(CredentialKind.EntraToken,
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
    public void Windows_authentication_is_named_as_such_in_the_dropdown()
        => Assert.Contains("Windows",
            CredentialKindOptions.For(new SqlServerProvider())
                .Single(o => o.Kind == CredentialKind.Integrated).Label);
}
