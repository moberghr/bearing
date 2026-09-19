using Bearing.Core.Data;
using Bearing.Data.Postgres;
using Bearing.Data.SqlServer;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// What the app can be asked to connect with. The registry is how a provider id in a saved project file
/// becomes an engine, so a provider that is written but not registered is a provider that does not exist —
/// and one that is registered under a different id than its dialect's silently generates the wrong SQL
/// (which <c>Bearing.App.Tests.ProviderTraitsTests</c> pins from the other side).
/// <para>Pure: constructing a provider or a connection factory opens no connection.</para>
/// </summary>
public class ProviderRegistryTests
{
    [Fact]
    public void Both_engines_are_registered_with_postgres_first()
    {
        var all = new ProviderRegistry().All.ToList();

        // The order is the order the connection dialog offers them in, and PostgreSQL — the engine every
        // existing project file names — stays first.
        Assert.Equal(
            new[] { PostgresProvider.ProviderId, SqlServerProvider.ProviderId },
            all.Select(p => p.Id));
        Assert.Equal("Microsoft SQL Server", all[1].DisplayName);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("SqlServer")]
    [InlineData("SQLSERVER")]
    public void Sql_server_resolves_by_id_case_insensitively(string id)
        => Assert.IsType<SqlServerProvider>(new ProviderRegistry().Get(id));

    [Fact]
    public void An_unregistered_id_is_a_named_failure_not_a_silent_fallback()
    {
        // A project file naming an engine this build does not ship must say so, rather than quietly
        // connecting with whichever provider happened to be first.
        var ex = Assert.Throws<KeyNotFoundException>(() => new ProviderRegistry().Get("mysql"));
        Assert.Contains("mysql", ex.Message);
    }

    [Fact]
    public void The_sql_server_provider_builds_its_own_trio()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        var factory = provider.CreateConnectionFactory(MsSqlTestServer.Info(), MsSqlTestServer.Password);

        Assert.IsType<SqlServerConnectionFactory>(factory);
        Assert.IsType<SqlServerMetadataReader>(provider.CreateMetadataReader(factory));
        Assert.IsType<SqlServerQueryExecutor>(provider.CreateQueryExecutor(factory));
    }

    /// <summary>
    /// A named instance (<c>HOST\INSTANCE</c>) is resolved by the SQL Browser service, which hands back the
    /// instance's own dynamic port — so the port must not be appended, and a connection carrying one must
    /// still build. What cannot be asserted here is the resulting <c>Data Source</c>: the connection string
    /// holds the password and deliberately never leaves the factory (§1.1), and exposing it for a test would
    /// be a worse trade than leaving this at "it builds".
    /// </summary>
    [Fact]
    public void A_named_instance_drops_the_port_and_a_plain_host_joins_it_with_a_comma()
    {
        // Asserted as strings, because "the constructor did not throw" let both rules regress silently:
        // a colon separator makes SqlClient look up a host literally named "sqlprod:1433" (a DNS failure,
        // not a port error), and appending a port to a named instance overrides the SQL Browser lookup and
        // aims at the wrong endpoint. Only the Data Source is exposed — never the connection string (§1.1).
        var plain = MsSqlTestServer.Info() with { Host = "sqlprod", Port = 1433 };
        Assert.Equal("sqlprod,1433", SqlServerConnectionFactory.DataSourceFor(plain));

        var instance = MsSqlTestServer.Info() with { Host = @"box\SQLEXPRESS", Port = 1433 };
        Assert.Equal(@"box\SQLEXPRESS", SqlServerConnectionFactory.DataSourceFor(instance));

        // No port configured at all: the host stands alone rather than gaining a ",0".
        var noPort = MsSqlTestServer.Info() with { Host = "sqlprod", Port = 0 };
        Assert.Equal("sqlprod", SqlServerConnectionFactory.DataSourceFor(noPort));

        // Whitespace is trimmed, and the factory still builds for each shape.
        Assert.Equal("sqlprod,1433", SqlServerConnectionFactory.DataSourceFor(plain with { Host = "  sqlprod  " }));
        Assert.NotNull(new SqlServerConnectionFactory(instance, "pw"));
    }

    /// <summary>Integrated authentication resolves no secret at all, so the factory has to build without
    /// one — the password argument is null on that path by design (§4c), not by accident.</summary>
    [Fact]
    public void An_integrated_connection_builds_without_a_password()
    {
        var info = MsSqlTestServer.Info() with { CredentialKind = CredentialKind.Integrated };
        Assert.NotNull(new SqlServerConnectionFactory(info, null));
    }

    [Fact]
    public void The_postgres_field_list_is_untouched_by_the_second_engine()
    {
        // Adding SQL Server must not have added, removed or re-defaulted a field on the engine every
        // existing connection uses.
        var fields = new PostgresProvider().ConnectionFields;

        Assert.Equal(
            new[] { "Host", "Port", "Database", "User", "Password" },
            fields.Select(f => f.Key));
        Assert.Equal("5432", fields.Single(f => f.Key == "Port").Default);
        // sslmode is no longer a declared field: transport security is the typed ConnectionInfo.Tls (#23).
        Assert.DoesNotContain("sslmode", fields.Select(f => f.Key));
    }

    [Fact]
    public void The_sql_server_field_list_defaults_to_the_engines_own_port()
    {
        var fields = new SqlServerProvider().ConnectionFields;

        Assert.Equal(
            new[] { "Host", "Port", "Database", "User", "Password" },
            fields.Select(f => f.Key));
        Assert.Equal("1433", fields.Single(f => f.Key == "Port").Default);
        // Encrypt/TrustServerCertificate followed sslmode onto the typed field, for the same reason: a
        // security setting does not belong in an options bag that travels in a shared project file (#23).
        Assert.DoesNotContain("Encrypt", fields.Select(f => f.Key));
        Assert.DoesNotContain("TrustServerCertificate", fields.Select(f => f.Key));
    }
}
