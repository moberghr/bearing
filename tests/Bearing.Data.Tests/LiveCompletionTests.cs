using Bearing.Core.Completion;
using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Sql;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// End-to-end M3 check: load the real pagila catalog, then drive the completion engine against it,
/// proving schema-aware table/column suggestions work on live metadata (not just a hand-built snapshot).
/// </summary>
public class LiveCompletionTests
{
    private static ConnectionInfo Info() => PgTestServer.Info();
    private static string Password => PgTestServer.Password;

    [SkippableFact]
    public async Task Completes_pagila_tables_and_columns_from_live_schema()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var snapshot = await provider.CreateMetadataReader(factory)
            .LoadSnapshotAsync("pagila", CancellationToken.None);
        var engine = new CompletionEngine();

        // Table position after FROM offers pagila tables.
        var afterFrom = engine.Complete("select * from fil", caretOffset: 17, snapshot);
        var tables = afterFrom.Suggestions.Where(s => s.Kind is SuggestionKind.Table).Select(s => s.DisplayText).ToList();
        Assert.Contains("film", tables);
        Assert.Contains("film_actor", tables);

        // Column position in the select list offers columns of pagila tables.
        var inSelect = engine.Complete("select ti from film", caretOffset: 9, snapshot);
        var cols = inSelect.Suggestions.Where(s => s.Kind is SuggestionKind.Column).Select(s => s.DisplayText).ToList();
        Assert.Contains("title", cols);
        Assert.Contains("film_id", cols);

        // FK smart-join: after "from film f join", real pagila foreign keys are synthesized.
        const string joinSql = "select * from film f join ";
        var joins = engine.Complete(joinSql, joinSql.Length, snapshot)
            .Suggestions.Where(s => s.Kind is SuggestionKind.Join).ToList();
        Assert.NotEmpty(joins);
        // film_actor.film_id -> film.film_id  (film is the referenced side)
        var filmActor = joins.FirstOrDefault(j => j.DisplayText == "film_actor");
        Assert.NotNull(filmActor);
        Assert.Contains("f.film_id", filmActor!.ReplacementText);
        // film.language_id -> language.language_id (film is the referencing side)
        Assert.Contains(joins, j => j.DisplayText == "language" && j.ReplacementText.Contains("f.language_id"));
    }
}
