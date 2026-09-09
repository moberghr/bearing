using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// #119's four catalog reads against a real server. Live rather than fixtures, and the reason is §4.6's: a
/// fixture only proves the reader parses the shape <em>we believe</em> Postgres returns, so a query that
/// asks for the wrong column or joins the wrong way would still look right in one.
/// <para>
/// pagila has sequences, extensions and no policies, so the policy and type tests create their own objects
/// in their own schema under <see cref="PgTestServer.RequireWritableAsync"/> and drop them again.
/// </para>
/// </summary>
public class PostgresObjectKindTests
{
    private static ConnectionInfo Info() => PgTestServer.Info();
    private static string Password => PgTestServer.Password;

    private const string Schema = "bearing_kinds_test";

    // ---- what pagila already has ---------------------------------------------------------------------

    /// <summary>
    /// pagila's sequences read back with their type and position.
    /// <para>
    /// Deliberately <b>not</b> asserting an owning column here. This pagila's sequences have no
    /// <c>OWNED BY</c> at all — the dump defines them standalone and wires them up with a
    /// <c>default nextval(…)</c>, so <c>pg_get_serial_sequence</c> returns nothing and there is not one
    /// 'a'/'i' dependency in the database. Asserting an owner against this fixture would be asserting our
    /// assumption about how pagila was built rather than what the query does; the ownership join is covered
    /// against a <c>serial</c> column this suite creates itself.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Sequences_come_back_with_their_type_and_position()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var kinds = await provider.CreateMetadataReader(factory).GetDatabaseObjectsAsync(CancellationToken.None);

        Assert.NotEmpty(kinds.Sequences);
        var film = kinds.Sequences.SingleOrDefault(q => q.Name == "film_film_id_seq");
        Assert.NotNull(film);
        Assert.Equal("public", film.Schema);
        // bigint: pagila's film_id column is an integer, but the *sequence* behind it is declared bigint,
        // which is Postgres' default and what pg_sequence records. The two are genuinely different types,
        // and reporting the column's would be reporting something this query never read.
        Assert.Equal("bigint", film.DataType);
        Assert.Equal(1, film.Increment);
        Assert.False(film.Cycles);
        // It has been read from — pagila ships rows — so this is a number rather than the null case.
        Assert.NotNull(film.LastValue);

        // The catalog schemas are excluded, like everywhere else in the browser.
        Assert.DoesNotContain(kinds.Sequences, q => q.Schema is "pg_catalog" or "information_schema");
    }

    [SkippableFact]
    public async Task Extensions_come_back_with_their_versions()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var kinds = await provider.CreateMetadataReader(factory).GetDatabaseObjectsAsync(CancellationToken.None);

        // plpgsql is installed in every database Postgres creates, so this is the one extension that is
        // always there to assert on.
        var plpgsql = kinds.Extensions.SingleOrDefault(e => e.Name == "plpgsql");
        Assert.NotNull(plpgsql);
        Assert.NotEqual("", plpgsql.Version);
    }

    [SkippableFact]
    public async Task Every_table_of_the_database_is_not_reported_as_a_composite_type()
    {
        // Postgres creates a composite type per table and per view. Reporting those would list every
        // relation in the database a second time under Types — noise rather than information, and the one
        // mistake this query's relkind filter exists to prevent.
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var reader = provider.CreateMetadataReader(factory);
        var kinds = await reader.GetDatabaseObjectsAsync(CancellationToken.None);
        var snapshot = await reader.LoadSnapshotAsync("pagila", CancellationToken.None);

        Assert.DoesNotContain(kinds.Types, t => t.Name == "film");
        Assert.DoesNotContain(kinds.Types, t => snapshot.ResolveTable(t.Schema, t.Name) is not null);
    }

    [SkippableFact]
    public async Task An_enum_pagila_ships_comes_back_with_its_labels_in_order()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var kinds = await provider.CreateMetadataReader(factory).GetDatabaseObjectsAsync(CancellationToken.None);

        // pagila's mpaa_rating. Skipped rather than failed if this pagila predates it: the query is what is
        // under test, and a fixture difference is not a bug in it.
        var rating = kinds.Types.SingleOrDefault(t => t.Name == "mpaa_rating");
        Skip.If(rating is null, "this pagila has no mpaa_rating enum");

        Assert.Equal(TypeKind.Enum, rating!.Kind);
        // Declaration order, not alphabetical — which is what enumsortorder gets right and an ORDER BY on
        // the label would get wrong.
        Assert.Equal("'G', 'PG', 'PG-13', 'R', 'NC-17'", rating.Detail);
    }

    // ---- what has to be created ----------------------------------------------------------------------

    /// <summary>
    /// A policy, an enum, a domain and a composite, created in their own schema and read back. The
    /// interesting assertions are the ones a fixture cannot make: that <c>pg_get_expr</c> renders the
    /// predicate, that <c>polcmd</c> maps to the verb the DDL used, and that permissive and restrictive come
    /// back distinguishable.
    /// </summary>
    [SkippableFact]
    public async Task Policies_and_types_come_back_as_they_were_declared()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        var reader = provider.CreateMetadataReader(factory);

        await Setup(exec);
        try
        {
            var kinds = await reader.GetDatabaseObjectsAsync(CancellationToken.None);

            // ---- types
            var state = kinds.Types.Single(t => t.Schema == Schema && t.Name == "ticket_state");
            Assert.Equal(TypeKind.Enum, state.Kind);
            Assert.Equal("'open', 'closed'", state.Detail);

            var amount = kinds.Types.Single(t => t.Schema == Schema && t.Name == "positive_amount");
            Assert.Equal(TypeKind.Domain, amount.Kind);
            Assert.Contains("numeric", amount.Detail);
            Assert.Contains("CHECK", amount.Detail);   // pg_get_constraintdef, not reassembled here

            var address = kinds.Types.Single(t => t.Schema == Schema && t.Name == "address");
            Assert.Equal(TypeKind.Composite, address.Kind);
            Assert.Contains("line1 text", address.Detail);

            // ---- policies
            var ours = kinds.Policies.Where(p => p.Name.StartsWith("ticket_")).ToList();
            Assert.Equal(2, ours.Count);

            var visible = ours.Single(p => p.Name == "ticket_visible");
            Assert.Equal("SELECT", visible.Command);
            Assert.True(visible.Permissive);
            Assert.NotNull(visible.Using);
            Assert.Contains("state", visible.Using);   // rendered by pg_get_expr
            Assert.Null(visible.WithCheck);
            Assert.Contains("public", visible.Roles);  // TO PUBLIC, not an empty role list

            // Restrictive is the distinction that decides whether a policy can only widen or only narrow
            // access, and it is one boolean away from being reported wrong.
            var noDelete = ours.Single(p => p.Name == "ticket_no_reopen");
            Assert.False(noDelete.Permissive);
            Assert.Equal("UPDATE", noDelete.Command);
            Assert.NotNull(noDelete.WithCheck);
        }
        finally
        {
            await Teardown(exec);
        }
    }

    /// <summary>
    /// The same policies, read through the <em>per-table</em> path this time — the placement that puts them
    /// beside the table's constraints and triggers. Both readings have to agree, or the tree contradicts
    /// itself about the same table.
    /// </summary>
    [SkippableFact]
    public async Task A_table_s_own_policies_come_back_with_its_other_details()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        var reader = provider.CreateMetadataReader(factory);

        await Setup(exec);
        try
        {
            var snapshot = await reader.LoadSnapshotAsync("pagila", CancellationToken.None);
            var ticket = snapshot.ResolveTable(Schema, "ticket");
            Assert.NotNull(ticket);

            var details = await reader.GetTableDetailsAsync(ticket!.Id, CancellationToken.None);
            Assert.Equal(2, details.Policies.Count);
            Assert.All(details.Policies, p => Assert.Equal(ticket.Id, p.TableId));

            // …and a table with no policies gets none rather than the whole database's.
            var film = snapshot.ResolveTable("public", "film")!;
            Assert.Empty((await reader.GetTableDetailsAsync(film.Id, CancellationToken.None)).Policies);
        }
        finally
        {
            await Teardown(exec);
        }
    }

    [SkippableFact]
    public async Task A_sequence_never_read_from_reports_no_last_value()
    {
        // Null, not zero: rendering it as 0 would claim the first id has already been handed out. A real
        // state, and only a live server produces it.
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        await Setup(exec);
        try
        {
            var kinds = await provider.CreateMetadataReader(factory)
                .GetDatabaseObjectsAsync(CancellationToken.None);

            var fresh = kinds.Sequences.Single(q => q.Schema == Schema && q.Name == "untouched_seq");
            Assert.Null(fresh.LastValue);
            Assert.Null(fresh.OwnedBy);
            Assert.True(fresh.Cycles);
            Assert.Equal(5, fresh.Increment);

            // The other half of the same query: a sequence behind a `serial` column, which is the route from
            // a sequence back to what it feeds. This is the assertion pagila cannot make (see
            // Sequences_come_back_with_their_type_and_position) — so the fixture creates the shape itself.
            var owned = kinds.Sequences.Single(q => q.Schema == Schema && q.Name == "ticket_id_seq");
            Assert.Equal($"{Schema}.ticket.id", owned.OwnedBy);
        }
        finally
        {
            await Teardown(exec);
        }
    }

    // ---- fixture ------------------------------------------------------------------------------------

    private static async Task Setup(IQueryExecutor exec)
    {
        var results = await exec.ExecuteAsync(
            $"""
             drop schema if exists {Schema} cascade;
             create schema {Schema};
             create type {Schema}.ticket_state as enum ('open', 'closed');
             create domain {Schema}.positive_amount as numeric(10,2) check (value > 0);
             create type {Schema}.address as (line1 text, city text);
             create sequence {Schema}.untouched_seq increment by 5 cycle maxvalue 100;
             create table {Schema}.ticket (
                 id serial primary key,
                 state {Schema}.ticket_state not null default 'open'
             );
             alter table {Schema}.ticket enable row level security;
             create policy ticket_visible on {Schema}.ticket
                 for select to public using (state = 'open');
             create policy ticket_no_reopen on {Schema}.ticket
                 as restrictive for update with check (state = 'closed');
             """,
            new QueryOptions(), CancellationToken.None);

        var failed = results.FirstOrDefault(r => !r.Success);
        Assert.True(failed is null, failed?.Error?.Message);
    }

    private static Task Teardown(IQueryExecutor exec)
        => exec.ExecuteAsync($"drop schema if exists {Schema} cascade;", new QueryOptions(), CancellationToken.None);
}
