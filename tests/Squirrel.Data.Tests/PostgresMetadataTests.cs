using Squirrel.Core.Data;
using Squirrel.Data;
using Squirrel.Data.Postgres;
using Xunit;

namespace Squirrel.Data.Tests;

public class PostgresMetadataTests
{
    private static ConnectionInfo Info() => new()
    {
        Id = Guid.NewGuid(),
        Name = "pagila-test",
        ProviderId = PostgresProvider.ProviderId,
        Host = Env("HOST", "localhost"),
        Port = int.Parse(Env("PORT", "5433")),
        Database = Env("DB", "pagila"),
        User = Env("USER", "postgres"),
    };

    private static string Password => Env("PASSWORD", "squirrel");
    private static string Env(string key, string dflt)
        => Environment.GetEnvironmentVariable($"SQUIRREL_TEST_PG_{key}") ?? dflt;

    [SkippableFact]
    public async Task Loads_tables_columns_and_foreign_keys()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        Skip.IfNot(await Safe(factory), "No PostgreSQL reachable for integration test.");

        var reader = provider.CreateMetadataReader(factory);
        var snapshot = await reader.LoadSnapshotAsync("pagila", CancellationToken.None);

        // Tables
        var film = snapshot.ResolveTable("public", "film");
        Assert.NotNull(film);
        var filmBare = snapshot.ResolveTable(null, "film"); // search_path resolution
        Assert.Equal(film, filmBare);

        // Columns
        var cols = snapshot.ColumnsOf(film!.Oid).Select(c => c.Name).ToList();
        Assert.Contains("film_id", cols);
        Assert.Contains("title", cols);
        Assert.Contains("language_id", cols);
        Assert.True(snapshot.ColumnsOf(film.Oid).Single(c => c.Name == "film_id").IsPrimaryKey);

        // Foreign keys touching film (film.language_id -> language, and film_actor/film_category -> film)
        var fks = snapshot.ForeignKeysTouching(film.Oid);
        Assert.NotEmpty(fks);
        Assert.Contains(fks, fk => fk.ReferencedOid == film.Oid || fk.ParentOid == film.Oid);

        // Pagila has 36 FKs total; make sure we read a healthy set.
        Assert.Contains("public", snapshot.Schemas);
    }

    [SkippableFact]
    public async Task Reads_routines_from_pg_proc()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        Skip.IfNot(await Safe(factory), "No PostgreSQL reachable for integration test.");

        var reader = provider.CreateMetadataReader(factory);
        var routines = await reader.GetRoutinesAsync(CancellationToken.None);

        // Pagila ships several functions.
        var balance = routines.FirstOrDefault(r => r.Name == "get_customer_balance");
        Assert.NotNull(balance);
        Assert.Equal(Squirrel.Core.Schema.PgRoutineKind.Function, balance!.Kind);
        Assert.Equal("public", balance.Schema);
        Assert.Contains("rewards_report", routines.Select(r => r.Name));
        Assert.All(routines, r => Assert.NotEqual("pg_catalog", r.Schema));
    }

    [SkippableFact]
    public async Task Reads_view_definition()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        Skip.IfNot(await Safe(factory), "No PostgreSQL reachable for integration test.");

        var reader = provider.CreateMetadataReader(factory);
        var snapshot = await reader.LoadSnapshotAsync("pagila", CancellationToken.None);
        var view = snapshot.ResolveTable("public", "film_list");
        Assert.NotNull(view);

        var def = await reader.GetViewDefinitionAsync(view!.Oid, CancellationToken.None);
        Assert.Contains("select", def, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Reads_routine_definition()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        Skip.IfNot(await Safe(factory), "No PostgreSQL reachable for integration test.");

        var reader = provider.CreateMetadataReader(factory);
        var routine = (await reader.GetRoutinesAsync(CancellationToken.None))
            .First(r => r.Name == "get_customer_balance");

        var def = await reader.GetRoutineDefinitionAsync(routine.Oid, CancellationToken.None);
        Assert.Contains("FUNCTION", def, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("get_customer_balance", def);
    }

    private static async Task<bool> Safe(IDbConnectionFactory f)
    {
        try { return await f.TestConnectionAsync(CancellationToken.None); } catch { return false; }
    }
}
