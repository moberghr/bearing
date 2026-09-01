using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The per-table catalog reads behind the schema tree's folders (#46): constraints from <c>pg_constraint</c>,
/// indexes from <c>pg_index</c>, triggers from <c>pg_trigger</c>.
/// <para>
/// Against a live server on purpose. This is the layer §4.6 says must <b>not</b> be checked against the demo
/// fixtures: those encode what we believe Postgres reports, and that belief is what is under test here.
/// Skips cleanly with no server (§4.2).
/// </para>
/// <para>
/// The fixture builds its own schema rather than leaning on pagila, following <c>PostgresWriteTests</c>. An
/// expression index, a partial index, a disabled trigger, a self-referencing key and a view are each a branch
/// of these queries, and each is something a real database may or may not happen to contain — so the fixture
/// creates one of each instead of hoping.
/// </para>
/// </summary>
public class PostgresTableDetailTests : IAsyncLifetime
{
    private readonly string _schema = "bearing_detail_" + Guid.NewGuid().ToString("N")[..8];
    private IDbConnectionFactory _factory = null!;
    private IMetadataReader _reader = null!;
    private IQueryExecutor _exec = null!;
    private ISchemaSnapshot _snapshot = null!;
    private string? _unreachable;

    public async Task InitializeAsync()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        _factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        _reader = provider.CreateMetadataReader(_factory);
        _exec = provider.CreateQueryExecutor(_factory);

        // Skipping from InitializeAsync fails the test rather than skipping it, so the reason is recorded
        // here and each test skips on it — reported, not collapsed to a bool, for the same reason §4.2 gives.
        _unreachable = await PgTestServer.UnreachableReasonAsync(_factory, CancellationToken.None);
        if (_unreachable is not null) return;

        await RunAsync($"""
            create schema {_schema};
            create table {_schema}.store (
              id int primary key,
              name text not null unique,
              constraint store_name_not_blank check (btrim(name) <> '')
            );
            create table {_schema}.payment (
              id int primary key,
              store_id int references {_schema}.store(id),
              amount numeric not null constraint payment_amount_positive check (amount > 0),
              note text
            );
            create index payment_store_id_idx on {_schema}.payment (store_id);
            create index payment_note_lower_idx on {_schema}.payment (lower(note));
            create index payment_partial_idx on {_schema}.payment (id) where note is not null;
            create unique index payment_note_covering_idx on {_schema}.payment (note) include (amount);
            create table {_schema}.node (id int primary key, parent_id int references {_schema}.node(id));
            -- store.id is referenced by payment and by these two, so three foreign keys point at its
            -- primary-key index: an unrestricted conindid join reports that index four times.
            create table {_schema}.shift (id int primary key, store_id int references {_schema}.store(id));
            create table {_schema}.stock (id int primary key, store_id int references {_schema}.store(id));
            -- A hand-made unique index supporting a foreign key. Postgres only requires a unique *index*,
            -- not a constraint, so nothing owns this one — and generated DDL must keep it, or the key that
            -- depends on it can no longer be created.
            create table {_schema}.till (code text, name text);
            create unique index till_code_key on {_schema}.till (code);
            create table {_schema}.till_use (id int primary key,
              code text references {_schema}.till(code));
            create view {_schema}.receipt as
              select p.id as payment_id, s.name as store_name
              from {_schema}.payment p join {_schema}.store s on s.id = p.store_id;
            create function {_schema}.touch() returns trigger language plpgsql as $$ begin return new; end $$;
            create trigger payment_touch before update on {_schema}.payment
              for each row execute function {_schema}.touch();
            create trigger payment_audit after insert or update of note on {_schema}.payment
              for each row execute function {_schema}.touch();
            alter table {_schema}.payment disable trigger payment_audit;
            """);

        _snapshot = await _reader.LoadSnapshotAsync(PgTestServer.Database, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_unreachable is null)
        {
            try { await RunAsync($"drop schema if exists {_schema} cascade;"); } catch { /* best-effort */ }
        }
        await _factory.DisposeAsync();
    }

    private Task RunAsync(string sql) => _exec.ExecuteAsync(sql, new QueryOptions(), CancellationToken.None);

    /// <summary>Skips with the reason the probe reported, the way <c>PgTestServer.RequireAsync</c> does.</summary>
    private void RequireServer()
        => Skip.If(_unreachable is not null,
            $"No PostgreSQL reachable for integration test at {PgTestServer.Endpoint} — "
            + $"{_unreachable?.TrimEnd('.', ' ')}. "
            + "Set BEARING_TEST_PG_{HOST,PORT,DB,USER,PASSWORD} to point at your server.");

    private long TableId(string name) => _snapshot.ResolveTable(_schema, name)!.Id;

    private Task<TableDetails> DetailsOf(string name)
        => _reader.GetTableDetailsAsync(TableId(name), CancellationToken.None);

    private string ColumnsOf(string table, IReadOnlyList<int> ordinals)
        => string.Join(", ", _snapshot.ColumnsOf(TableId(table))
            .Where(c => ordinals.Contains(c.Ordinal))
            .OrderBy(c => ordinals.ToList().IndexOf(c.Ordinal))
            .Select(c => c.Name));

    // ---- constraints ----------------------------------------------------------------------------

    [SkippableFact]
    public async Task Reads_every_kind_of_constraint_with_the_servers_own_definition()
    {
        RequireServer();

        var payment = await DetailsOf("payment");

        var pk = Assert.Single(payment.Constraints, c => c.Kind == ConstraintKind.PrimaryKey);
        Assert.Equal("PRIMARY KEY (id)", pk.Definition);
        var fk = Assert.Single(payment.Constraints, c => c.Kind == ConstraintKind.ForeignKey);
        Assert.Contains("REFERENCES", fk.Definition);
        var check = Assert.Single(payment.Constraints, c => c.Kind == ConstraintKind.Check);
        Assert.Equal("payment_amount_positive", check.Name);
        Assert.Contains("amount > 0", check.Definition);

        var store = await DetailsOf("store");
        Assert.Single(store.Constraints, c => c.Kind == ConstraintKind.Unique);
    }

    [SkippableFact]
    public async Task Constraint_ordinals_name_the_columns_the_snapshot_knows()
    {
        // The two sides of this mapping — conkey and the snapshot's column ordinals — are only both real
        // against a server. Get it wrong and every label in the tree names the wrong column.
        RequireServer();

        var payment = await DetailsOf("payment");

        Assert.Equal("id", ColumnsOf("payment", Single(payment, ConstraintKind.PrimaryKey).Ordinals));
        Assert.Equal("store_id", ColumnsOf("payment", Single(payment, ConstraintKind.ForeignKey).Ordinals));
        Assert.Equal("amount", ColumnsOf("payment", Single(payment, ConstraintKind.Check).Ordinals));

        static ConstraintInfo Single(TableDetails details, ConstraintKind kind)
            => details.Constraints.Single(c => c.Kind == kind);
    }

    // ---- indexes --------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Reads_indexes_with_their_flags_and_definitions()
    {
        RequireServer();

        var indexes = (await DetailsOf("payment")).Indexes;

        var primary = Assert.Single(indexes, i => i.IsPrimary);
        Assert.True(primary.IsUnique, "the index behind a primary key is unique");
        Assert.True(primary.IsValid);
        Assert.StartsWith("CREATE UNIQUE INDEX", primary.Definition);
        // The primary key comes first, so a tree does not open onto three lookup indexes above the key.
        Assert.Same(primary, indexes[0]);

        var partial = indexes.Single(i => i.Name == "payment_partial_idx");
        Assert.Contains("WHERE", partial.Definition);
        Assert.Equal("id", ColumnsOf("payment", partial.Ordinals));
    }

    [SkippableFact]
    public async Task Index_ordinals_come_back_as_a_one_based_array()
    {
        // The trap this test exists for: indkey is an int2vector, and casting one to an array keeps its
        // ZERO-based bounds — `[0:0]={1}` — which a client cannot read as an int[] at all. The query rebuilds
        // it with array_agg. A silent regression here would resolve every index to the wrong column.
        RequireServer();

        var index = (await DetailsOf("payment")).Indexes.Single(i => i.Name == "payment_store_id_idx");

        Assert.Equal([2], index.Ordinals);
        Assert.Equal("store_id", ColumnsOf("payment", index.Ordinals));
    }

    [SkippableFact]
    public async Task An_expression_index_reports_no_ordinals_at_all()
    {
        // indkey holds a 0 per expression key. Keeping it would point at no column and resolve to the wrong
        // name, so those are dropped — leaving the definition as the only thing that says what it covers.
        RequireServer();

        var index = (await DetailsOf("payment")).Indexes.Single(i => i.Name == "payment_note_lower_idx");

        Assert.Empty(index.Ordinals);
        Assert.Contains("lower(note)", index.Definition);
    }

    // ---- triggers -------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Reads_triggers_and_whether_each_is_enabled()
    {
        RequireServer();

        var triggers = (await DetailsOf("payment")).Triggers;

        Assert.Equal(["payment_audit", "payment_touch"], triggers.Select(t => t.Name));
        // A disabled trigger is indistinguishable from an enabled one everywhere else in the catalog.
        Assert.False(triggers.Single(t => t.Name == "payment_audit").Enabled);
        Assert.True(triggers.Single(t => t.Name == "payment_touch").Enabled);
        Assert.Contains("BEFORE UPDATE", triggers.Single(t => t.Name == "payment_touch").Definition);
    }

    [SkippableFact]
    public async Task The_triggers_enforcing_foreign_keys_are_left_out()
    {
        // Postgres implements every foreign key as internal triggers, three per key. Listing them buries the
        // user's own triggers under machinery they did not write and cannot edit — node has a self-referencing
        // key and no triggers of its own, so it must report none.
        RequireServer();

        var node = await DetailsOf("node");

        Assert.Empty(node.Triggers);
        Assert.Single(node.Constraints, c => c.Kind == ConstraintKind.ForeignKey);
    }

    [SkippableFact]
    public async Task A_not_null_column_does_not_arrive_as_a_constraint()
    {
        // PostgreSQL 18 stores every NOT NULL as a real pg_constraint row (contype 'n'). Left in, a
        // four-column table reports four of them — one node per column, saying what the column row already says, and
        // inflating the folder's count with information already on screen.
        RequireServer();

        var payment = await DetailsOf("payment");

        Assert.All(payment.Constraints, c => Assert.DoesNotContain("not_null", c.Name));
        Assert.All(payment.Constraints, c =>
            Assert.True(c.Kind is not ConstraintKind.Other, $"{c.Name} came through as an unmapped kind"));
        // payment declares exactly three: its key, its foreign key and its check.
        Assert.Equal(3, payment.Constraints.Count);
    }

    [SkippableFact]
    public async Task An_index_reports_its_key_columns_and_not_its_include_payload()
    {
        // indkey spans indnatts, so it carries INCLUDE columns too — but the planner cannot search on those.
        // A row that lists them reads as a two-column key, which is the one thing an index row must not
        // misstate, since it exists to answer "will this serve my predicate".
        RequireServer();

        var index = (await DetailsOf("payment")).Indexes.Single(i => i.Name == "payment_note_covering_idx");

        Assert.Equal("note", ColumnsOf("payment", index.Ordinals));
        Assert.Contains("INCLUDE", index.Definition);
    }

    [SkippableFact]
    public async Task An_index_says_whether_a_constraint_owns_it()
    {
        // What generated DDL has to skip: an index its constraint creates cannot be issued separately, and
        // the name is already taken by then.
        RequireServer();

        var indexes = (await DetailsOf("payment")).Indexes;

        Assert.True(indexes.Single(i => i.Name == "payment_pkey").BackedByConstraint);
        Assert.False(indexes.Single(i => i.Name == "payment_store_id_idx").BackedByConstraint);
        // A unique index created by hand is nobody's constraint, however unique it is.
        Assert.False(indexes.Single(i => i.Name == "payment_note_covering_idx").BackedByConstraint);
    }

    [SkippableFact]
    public async Task An_index_is_listed_once_however_many_keys_reference_it()
    {
        // A FOREIGN KEY sets conindid too, pointing at the *referenced* table's index — so joining on
        // conindid alone reported store's primary-key index once per key referencing store, inflating both
        // the Indexes folder and its count.
        RequireServer();

        var indexes = (await DetailsOf("store")).Indexes;

        Assert.Equal(indexes.Select(i => i.Name).Distinct(), indexes.Select(i => i.Name));
        Assert.Single(indexes, i => i.Name == "store_pkey");
    }

    [SkippableFact]
    public async Task A_hand_made_unique_index_supporting_a_key_is_not_reported_as_owned()
    {
        // Postgres requires only a unique index behind a foreign key, not a constraint. Marking this one as
        // constraint-owned dropped it from generated DDL — and the key depending on it could then no longer
        // be created, which is a broken schema rather than a cosmetic omission.
        RequireServer();

        var index = (await DetailsOf("till")).Indexes.Single(i => i.Name == "till_code_key");

        Assert.False(index.BackedByConstraint);
        Assert.True(index.IsUnique);

        var table = _snapshot.ResolveTable(_schema, "till")!;
        var ddl = Bearing.Sql.TableDdlGenerator.CreateTable(table, _snapshot, await DetailsOf("till"));
        Assert.Contains("till_code_key", ddl);
    }

    // ---- sizes (#76) ----------------------------------------------------------------------------

    [SkippableFact]
    public async Task Reads_every_relations_size_in_one_pass()
    {
        // One query for the whole database, not one per table: pg_total_relation_size stats files, so a round
        // trip per relation would be strictly worse — and the tree wants them all at once to sort by.
        RequireServer();

        var sizes = await _reader.GetRelationSizesAsync(CancellationToken.None);

        var payment = sizes.Single(x => x.TableId == TableId("payment"));
        Assert.True(payment.TotalBytes > 0, "an existing table reported no size at all");
        // The split has to add up the way Postgres defines it: total is heap+toast+indexes, and table is
        // heap+toast, so indexes is the difference.
        Assert.Equal(payment.TotalBytes, payment.TableBytes + payment.IndexBytes);
        Assert.True(payment.IndexBytes > 0, "payment has four indexes and reported none");
    }

    [SkippableFact]
    public async Task A_view_has_no_size_of_its_own()
    {
        // Views have no storage, so they are simply absent from the read rather than reported as zero.
        RequireServer();

        var sizes = await _reader.GetRelationSizesAsync(CancellationToken.None);

        Assert.DoesNotContain(TableId("receipt"), sizes.Select(x => x.TableId));
    }

    [SkippableFact]
    public async Task A_never_analysed_table_reports_no_row_count()
    {
        // reltuples is -1 until ANALYZE runs. Minus one is not a row count, and must not reach a label as
        // one — the fixture's tables have just been created and never analysed.
        RequireServer();

        var sizes = await _reader.GetRelationSizesAsync(CancellationToken.None);

        var payment = sizes.Single(x => x.TableId == TableId("payment"));
        Assert.Null(payment.EstimatedRows);

        await RunAsync($"analyze {_schema}.payment;");
        var analysed = (await _reader.GetRelationSizesAsync(CancellationToken.None))
            .Single(x => x.TableId == TableId("payment"));
        Assert.NotNull(analysed.EstimatedRows);
        Assert.True(analysed.EstimatedRows >= 0);
    }

    [SkippableFact]
    public async Task An_index_carries_its_own_size()
    {
        // The other half of "is this index worth keeping".
        RequireServer();

        var index = (await DetailsOf("payment")).Indexes.Single(i => i.Name == "payment_store_id_idx");

        Assert.NotNull(index.SizeBytes);
        Assert.True(index.SizeBytes >= 0);
    }

    [SkippableFact]
    public async Task Database_sizes_come_back_for_the_ones_the_user_can_reach()
    {
        // pg_database_size raises for a database the caller cannot connect to, so it is only called where
        // has_database_privilege says it will work — an unreadable size is null, and one inaccessible
        // database must not cost the sizes of the rest.
        RequireServer();

        var sizes = await _reader.GetDatabaseSizesAsync(CancellationToken.None);

        Assert.NotEmpty(sizes);
        var own = sizes.Single(d => d.Database == PgTestServer.Database);
        Assert.NotNull(own.Bytes);
        Assert.True(own.Bytes > 0);
        Assert.All(sizes, d => Assert.False(d.Bytes < 0));
    }

    // ---- the edges ------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_view_has_no_constraints_indexes_or_triggers_of_its_own()
    {
        RequireServer();

        var receipt = await DetailsOf("receipt");

        Assert.Empty(receipt.Constraints);
        Assert.Empty(receipt.Indexes);
        Assert.Empty(receipt.Triggers);
    }

    [SkippableFact]
    public async Task An_unknown_relation_reads_as_empty_rather_than_throwing()
    {
        // A tree outlives what it shows: expanding a table another session has just dropped must put empty
        // folders there, not an exception.
        RequireServer();

        var details = await _reader.GetTableDetailsAsync(999_999_999, CancellationToken.None);

        Assert.Empty(details.Constraints);
        Assert.Empty(details.Indexes);
        Assert.Empty(details.Triggers);
    }

    [SkippableFact]
    public async Task The_generated_ddl_carries_the_constraints_and_indexes_it_used_to_omit()
    {
        // The generator's own note admitted the hole: "indexes, defaults, checks are omitted" (#46).
        RequireServer();
        var table = _snapshot.ResolveTable(_schema, "payment")!;

        var ddl = Bearing.Sql.TableDdlGenerator.CreateTable(table, _snapshot, await DetailsOf("payment"));

        Assert.Contains("primary key", ddl);
        Assert.Contains("payment_amount_positive", ddl);
        Assert.Contains("CREATE INDEX payment_store_id_idx", ddl);
        Assert.Contains("CREATE INDEX payment_note_lower_idx", ddl);
        // An index a constraint owns is not re-issued — running that DDL would fail on the name.
        Assert.DoesNotContain("INDEX payment_pkey", ddl);
        // …but a hand-made unique index is not owned by anything, and dropping it would lose a real index.
        Assert.Contains("payment_note_covering_idx", ddl);
    }
}
