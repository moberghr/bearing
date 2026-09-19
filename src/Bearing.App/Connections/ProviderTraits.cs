using System;
using Bearing.App.Results;
using Bearing.Core.Data;
using Bearing.Data.Postgres;
using Bearing.Data.SqlServer;
using Bearing.Sql;

namespace Bearing.App.Connections;

/// <summary>
/// The App-side facts about one engine that no <c>Core</c> abstraction carries: which
/// <see cref="ISqlDialect"/> shapes its SQL text, how a value is written as a literal, which AAD resource
/// its Entra tokens are minted for, and what the connection editor should say about its endpoint.
/// <para>
/// They live here, together, for two reasons. They are <b>text and UI concerns</b>, so putting them on
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
public sealed record ProviderTraits(
    string ProviderId,
    ISqlDialect Dialect,
    SqlLiteralStyle Literals,
    string EntraResource,
    string? EndpointHint)
{
    /// <summary>PostgreSQL — the behaviour every caller had when there was one engine, so it is also the
    /// fallback below.</summary>
    public static ProviderTraits Postgres { get; } = new(
        PostgresProvider.ProviderId,
        PostgresDialect.Instance,
        SqlLiteralStyle.Postgres,
        // Azure Database for PostgreSQL's own resource. Unchanged: an existing Entra connection must keep
        // minting exactly the token it minted before a second engine arrived.
        EntraResource: "https://ossrdbms-aad.database.windows.net",
        EndpointHint: null);

    /// <summary>Microsoft SQL Server.</summary>
    public static ProviderTraits SqlServer { get; } = new(
        SqlServerProvider.ProviderId,
        SqlServerDialect.Instance,
        SqlLiteralStyle.TSql,
        // Azure SQL Database / Managed Instance. A different resource from the Postgres one above, which is
        // the whole reason this is per-provider rather than a constant.
        EntraResource: "https://database.windows.net/",
        EndpointHint: "A named instance (HOST\\INSTANCE) is resolved by the SQL Browser service, "
                    + "which ignores the port.");

    /// <summary>
    /// The traits for a provider id, matched the way the registry matches it (case-insensitively).
    /// <para>
    /// An id nothing here knows falls back to <see cref="Postgres"/> rather than throwing: these are text
    /// and copy decisions on paths that must not fail — paging a grid, rendering a preview — and Postgres
    /// is what every one of them did before. The cost of the fallback is a wrong page suffix for a fourth
    /// engine that shipped a provider without adding an arm here; the cost of throwing is a crash in the
    /// results grid. Adding the arm is the fix, and <c>ProviderTraitsTests</c> asserts every registered
    /// provider has one.
    /// </para>
    /// </summary>
    public static ProviderTraits For(string? providerId)
        => string.Equals(providerId, SqlServer.ProviderId, StringComparison.OrdinalIgnoreCase)
            ? SqlServer
            : Postgres;

    /// <summary>The traits for the engine <paramref name="info"/> connects to. The overload every caller
    /// holding a connection should use.</summary>
    public static ProviderTraits For(ConnectionInfo? info) => For(info?.ProviderId);
}
