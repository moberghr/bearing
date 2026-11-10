using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Bearing.Sessions;

/// <summary>One Azure server the signed-in az account can see.</summary>
/// <param name="Id">The ARM resource id — what its databases are read under.</param>
/// <param name="Host">The fully qualified domain name, which is what the Host field wants.</param>
/// <param name="AdminLogin">The server admin's login as Azure reports it, or null. Shown, never filled in: it is
/// rarely the account anyone should connect as.</param>
/// <param name="State">The server's state (<c>Ready</c>, <c>Stopped</c>…) where the engine reports one.</param>
/// <param name="PublicAccess">Azure's <c>publicNetworkAccess</c> (<c>Enabled</c> / <c>Disabled</c>), or null when it
/// reports none. <c>Disabled</c> means Azure accepts it only over private networking (its VNet or a private endpoint);
/// how a connect from elsewhere fails differs by engine, so nothing here says which way.</param>
public sealed record AzureServer(
    string Id,
    string Name,
    string Host,
    string ResourceGroup,
    string Subscription,
    string Location,
    string? AdminLogin,
    string? State,
    string? PublicAccess = null)
{
    /// <summary>True only when Azure says public access is off — not when it says nothing, which is not the same
    /// answer (§1.1).</summary>
    public bool IsPrivateOnly => string.Equals(PublicAccess, "Disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the browser's filter box keeps this row: every word typed appears in the name, host,
    /// resource group, subscription or location (case-insensitive). Blank keeps everything.</summary>
    public bool Matches(string? filter)
    {
        var haystack = string.Join(' ', Name, Host, ResourceGroup, Subscription, Location);
        return (filter ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>What one server listing found.</summary>
/// <param name="Tenant">The tenant that was searched, as az names it (display name, else default domain, else
/// id), or null when az wouldn't say. Resource Graph sees only the tenant az is signed into, so an empty list
/// means "none here", and this says where "here" is.</param>
/// <param name="Truncated">More servers exist than were read; the list is a first part, not the whole.</param>
/// <param name="WithoutHost">Servers Azure returned with no host name, so not listed — counted, so "found but nothing
/// to connect to" is never reported as "found none" (§1.1).</param>
public sealed record AzureServerList(IReadOnlyList<AzureServer> Servers, string? Tenant, bool Truncated, int WithoutHost = 0);

/// <summary>A server's database names, ordered. <paramref name="Truncated"/>: Azure has more than the first page,
/// which is all that was read.</summary>
public sealed record AzureDatabaseList(IReadOnlyList<string> Names, bool Truncated);

/// <summary>Lists the Azure servers, and their databases, that the user's az sign-in can see — for the
/// connection editor's Browse Azure. Read-only: it asks Azure what exists and changes nothing.</summary>
public interface IAzureCatalog
{
    /// <summary>Every server of <paramref name="providerId"/>'s engine in the subscriptions az can reach within
    /// its signed-in tenant, ordered by name.</summary>
    Task<AzureServerList> ListServersAsync(string providerId, CancellationToken ct);

    /// <summary><paramref name="server"/>'s databases.</summary>
    Task<AzureDatabaseList> ListDatabasesAsync(string providerId, AzureServer server, CancellationToken ct);
}

/// <summary>
/// <see cref="IAzureCatalog"/> over the Azure CLI, in keeping with <see cref="EntraTokenProvider"/>: no Azure SDK,
/// it reuses the user's existing <c>az login</c>, and <c>az rest</c> handles the ARM token.
/// <para>
/// Servers come from one Azure Resource Graph query rather than <c>az postgres flexible-server list</c> /
/// <c>az sql server list</c>: those see only the current subscription, so a server in another one would simply
/// not be there, and the user would have no way to tell "not there" from "not looked for". Resource Graph covers
/// every subscription the account can read <b>in the tenant az is signed into</b> — not other tenants (a B2B
/// guest's host tenant, say) — in one call, without the <c>resource-graph</c> extension. The listing names that
/// tenant so an empty answer says where it looked.
/// </para>
/// <para>
/// Databases are read per server from its ARM <c>/databases</c> collection when the server is picked — first
/// page only: following <c>nextLink</c> would put an <c>&amp;</c> on az's command line, which <c>az.cmd</c> on
/// Windows hands to cmd.exe as a command separator. A further page is reported, not silently dropped.
/// </para>
/// <para>
/// URLs are relative: <c>az rest</c> prefixes the active cloud's ARM endpoint, so sovereign clouds
/// (<c>az cloud set</c>) are browsed where they live rather than on the public one.
/// </para>
/// </summary>
public sealed class AzureCatalog : IAzureCatalog
{
    private const string GraphUrl = "/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01";

    /// <summary>Resource Graph answers a cross-subscription query slower than a token request; give it room.</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Pages read before stopping. 1,000 servers a page — far past anything one person browses.</summary>
    private const int MaxPages = 5;

    public async Task<AzureServerList> ListServersAsync(string providerId, CancellationToken ct)
    {
        // The tenant is asked alongside the listing, under its own token: a listing that fails must not leave
        // that second az running (or its task unobserved) after the error is already shown.
        using var tenantCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tenant = TenantAsync(tenantCts.Token);
        var query = ServerQuery(ProviderTraits.For(providerId).AzureServers.ResourceType);
        var servers = new List<AzureServer>();
        var withoutHost = 0;
        string? skipToken = null;
        try
        {
            for (var page = 0; page < MaxPages; page++)
            {
                var json = await RestAsync(["--method", "post", "--url", GraphUrl], GraphBody(query, skipToken),
                    "the list of Azure servers", ct);
                var parsed = ParseServers(json);
                servers.AddRange(parsed.Servers);
                withoutHost += parsed.WithoutHost;
                skipToken = parsed.SkipToken;
                if (skipToken is null)
                {
                    break;
                }
            }
        }
        catch
        {
            tenantCts.Cancel();
            try
            {
                await tenant;
            }
            catch (OperationCanceledException)
            {
                // Cancelled just above; the listing's own failure is the one to report.
            }
            throw;
        }
        return new AzureServerList(
            servers.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            await tenant,
            Truncated: skipToken is not null,
            WithoutHost: withoutHost);
    }

    public async Task<AzureDatabaseList> ListDatabasesAsync(
        string providerId, AzureServer server, CancellationToken ct)
    {
        var json = await RestAsync(
            ["--method", "get", "--url", DatabasesUrl(server, ProviderTraits.For(providerId).AzureServers)],
            body: null, $"the databases on {server.Name}", ct);
        return ParseDatabases(json);
    }

    /// <summary>The signed-in tenant's name, for the status line. Best-effort: the listing stands without it,
    /// so a failure here is null ("not said"), never an error.</summary>
    private static async Task<string?> TenantAsync(CancellationToken ct)
    {
        try
        {
            var (exit, stdout, _) = await AzCli.RunAsync(
                ["account", "show", "--output", "json"], TimeSpan.FromSeconds(30), "the signed-in tenant", ct);
            return exit == 0 ? ParseTenant(stdout) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>How <c>az account show</c> names its tenant: display name, else default domain, else id. Pure.</summary>
    public static string? ParseTenant(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return Text(root, "tenantDisplayName") ?? Text(root, "tenantDefaultDomain") ?? Text(root, "tenantId");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The Resource Graph query for one resource type, with each server's subscription named rather than
    /// numbered. The type is ours (<see cref="ProviderTraits"/>), never typed by the user.</summary>
    public static string ServerQuery(string resourceType)
        => $"resources | where type =~ '{resourceType}'"
           + " | join kind=leftouter (resourcecontainers"
           + " | where type =~ 'microsoft.resources/subscriptions'"
           + " | project subscriptionId, subscriptionName = name) on subscriptionId"
           + " | project id, name, resourceGroup, location, subscriptionId, subscriptionName,"
           + " host = tostring(properties.fullyQualifiedDomainName),"
           + " admin = tostring(properties.administratorLogin),"
           + " state = tostring(properties.state),"
           // Postgres flexible server keeps it under network, Azure SQL at the top of properties.
           + " publicAccess = coalesce(tostring(properties.network.publicNetworkAccess),"
           + " tostring(properties.publicNetworkAccess))";

    public static string GraphBody(string query, string? skipToken)
    {
        var options = new Dictionary<string, object> { ["resultFormat"] = "objectArray", ["$top"] = 1000 };
        if (skipToken is not null)
        {
            options["$skipToken"] = skipToken;
        }
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["query"] = query, ["options"] = options });
    }

    public static string DatabasesUrl(AzureServer server, AzureServerKind kind)
        => $"{server.Id}/databases?api-version={kind.DatabasesApiVersion}";

    /// <summary>One page of a Resource Graph response: its rows, the token for the next page if there is one, and
    /// how many rows had no host — those are not listed (there is nothing to connect to) but are counted, so the
    /// dialog can tell them from "none". Pure, so it is testable without az.</summary>
    public static (IReadOnlyList<AzureServer> Servers, string? SkipToken, int WithoutHost) ParseServers(string json)
    {
        var withoutHost = 0;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var servers = new List<AzureServer>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in data.EnumerateArray())
            {
                var host = Text(row, "host");
                if (host is null)
                {
                    withoutHost++;
                    continue;
                }
                servers.Add(new AzureServer(
                    Id: Text(row, "id") ?? "",
                    Name: Text(row, "name") ?? host,
                    Host: host,
                    ResourceGroup: Text(row, "resourceGroup") ?? "",
                    Subscription: Text(row, "subscriptionName") ?? Text(row, "subscriptionId") ?? "",
                    Location: Text(row, "location") ?? "",
                    AdminLogin: Text(row, "admin"),
                    State: Text(row, "state"),
                    PublicAccess: Text(row, "publicAccess")));
            }
        }
        return (servers, Text(root, "$skipToken"), withoutHost);
    }

    /// <summary>The database names in an ARM <c>/databases</c> list response, ordered, and whether a
    /// <c>nextLink</c> says there are more. Pure.</summary>
    public static AzureDatabaseList ParseDatabases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var db in value.EnumerateArray())
            {
                if (Text(db, "name") is { } name)
                {
                    names.Add(name);
                }
            }
        }
        return new AzureDatabaseList(
            names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            Truncated: Text(doc.RootElement, "nextLink") is not null);
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim() : null;

    /// <summary>
    /// <c>az rest</c> with an optional JSON body. The body goes through a temporary file (<c>--body @path</c>)
    /// rather than on the command line: on Windows az is a batch file, and cmd.exe would take the quotes in a
    /// JSON argument apart. The file holds only the query, nothing secret, and is deleted afterwards.
    /// </summary>
    private static async Task<string> RestAsync(
        IReadOnlyList<string> args, string? body, string what, CancellationToken ct)
    {
        string? bodyFile = null;
        try
        {
            var all = new List<string>(args) { "--output", "json" };
            if (body is not null)
            {
                bodyFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(bodyFile, body, ct);
                all.AddRange(["--body", "@" + bodyFile, "--headers", "Content-Type=application/json"]);
            }

            var (exit, stdout, stderr) = await AzCli.RunAsync(["rest", .. all], RunTimeout, what, ct);
            if (exit != 0)
            {
                throw new InvalidOperationException(AzCli.Describe(exit, stderr, what));
            }
            return stdout;
        }
        finally
        {
            if (bodyFile is not null)
            {
                try { File.Delete(bodyFile); } catch { /* best-effort: a leftover holds only the query */ }
            }
        }
    }
}
