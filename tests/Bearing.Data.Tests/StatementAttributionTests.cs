using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Testing;
using Npgsql;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// Which statement of a batch produced which result set (<see cref="QueryResult.StatementIndex"/>).
/// <para>
/// Live, and it has to be: the whole question is what Npgsql does with one string of several statements —
/// how many result sets the reader walks, and what it will tell us about them. That is a driver behaviour
/// like the temporal mappings (§5.5), and getting it wrong would caption a copied table with a neighbouring
/// query, which reads as authoritative and is silently false.
/// </para>
/// </summary>
public class StatementAttributionTests
{
    private static async Task<IQueryExecutor> ExecutorAsync()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);
        return provider.CreateQueryExecutor(factory);
    }

    [SkippableFact]
    public async Task Each_set_of_an_all_select_batch_is_numbered_with_its_own_statement()
    {
        var executor = await ExecutorAsync();

        // Three statements, similar in shape and different in text, so a set attributed to the wrong one is
        // visible rather than plausible.
        var results = await executor.ExecuteAsync(
            """
            select 'one' as which, count(*) as n from film;
            select 'two' as which, count(*) as n from actor;
            select 'three' as which, count(*) as n from category;
            """,
            new QueryOptions(), CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Success, r.Error?.Message));

        // Numbered in order — and the value in each row is the cross-check that the number belongs to that
        // set rather than to its neighbour.
        Assert.Equal([0, 1, 2], results.Select(r => r.StatementIndex));
        Assert.Equal("one", results[0].Rows[0][0]);
        Assert.Equal("two", results[1].Rows[0][0]);
        Assert.Equal("three", results[2].Rows[0][0]);
    }

    /// <summary>
    /// The behaviour the whole design turns on: <c>NextResult</c> <b>skips</b> a statement that returned no
    /// rows, so a batch mixing writes and reads yields fewer sets than it has statements — and set 1 is then
    /// statement 2. Since nothing public says which were skipped, every set of such a batch is left
    /// unattributed rather than numbered by position.
    /// </summary>
    [SkippableFact]
    public async Task A_batch_that_skipped_a_statement_is_left_unattributed_rather_than_renumbered()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireWritableAsync(factory);
        var executor = provider.CreateQueryExecutor(factory);

        var table = "public.attribution_" + Guid.NewGuid().ToString("n")[..8];
        try
        {
            await executor.ExecuteAsync($"create table {table} (id int)", new QueryOptions(), CancellationToken.None);

            var results = await executor.ExecuteAsync(
                $"""
                select 'before' as which;
                insert into {table} (id) values (1);
                select 'after' as which;
                """,
                new QueryOptions(), CancellationToken.None);

            // Three statements, two sets: the INSERT returned no rows and was skipped entirely — it does not
            // even appear as an empty result. (A lone INSERT does produce one; only a batch skips.)
            Assert.Equal(2, results.Count);
            Assert.Equal("before", results[0].Rows[0][0]);
            Assert.Equal("after", results[1].Rows[0][0]);

            // So neither set is numbered. Numbering set 1 as statement 1 would name the INSERT.
            Assert.All(results, r => Assert.Null(r.StatementIndex));
        }
        finally
        {
            await executor.ExecuteAsync($"drop table if exists {table}", new QueryOptions(), CancellationToken.None);
        }
    }

    /// <summary>
    /// Why the skipped statements can't simply be filtered out by <c>StatementType</c>, which is the obvious
    /// fix and does not work. Pinned so it isn't "tidied" into one later: the type does not partition
    /// row-returning from not.
    /// </summary>
    [SkippableFact]
    public async Task StatementType_does_not_say_whether_a_statement_returned_rows()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);

        var cs = $"Host={PgTestServer.Host};Port={PgTestServer.Port};Database={PgTestServer.Database};"
               + $"Username={PgTestServer.User};Password={PgTestServer.Password}";
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "explain select 1; analyze film; create temp table t_attr as select 1 as a; select 2 as b;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var sets = 0;
        do { sets++; } while (await reader.NextResultAsync());

#pragma warning disable CS0618 // no non-obsolete accessor reports what the driver parsed
        var statements = reader.Statements;
#pragma warning restore CS0618

        Assert.Equal(4, statements.Count);
        Assert.Equal(2, sets);                            // explain and the final select; the other two skipped

        // 'Other' covers both a statement that returns rows and one that doesn't…
        Assert.Equal(StatementType.Other, statements[0].StatementType);   // explain — returns rows
        Assert.Equal(StatementType.Other, statements[1].StatementType);   // analyze — returns none
        // …and 'Select' is reported for one that returns none at all.
        Assert.Equal(StatementType.Select, statements[2].StatementType);  // create table as
        Assert.Equal(StatementType.Select, statements[3].StatementType);

        // The per-statement text isn't public either: every entry carries the whole batch.
        Assert.All(statements, s => Assert.Contains("analyze film", s.CommandText));
    }

    /// <summary>A lone statement is numbered 0 — trivially provable, and pinned because the copy path
    /// deliberately ignores it there (see <c>ResultSetBuilder.StatementsBehind</c>: that is the one case
    /// <c>FirstPageLimiter</c> rewrites, so the user's own text is the honest caption).</summary>
    [SkippableFact]
    public async Task A_single_statement_is_numbered_zero()
    {
        var executor = await ExecutorAsync();

        var results = await executor.ExecuteAsync(
            "select title from film order by film_id limit 2", new QueryOptions(), CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(0, results[0].StatementIndex);
    }
}
