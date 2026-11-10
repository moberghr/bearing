using System;
using System.Linq;
using System.Text.Json;
using Bearing.Data.MySql;
using Bearing.Data.Postgres;
using Bearing.Data.SqlServer;
using Bearing.Sessions;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Browse Azure's az side, without az: the Resource Graph query and body it sends, the URL it reads databases
/// from, and how the answers are read. The az process itself is <c>AzCli</c>, shared with the Entra token.
/// </summary>
public class AzureCatalogTests
{
    private static AzureServer Server(string name = "pg-prod", string id = "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.DBforPostgreSQL/flexibleServers/pg-prod")
        => new(id, name, $"{name}.postgres.database.azure.com", "rg-data", "Production", "westeurope", "pgadmin", "Ready");

    [Fact]
    public void Each_engine_lists_its_own_resource_type()
    {
        Assert.Equal("microsoft.dbforpostgresql/flexibleservers", ProviderTraits.For(PostgresProvider.ProviderId).AzureServers.ResourceType);
        Assert.Equal("microsoft.sql/servers", ProviderTraits.For(SqlServerProvider.ProviderId).AzureServers.ResourceType);
        Assert.Equal("microsoft.dbformysql/flexibleservers", ProviderTraits.For(MySqlProvider.ProviderId).AzureServers.ResourceType);
    }

    [Fact]
    public void The_query_filters_on_the_type_and_names_the_subscription()
    {
        var query = AzureCatalog.ServerQuery("microsoft.sql/servers");

        Assert.StartsWith("resources | where type =~ 'microsoft.sql/servers'", query);
        Assert.Contains("subscriptionName = name", query);
        Assert.Contains("host = tostring(properties.fullyQualifiedDomainName)", query);
        Assert.Contains("properties.network.publicNetworkAccess", query);
    }

    [Fact]
    public void The_body_asks_for_objects_and_carries_the_skip_token_only_when_paging()
    {
        using var first = JsonDocument.Parse(AzureCatalog.GraphBody("resources", skipToken: null));
        Assert.Equal("resources", first.RootElement.GetProperty("query").GetString());
        Assert.Equal("objectArray", first.RootElement.GetProperty("options").GetProperty("resultFormat").GetString());
        Assert.False(first.RootElement.GetProperty("options").TryGetProperty("$skipToken", out _));

        using var next = JsonDocument.Parse(AzureCatalog.GraphBody("resources", skipToken: "abc"));
        Assert.Equal("abc", next.RootElement.GetProperty("options").GetProperty("$skipToken").GetString());
    }

    [Fact]
    public void Databases_are_read_under_the_server_id_with_the_engine_api_version()
        => Assert.Equal(
            // Relative: az rest puts the active cloud's ARM endpoint in front, so sovereign clouds work too.
            "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.DBforPostgreSQL/flexibleServers/pg-prod/databases?api-version=2022-12-01",
            AzureCatalog.DatabasesUrl(Server(), ProviderTraits.For(PostgresProvider.ProviderId).AzureServers));

    [Fact]
    public void Reads_servers_and_the_next_page_token()
    {
        var json = """
            {
              "totalRecords": 3, "count": 3, "$skipToken": "next-page",
              "data": [
                { "id": "/subscriptions/s1/x/pg-a", "name": "pg-a", "resourceGroup": "rg", "location": "westeurope",
                  "subscriptionId": "s1", "subscriptionName": "Production",
                  "host": "pg-a.postgres.database.azure.com", "admin": "pgadmin", "state": "Stopped",
                  "publicAccess": "Disabled" },
                { "id": "/subscriptions/s2/x/pg-b", "name": "pg-b", "resourceGroup": "rg2", "location": "northeurope",
                  "subscriptionId": "s2", "subscriptionName": null,
                  "host": "pg-b.postgres.database.azure.com", "admin": "", "state": "" },
                { "id": "/subscriptions/s1/x/pg-c", "name": "pg-c", "host": "" }
              ]
            }
            """;

        var (servers, skip, withoutHost) = AzureCatalog.ParseServers(json);

        Assert.Equal("next-page", skip);
        // A row with no host is nothing to connect to, so it is not offered — but it is counted, not forgotten.
        Assert.Equal(["pg-a", "pg-b"], servers.Select(s => s.Name));
        Assert.Equal(1, withoutHost);
        Assert.Equal("pg-a.postgres.database.azure.com", servers[0].Host);
        Assert.Equal("Production", servers[0].Subscription);
        Assert.Equal("Stopped", servers[0].State);
        Assert.True(servers[0].IsPrivateOnly);
        // Unreported is not "private": only an explicit Disabled says so.
        Assert.False(servers[1].IsPrivateOnly);
        // A subscription Resource Graph couldn't name is still identified, by its id.
        Assert.Equal("s2", servers[1].Subscription);
        Assert.Null(servers[1].AdminLogin);
        Assert.Null(servers[1].State);
    }

    [Fact]
    public void The_last_page_has_no_token()
        => Assert.Null(AzureCatalog.ParseServers("""{ "data": [] }""").SkipToken);

    [Fact]
    public void Reads_database_names_in_order()
    {
        var listed = AzureCatalog.ParseDatabases("""
            { "value": [ { "name": "postgres" }, { "name": "Sales" }, { "name": "azure_maintenance" }, { "name": "orders" } ] }
            """);
        Assert.Equal(["azure_maintenance", "orders", "postgres", "Sales"], listed.Names);
        Assert.False(listed.Truncated);
    }

    [Fact]
    public void A_next_link_marks_the_databases_as_a_first_page()
        => Assert.True(AzureCatalog.ParseDatabases("""
            { "value": [ { "name": "postgres" } ], "nextLink": "https://management.azure.com/next?x=1&y=2" }
            """).Truncated);

    [Theory]
    [InlineData("""{ "tenantId": "t-1", "tenantDisplayName": "Arctic", "tenantDefaultDomain": "arctic.com" }""", "Arctic")]
    [InlineData("""{ "tenantId": "t-1", "tenantDefaultDomain": "arctic.com" }""", "arctic.com")]
    [InlineData("""{ "tenantId": "t-1" }""", "t-1")]
    [InlineData("not json", null)]
    public void The_tenant_is_named_as_plainly_as_az_allows(string json, string? expected)
        => Assert.Equal(expected, AzureCatalog.ParseTenant(json));

    [Fact]
    public void An_empty_listing_names_the_tenant_searched_and_that_others_were_not()
    {
        var text = Bearing.App.Views.AzureBrowseDialog.ServerStatusText(new AzureServerList([], "Arctic", false), 0);
        Assert.Contains("the Arctic tenant", text);
        Assert.Contains("Other tenants aren't searched", text);

        var unnamed = Bearing.App.Views.AzureBrowseDialog.ServerStatusText(new AzureServerList([], null, false), 0);
        Assert.Contains("the tenant az is signed into", unnamed);
    }

    [Fact]
    public void Servers_without_a_host_are_not_reported_as_none()
    {
        var text = Bearing.App.Views.AzureBrowseDialog.ServerStatusText(new AzureServerList([], "Arctic", false, WithoutHost: 3), 0);
        Assert.Equal("az found 3 servers of this engine in the Arctic tenant, but none reports a host name to connect to.", text);
        Assert.DoesNotContain("no servers", text);
    }

    [Fact]
    public void Hostless_servers_beside_listed_ones_are_counted()
        => Assert.Equal("1 server in the Arctic tenant (2 more report no host name and aren't listed)",
            Bearing.App.Views.AzureBrowseDialog.ServerStatusText(new AzureServerList([Server()], "Arctic", false, WithoutHost: 2), 1));

    // Windows: az is az.cmd, which only cmd.exe finds; every argument is quoted so cmd takes & | < > literally.
    [Fact]
    public void Windows_runs_az_through_cmd_with_every_argument_quoted()
        => Assert.Equal(
            """/d /v:off /s /c "az "rest" "--url" "/subscriptions/s1/x?api-version=1&y=2" "--body" "@C:\Users\A B\t.json"" """.TrimEnd(),
            AzCli.WindowsArguments(["rest", "--url", "/subscriptions/s1/x?api-version=1&y=2", "--body", @"@C:\Users\A B\t.json"]));

    [Theory]
    [InlineData("has\"quote")]
    [InlineData("%PATH%")]
    [InlineData("two\nlines")]
    public void Windows_refuses_what_cmd_quoting_cannot_protect(string arg)
        => Assert.Throws<InvalidOperationException>(() => AzCli.WindowsArguments(["rest", arg]));

    [Fact]
    public void A_cut_short_listing_says_so()
        => Assert.EndsWith("only the first part of a longer list was read.",
            Bearing.App.Views.AzureBrowseDialog.ServerStatusText(new AzureServerList([Server()], "Arctic", Truncated: true), 1));

    [Theory]
    [InlineData("ERROR: Please run 'az login' to setup account.", "Azure sign-in required — run `az login`.")]
    [InlineData("ERROR: (AuthorizationFailed) no access", "Could not get the list of Azure servers from az: ERROR: (AuthorizationFailed) no access")]
    [InlineData("   ", "Could not get the list of Azure servers from az: az exited with code 2.")]
    public void Az_failures_are_worded_once_for_every_caller(string stderr, string expectedStart)
        => Assert.StartsWith(expectedStart, AzCli.Describe(2, stderr, "the list of Azure servers"));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("PROD", true)]          // case-insensitive, and the subscription counts
    [InlineData("pg rg-data", true)]    // every word, from any column
    [InlineData("westeurope", true)]
    [InlineData("pg staging", false)]
    public void The_filter_needs_every_word_somewhere(string? filter, bool kept)
        => Assert.Equal(kept, Server().Matches(filter));
}
