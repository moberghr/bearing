using System;
using Bearing.Core.Data;
using Bearing.Data.DynamoDb;
using Bearing.Data.Postgres;
using Bearing.Data.MySql;
using Bearing.Data.SqlServer;
using Bearing.Sql;

namespace Bearing.Sessions;

/// <summary>
/// The facts about one engine that no <c>Core</c> abstraction carries: which
/// <see cref="ISqlDialect"/> shapes its SQL text, how a value is written as a literal, which AAD resource
/// its Entra tokens are minted for, and what the connection editor should say about its endpoint.
/// <para>
/// They live here, together, for two reasons. They are <b>text and presentation concerns</b>, so putting them on
/// <c>IDbProvider</c> would drag paging syntax and dialog copy into the driver contract
/// (<c>Bearing.Sql</c> and <c>Bearing.Data</c> deliberately do not reference each other — §2.2, which is
/// why a provider and its dialect are paired by a shared id rather than by a property). And keeping the
/// id-matching in exactly one place means a third engine is one arm added here, not a search for every
/// place that assumed Postgres.
/// </para>
/// <para>
/// <b>Resolve these from the connection, never once at startup.</b> A tab connected to SQL Server that
/// pages with <c>limit/offset</c> is the failure this type exists to prevent: the dialect has to travel
/// with the session, so every caller asks <see cref="For(ConnectionInfo)"/> for the connection it is
/// actually running against.
/// </para>
/// </summary>
/// <param name="GridEditUnavailable">Why the results grid is never editable on this engine — the lock chip's
/// sentence, said before any per-result reason is looked for — or null where editing depends on the result.
/// Its own member because the per-result reasons would be false here: a result with no base columns reads as
/// "a computed expression", which is not why an engine without grid editing refuses.</param>
/// <param name="OffersTableDdl">Whether a table's definition (DDL) can be shown. False where the engine has
/// no table DDL for Bearing to render.</param>
public sealed record ProviderTraits(
    string ProviderId,
    ISqlDialect Dialect,
    SqlLiteralStyle Literals,
    string EntraResource,
    string? EndpointHint,
    bool EntraTakesUser,
    AzureServerKind AzureServers,
    string? GridEditUnavailable,
    bool OffersTableDdl)
{
    /// <summary>
    /// Whether this engine asks the full-table-scan question at all — the connection editor's "Confirm full table
    /// scans" setting only means something where it does. Read from the dialect rather than stored as a second
    /// fact: <see cref="ISqlDialect.ScanTargets"/> is null exactly for an engine that never asks, and an empty list
    /// (no statement) for one that does, so the two cannot drift apart.
    /// </summary>
    public bool AsksFullScanQuestion => Dialect.ScanTargets("") is not null;

    /// <summary>
    /// The sentence naming what actually stops a write on an engine where <b>only Bearing refuses</b>
    /// (<see cref="IDbProvider.EnforcesReadOnlyOnServer"/> false), for the connection editor's read-only note —
    /// or null where the generic wording already says enough (SQL Server: a role without write privileges).
    /// </summary>
    public string? WriteBoundary { get; init; }

    /// <summary>
    /// Whether this engine's reads continue from a server-side token (<see cref="QueryResult.Continuation"/>)
    /// rather than by re-running the statement with an offset — so a host that must read a result to its end
    /// follows <see cref="IQueryExecutor.ExecuteContinuationAsync"/> instead of streaming from the start. Its own
    /// fact rather than read off <see cref="AsksFullScanQuestion"/>: the two happen to be true of the same engine
    /// (DynamoDB), and nothing makes the next engine with one also have the other.
    /// </summary>
    public bool ReadsContinueByToken { get; init; }

    /// <summary>
    /// Whether expanding one of this engine's tables in the tree reads a small sample of its items
    /// (<see cref="IMetadataReader.SampleAttributesAsync"/>): an engine whose tables declare no columns beyond
    /// their keys. False everywhere else, and then nothing is asked for — no reader is built, so no connect, no
    /// prompt and no "couldn't sample" row under a table whose columns the catalog already gave.
    /// </summary>
    public bool SamplesAttributes { get; init; }

    /// <summary>
    /// Keys of <see cref="ConnectionInfo.Options"/> that decide <em>which service is reached</em>, the way
    /// <see cref="ConnectionInfo.Host"/> does — so a connection whose value for one of them changes is a different
    /// network identity (the App's <c>SameNetwork</c>: a new tree, a new schema, a new pool). Compared trimmed, blank
    /// as absent. Empty for an engine whose options bag only tunes a connection to the same server.
    /// </summary>
    public IReadOnlyList<string> NetworkOptions { get; init; } = [];

    /// <summary>PostgreSQL — the behaviour every caller had when there was one engine, so it is also the
    /// fallback below.</summary>
    public static ProviderTraits Postgres { get; } = new(
        PostgresProvider.ProviderId,
        PostgresDialect.Instance,
        SqlLiteralStyle.Postgres,
        // Azure Database for PostgreSQL's own resource. Unchanged: an existing Entra connection must keep
        // minting exactly the token it minted before a second engine arrived.
        EntraResource: "https://ossrdbms-aad.database.windows.net",
        EndpointHint: null,
        // Azure Database for PostgreSQL wants the role name as the user, alongside the token as password.
        EntraTakesUser: true,
        // Flexible server only: single server was retired in March 2025, so nothing is left to list.
        AzureServers: new("microsoft.dbforpostgresql/flexibleservers", DatabasesApiVersion: "2022-12-01"),
        GridEditUnavailable: null,
        OffersTableDdl: true);

    /// <summary>Microsoft SQL Server.</summary>
    public static ProviderTraits SqlServer { get; } = new(
        SqlServerProvider.ProviderId,
        SqlServerDialect.Instance,
        SqlLiteralStyle.TSql,
        // Azure SQL Database / Managed Instance. A different resource from the Postgres one above, which is
        // the whole reason this is per-provider rather than a constant.
        EntraResource: "https://database.windows.net/",
        EndpointHint: "A named instance (HOST\\INSTANCE) is resolved by the SQL Browser service, "
                    + "which ignores the port.",
        // SqlClient refuses a User ID next to an access token; the token is the identity.
        EntraTakesUser: false,
        // Azure SQL logical servers. Managed Instances are a different resource type and are not listed.
        AzureServers: new("microsoft.sql/servers", DatabasesApiVersion: "2021-11-01"),
        GridEditUnavailable: null,
        OffersTableDdl: true);

    /// <summary>MySQL and MariaDB — one engine id, one dialect, one set of traits.</summary>
    public static ProviderTraits MySql { get; } = new(
        MySqlProvider.ProviderId,
        MySqlDialect.Instance,
        SqlLiteralStyle.MySql,
        // Azure Database for MySQL shares Azure Database for PostgreSQL's resource: both are the
        // "open-source RDBMS" service family, and a token for one audience is refused by the other.
        EntraResource: "https://ossrdbms-aad.database.windows.net",
        EndpointHint: null,
        // The Entra user name goes in User, with the token as the password — the Postgres arrangement.
        EntraTakesUser: true,
        // Flexible server only: single server was retired in September 2024. Azure has no managed MariaDB
        // any more (retired September 2025), so a MariaDB connection browses the MySQL list too.
        AzureServers: new("microsoft.dbformysql/flexibleservers", DatabasesApiVersion: "2023-12-30"),
        GridEditUnavailable: null,
        OffersTableDdl: true);

    /// <summary>
    /// Amazon DynamoDB, written in PartiQL. Read-focused: no grid editing and no table DDL to show.
    /// <para>
    /// The three Entra / Azure members are placeholders and are never read: every reader sits behind a
    /// <see cref="CredentialKind.EntraToken"/> check (<see cref="EntraTokenProvider"/>, the connection dialog's
    /// az-login box) or behind Browse Azure, and this engine offers neither
    /// (<see cref="IDbProvider.SupportsEntraToken"/> is false). Empty rather than a plausible-looking audience, so
    /// a path that did reach one would fail visibly instead of minting a token for some other service.
    /// </para>
    /// </summary>
    public static ProviderTraits DynamoDb { get; } = new(
        DynamoDbProvider.ProviderId,
        PartiQlDialect.Instance,
        // PartiQL shares the Postgres literal forms Bearing writes: '…' strings with '' doubled, and the
        // true/false keywords. Binary has no PartiQL literal Bearing could write; "Copy as SQL INSERT" stays
        // generic SQL on this engine (a known limitation in the spec), so nothing generated is sent as typed.
        SqlLiteralStyle.Postgres,
        EntraResource: "",
        // What the Endpoint is for is said under that field itself (DynamoDbProvider.EndpointHint).
        EndpointHint: "Region is an AWS region code such as eu-west-1. A blank AWS profile uses the AWS SDK's "
                      + "default credential chain. A plain http:// endpoint is accepted for a loopback host only.",
        EntraTakesUser: false,
        AzureServers: new("", DatabasesApiVersion: ""),
        GridEditUnavailable: "Read-only — editing isn't available on Amazon DynamoDB connections yet.",
        OffersTableDdl: false)
    {
        WriteBoundary = "On Amazon DynamoDB the boundary is an IAM policy that grants no write actions "
                        + "(PutItem, UpdateItem, DeleteItem and the like).",
        ReadsContinueByToken = true,
        SamplesAttributes = true,
        // An endpoint (DynamoDB Local, a VPC endpoint) replaces the region's: a different service.
        NetworkOptions = [DynamoDbConnectionFactory.EndpointOption],
    };

    /// <summary>
    /// The traits for a provider id, matched the way the registry matches it (case-insensitively).
    /// <para>
    /// An id nothing here knows falls back to <see cref="Postgres"/> rather than throwing: these are text
    /// and copy decisions on paths that must not fail — paging a grid, rendering a preview — and Postgres
    /// is what every one of them did before. The cost of the fallback is a wrong page suffix for a fifth
    /// engine that shipped a provider without adding an arm here; the cost of throwing is a crash in the
    /// results grid. Adding the arm is the fix, and <c>ProviderTraitsTests</c> asserts every registered
    /// provider has one.
    /// </para>
    /// </summary>
    public static ProviderTraits For(string? providerId)
    {
        if (string.Equals(providerId, SqlServer.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return SqlServer;
        }
        if (string.Equals(providerId, MySql.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return MySql;
        }
        if (string.Equals(providerId, DynamoDb.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return DynamoDb;
        }
        return Postgres;
    }

    /// <summary>The traits for the engine <paramref name="info"/> connects to. The overload every caller
    /// holding a connection should use.</summary>
    public static ProviderTraits For(ConnectionInfo? info) => For(info?.ProviderId);
}

/// <summary>Where an engine's servers live in Azure, for the connection editor's Browse Azure
/// (<see cref="AzureCatalog"/>): the ARM resource type a Resource Graph query filters on, and the API version its
/// <c>/databases</c> child collection is read with.</summary>
public sealed record AzureServerKind(string ResourceType, string DatabasesApiVersion);
