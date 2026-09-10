using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The rest of #119's catalog reads against a real server: column defaults and identity, comments, rules,
/// partitions, and the long-tail kinds. Live for §4.6's reason and §4.7's — a fixture asserts our belief
/// about the catalog, not the catalog.
/// </summary>
public class PostgresBreadthTests
{
    private static ConnectionInfo Info() => PgTestServer.Info();
    private static string Password => PgTestServer.Password;

    private const string Schema = "bearing_breadth_test";

    // ---- against pagila as it ships -------------------------------------------------------------------

    [SkippableFact]
    public async Task A_serial_column_reports_the_nextval_that_reaches_its_sequence()
    {
        // The half of #119 that was missing: SequenceInfo.OwnedBy gives sequence → column, and this is the
        // route back. It works on this pagila even though the sequences have no OWNED BY (§4.7), because the
        // default expression is on the column either way.
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var reader = provider.CreateMetadataReader(factory);
        var snapshot = await reader.LoadSnapshotAsync("pagila", CancellationToken.None);
        var film = snapshot.ResolveTable("public", "film")!;

        var details = await reader.GetTableDetailsAsync(film.Id, CancellationToken.None);
        var id = details.Columns.Single(c => c.Ordinal == 1);

        Assert.Contains("nextval", id.Default);
        Assert.Contains("film_film_id_seq", id.Default);
        Assert.Equal(ColumnIdentity.None, id.Identity);
        Assert.Null(id.GeneratedExpression);
    }

    [SkippableFact]
    public async Task A_column_with_no_default_reports_none_rather_than_an_empty_string()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var reader = provider.CreateMetadataReader(factory);
        var snapshot = await reader.LoadSnapshotAsync("pagila", CancellationToken.None);
        var film = snapshot.ResolveTable("public", "film")!;

        var details = await reader.GetTableDetailsAsync(film.Id, CancellationToken.None);
        var title = details.Columns.Single(c => c.Ordinal == 2);

        Assert.Null(title.Default);
        // Every text column has a collation; only a non-default one is reported, or the interesting case
        // would be buried under every other column.
        Assert.Null(title.Collation);
    }

    [SkippableFact]
    public async Task A_view_reports_no_rules_even_though_its_definition_is_one()
    {
        // A view *is* a _RETURN rule. Reporting it would make every view in the database look like it had a
        // rule, which is the one thing this filter exists to prevent.
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var reader = provider.CreateMetadataReader(factory);
        var snapshot = await reader.LoadSnapshotAsync("pagila", CancellationToken.None);
        var view = snapshot.Tables.FirstOrDefault(t => t.Kind == RelationKind.View);
        Skip.If(view is null, "this pagila has no views");

        var details = await reader.GetTableDetailsAsync(view!.Id, CancellationToken.None);
        Assert.Empty(details.Rules);
    }

    /// <summary>
    /// pagila partitions <c>payment</c> by month, which makes it the best possible fixture for this: a real
    /// parent with real children, in a database nobody wrote for this test.
    /// <para>
    /// The premise was checked before being asserted (§4.7) — the first version of this test claimed pagila
    /// had no partitions at all, and it has seven.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Pagilas_partitioned_payment_table_reports_its_children()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var reader = provider.CreateMetadataReader(factory);
        var snapshot = await reader.LoadSnapshotAsync("pagila", CancellationToken.None);

        var payment = snapshot.ResolveTable("public", "payment");
        Skip.If(payment is null, "this pagila has no payment table");
        Assert.Null(payment!.PartitionOf);

        var partitions = snapshot.Tables.Where(t => t.PartitionOf == payment.Id).ToList();
        Assert.NotEmpty(partitions);
        Assert.All(partitions, p => Assert.StartsWith("payment_p", p.Name));

        // An unpartitioned table reports no parent, which is what keeps the tree from nesting everything.
        Assert.Null(snapshot.ResolveTable("public", "film")!.PartitionOf);

        // A partition's own indexes are relations too, but relkind filters them out — so nothing but tables
        // ever appears with a parent.
        Assert.All(partitions, p => Assert.Equal(RelationKind.Table, p.Kind));

        // pg_default exists on every cluster, so it is the one tablespace always there to assert on.
        var tablespaces = await reader.GetTablespacesAsync(CancellationToken.None);
        Assert.Contains(tablespaces, t => t.Name == "pg_default");
    }

    /// <summary>
    /// A relation with two parents is reported once, and as a partition of neither.
    /// <para>
    /// Classic multiple inheritance (<c>CREATE TABLE c () INHERITS (a, b)</c>) puts two rows in
    /// <c>pg_inherits</c> for the same child. The first version read the parent with a <c>left join</c>,
    /// which fanned out and emitted the relation <b>twice</b> — reaching the tree as two identical rows, the
    /// completion engine as two tables, and the go-to-table picker as two entries. It is not a partition of
    /// anything in particular either, so it reports no parent rather than an arbitrary one.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_relation_with_two_parents_is_read_once_and_nested_under_neither()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        var reader = provider.CreateMetadataReader(factory);

        await exec.ExecuteAsync(
            $"""
             drop schema if exists {Schema}_inherit cascade;
             create schema {Schema}_inherit;
             create table {Schema}_inherit.a (x int);
             create table {Schema}_inherit.b (y int);
             create table {Schema}_inherit.c () inherits ({Schema}_inherit.a, {Schema}_inherit.b);
             create table {Schema}_inherit.d () inherits ({Schema}_inherit.a);
             """,
            new QueryOptions(), CancellationToken.None);
        try
        {
            var snapshot = await reader.LoadSnapshotAsync(PgTestServer.Database, CancellationToken.None);
            var ours = snapshot.Tables.Where(t => t.Schema == $"{Schema}_inherit").ToList();

            // Four relations, four rows. The duplicate was the bug.
            Assert.Equal(4, ours.Count);
            Assert.Equal(4, ours.Select(t => t.Id).Distinct().Count());

            // Two parents → none reported.
            Assert.Null(ours.Single(t => t.Name == "c").PartitionOf);
            // One parent → reported, so the fix did not simply stop reading parents.
            var a = ours.Single(t => t.Name == "a");
            Assert.Equal(a.Id, ours.Single(t => t.Name == "d").PartitionOf);
        }
        finally
        {
            await exec.ExecuteAsync($"drop schema if exists {Schema}_inherit cascade;",
                new QueryOptions(), CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task The_long_tail_reads_run_and_report_nothing_where_there_is_nothing()
    {
        // The honest baseline: pagila has none of these, and the point is that the queries execute rather
        // than being swallowed by Best() into an empty list that looks identical (§4.7).
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var kinds = await provider.CreateMetadataReader(factory).GetDatabaseObjectsAsync(CancellationToken.None);

        Assert.Empty(kinds.Publications);
        Assert.Empty(kinds.EventTriggers);
        // …while the kinds pagila *does* have still came back on the same call, which is what proves the
        // twelve reads are independent rather than one failing read emptying the lot.
        Assert.NotEmpty(kinds.Sequences);
        Assert.NotEmpty(kinds.Extensions);
    }

    // ---- against objects created here -----------------------------------------------------------------

    /// <summary>
    /// Identity, generated, collation, comments, a rule and a partitioned table — read back as declared.
    /// </summary>
    [SkippableFact]
    public async Task Everything_created_here_comes_back_as_it_was_declared()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        var reader = provider.CreateMetadataReader(factory);

        await Setup(exec);
        try
        {
            var snapshot = await reader.LoadSnapshotAsync(PgTestServer.Database, CancellationToken.None);

            // ---- partitions
            var parent = snapshot.ResolveTable(Schema, "event")!;
            Assert.Equal(RelationKind.Partitioned, parent.Kind);
            Assert.Null(parent.PartitionOf);

            var partitions = snapshot.Tables.Where(t => t.PartitionOf == parent.Id).ToList();
            Assert.Equal(2, partitions.Count);
            Assert.Equal(["event_2026", "event_2027"], partitions.Select(t => t.Name).Order());

            // ---- column extras
            var ticket = snapshot.ResolveTable(Schema, "ticket")!;
            var details = await reader.GetTableDetailsAsync(ticket.Id, CancellationToken.None);

            var byName = snapshot.ColumnsOf(ticket.Id).ToDictionary(c => c.Name, c => c.Ordinal);

            var id = details.Columns.Single(c => c.Ordinal == byName["id"]);
            Assert.Equal(ColumnIdentity.Always, id.Identity);

            var opened = details.Columns.Single(c => c.Ordinal == byName["opened_at"]);
            Assert.Contains("now()", opened.Default);

            // A generated column reports its expression and *not* a default: both arrive in the same adbin,
            // and a default it can never use would be a lie about how the column is written.
            var slug = details.Columns.Single(c => c.Ordinal == byName["slug"]);
            Assert.NotNull(slug.GeneratedExpression);
            Assert.Contains("lower", slug.GeneratedExpression);
            Assert.Null(slug.Default);

            var note = details.Columns.Single(c => c.Ordinal == byName["note"]);
            Assert.Equal("C", note.Collation);
            Assert.Equal("free text, not searched", note.Comment);

            // ---- comments
            Assert.Equal("one row per support ticket", details.Comment);

            // ---- rules
            var rule = Assert.Single(details.Rules);
            Assert.Equal("ticket_no_delete", rule.Name);
            Assert.Contains("DO INSTEAD NOTHING", rule.Definition);

            // ---- the long tail
            var kinds = await reader.GetDatabaseObjectsAsync(CancellationToken.None);
            Assert.Contains(kinds.Collations, c => c.Name == "case_insensitive" && c.Schema == Schema);
            Assert.Contains(kinds.Publications, p => p.Name == "bearing_test_pub");
            Assert.Contains(kinds.EventTriggers, e => e.Name == "bearing_test_evt");
            Assert.Contains(kinds.TextSearchConfigs, t => t.Name == "bearing_test_ts");
        }
        finally
        {
            await Teardown(exec);
        }
    }

    [SkippableFact]
    public async Task A_publication_says_how_much_of_the_database_it_covers()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        await Setup(exec);
        try
        {
            var kinds = await provider.CreateMetadataReader(factory)
                .GetDatabaseObjectsAsync(CancellationToken.None);

            var publication = kinds.Publications.Single(p => p.Name == "bearing_test_pub");
            Assert.Contains("1 tables", publication.Detail);
            Assert.Contains("insert", publication.Detail);
            // Declared FOR TABLE with insert only, so the other three operations must not be claimed.
            Assert.DoesNotContain("truncate", publication.Detail);
        }
        finally
        {
            await Teardown(exec);
        }
    }

    [SkippableFact]
    public async Task A_nondeterministic_collation_is_reported_as_one()
    {
        // The property that makes a collation behave surprisingly in a comparison, so it is the one worth
        // having on the row.
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        await Setup(exec);
        try
        {
            var kinds = await provider.CreateMetadataReader(factory)
                .GetDatabaseObjectsAsync(CancellationToken.None);

            var collation = kinds.Collations.Single(c => c.Name == "case_insensitive");
            Assert.Contains("nondeterministic", collation.Detail);
            Assert.Contains("icu", collation.Detail);
        }
        finally
        {
            await Teardown(exec);
        }
    }

    /// <summary>
    /// A relation dropped while the size read is running does not take the other relations' sizes with it.
    /// <para>
    /// <c>pg_class</c> is read under the query's MVCC snapshot, but <c>pg_total_relation_size</c> stats
    /// files — which is not — so a concurrent <c>DROP</c> leaves a row listed with a null size. The reader
    /// called <c>GetInt64</c> on it and threw <c>InvalidCastException</c>, losing every size in the
    /// database to one other person's DDL. CI found it: this suite's own schema-creating classes race the
    /// enumerating ones, and the runner lost a race this machine kept winning.
    /// </para>
    /// <para>
    /// Reproduced by asking for a size <em>while</em> a relation is being dropped, repeatedly — the window
    /// is small, so one attempt would prove nothing either way. The assertion is that nothing throws and the
    /// surviving relations still report, not that the doomed one is absent: whether it is caught depends on
    /// the timing this test cannot control.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_relation_dropped_mid_read_does_not_lose_the_other_sizes()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        var reader = provider.CreateMetadataReader(factory);
        const string churn = Schema + "_churn";

        await exec.ExecuteAsync($"drop schema if exists {churn} cascade; create schema {churn};",
            new QueryOptions(), CancellationToken.None);
        try
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                // Enough tables that the size read is still walking the list when the drop lands.
                var create = string.Join(" ", Enumerable.Range(0, 25)
                    .Select(i => $"create table {churn}.t{i} (x int);"));
                await exec.ExecuteAsync(create, new QueryOptions(), CancellationToken.None);

                var dropping = exec.ExecuteAsync(
                    $"drop schema {churn} cascade; create schema {churn};",
                    new QueryOptions(), CancellationToken.None);
                var sizing = reader.GetRelationSizesAsync(CancellationToken.None);

                await Task.WhenAll(dropping, sizing);

                // pagila's own tables are never dropped here, so a working read always finds them.
                Assert.NotEmpty(sizing.Result);
                Assert.Contains(sizing.Result, size => size.TotalBytes > 0);
            }
        }
        finally
        {
            await exec.ExecuteAsync($"drop schema if exists {churn} cascade;",
                new QueryOptions(), CancellationToken.None);
        }
    }

    // ---- fixture ------------------------------------------------------------------------------------

    private static async Task Setup(IQueryExecutor exec)
    {
        await Teardown(exec);
        var results = await exec.ExecuteAsync(
            $"""
             create schema {Schema};
             create collation {Schema}.case_insensitive
                 (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
             create table {Schema}.ticket (
                 id        integer generated always as identity primary key,
                 opened_at timestamptz not null default now(),
                 title     text not null,
                 slug      text generated always as (lower(title)) stored,
                 note      text collate "C"
             );
             comment on table {Schema}.ticket is 'one row per support ticket';
             comment on column {Schema}.ticket.note is 'free text, not searched';
             create rule ticket_no_delete as on delete to {Schema}.ticket do instead nothing;
             create table {Schema}.event (at timestamptz not null, what text)
                 partition by range (at);
             create table {Schema}.event_2026 partition of {Schema}.event
                 for values from ('2026-01-01') to ('2027-01-01');
             create table {Schema}.event_2027 partition of {Schema}.event
                 for values from ('2027-01-01') to ('2028-01-01');
             create publication bearing_test_pub for table {Schema}.ticket with (publish = 'insert');
             create function {Schema}.noop() returns event_trigger language plpgsql as $$ begin end $$;
             create event trigger bearing_test_evt on ddl_command_end
                 execute function {Schema}.noop();
             create text search configuration {Schema}.bearing_test_ts (parser = default);
             """,
            new QueryOptions(), CancellationToken.None);

        var failed = results.FirstOrDefault(r => !r.Success);
        Assert.True(failed is null, failed?.Error?.Message);
    }

    /// <summary>
    /// One statement per call: a publication and an event trigger are not schema objects, so
    /// <c>drop schema cascade</c> does not take them, and a failure on one must not abort the rest (§4.7).
    /// </summary>
    private static async Task Teardown(IQueryExecutor exec)
    {
        string[] statements =
        [
            "drop event trigger if exists bearing_test_evt",
            "drop publication if exists bearing_test_pub",
            $"drop schema if exists {Schema} cascade",
        ];
        foreach (var statement in statements)
            await exec.ExecuteAsync(statement, new QueryOptions(), CancellationToken.None);
    }
}
