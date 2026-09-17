using Npgsql;
using Bearing.Core.Data;
using Bearing.Core.Schema;

namespace Bearing.Data.Postgres;

/// <summary>Reads PostgreSQL catalog metadata into an immutable <see cref="SchemaSnapshot"/>.</summary>
public sealed class PostgresMetadataReader : IMetadataReader
{
    private readonly NpgsqlConnectionFactory _factory;

    public PostgresMetadataReader(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct)
    {
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "select datname from pg_database where datistemplate = false order by datname", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var names = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            names.Add(reader.GetString(0));
        return names;
    }

    public async Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct)
    {
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        var searchPath = await ReadSearchPathAsync(conn, ct).ConfigureAwait(false);
        var tables = await ReadTablesAsync(conn, ct).ConfigureAwait(false);
        var columns = await ReadColumnsAsync(conn, ct).ConfigureAwait(false);
        var fks = await ReadForeignKeysAsync(conn, ct).ConfigureAwait(false);

        // Schemas ordered by search_path, then any remaining schemas that actually hold tables.
        var schemas = new List<string>(searchPath);
        foreach (var s in tables.Select(t => t.Schema).Distinct())
            if (!schemas.Contains(s, StringComparer.OrdinalIgnoreCase))
                schemas.Add(s);

        return new SchemaSnapshot(database, schemas, tables, columns, fks, searchPath);
    }

    private static async Task<List<string>> ReadSearchPathAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select s from unnest(current_schemas(false)) s", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<string>();
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>
    /// Every relation, with the parent of a partition (#119 follow-up).
    /// <para>
    /// The parent comes from a scalar subquery rather than a <c>left join pg_inherits</c>, and that is a
    /// correctness fix rather than a style one: <c>pg_inherits</c> has one row per (child, parent) pair, and
    /// classic <c>CREATE TABLE c () INHERITS (a, b)</c> is legal — so the join fanned out and emitted the
    /// same relation twice, which reached the tree as two identical rows, the completion engine as two
    /// tables, and the go-to-table picker as two entries.
    /// </para>
    /// <para>
    /// A relation with <b>more than one</b> parent reports none. It is not a partition of anything in
    /// particular, and picking one of its parents arbitrarily would nest it somewhere the catalog does not
    /// say it belongs.
    /// </para>
    /// <para>
    /// Read here rather than separately because the tree needs the link to <em>arrange</em> the relation
    /// list, which happens before anything is expanded.
    /// </para>
    /// </summary>
    private static async Task<List<TableInfo>> ReadTablesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            select c.oid::bigint, n.nspname, c.relname, c.relkind::text,
                   (select case when count(*) = 1 then min(i.inhparent) end
                    from pg_inherits i where i.inhrelid = c.oid)::bigint as parent
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('r','v','m','f','p')
              and n.nspname not in ('pg_catalog','information_schema')
              and n.nspname not like 'pg\_temp%' and n.nspname not like 'pg\_toast%'
            order by n.nspname, c.relname
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<TableInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = r.GetInt64(0);
            var kind = MapRelKind(r.GetString(3)[0]);
            // pg_inherits covers both declarative partitioning and the older INHERITS, and the tree treats
            // them the same: either way this relation's rows are part of the parent's, which is what makes a
            // hundred sibling rows misleading rather than merely untidy.
            var parent = r.IsDBNull(4) ? (long?)null : r.GetInt64(4);
            list.Add(new TableInfo(id, r.GetString(1), r.GetString(2), kind, parent));
        }
        return list;
    }

    private static async Task<List<ColumnInfo>> ReadColumnsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            select a.attrelid::bigint, a.attnum, a.attname,
                   format_type(a.atttypid, a.atttypmod) as data_type,
                   a.attnotnull,
                   coalesce(pk.is_pk, false) as is_pk
            from pg_attribute a
            join pg_class c on c.oid = a.attrelid
            join pg_namespace n on n.oid = c.relnamespace
            left join (
                select conrelid, unnest(conkey) as attnum, true as is_pk
                from pg_constraint where contype = 'p'
            ) pk on pk.conrelid = a.attrelid and pk.attnum = a.attnum
            where a.attnum > 0 and not a.attisdropped
              and c.relkind in ('r','v','m','f','p')
              and n.nspname not in ('pg_catalog','information_schema')
              and n.nspname not like 'pg\_temp%' and n.nspname not like 'pg\_toast%'
            order by a.attrelid, a.attnum
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ColumnInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ColumnInfo(
                TableId: r.GetInt64(0),
                Ordinal: r.GetInt16(1),
                Name: r.GetString(2),
                DataType: r.GetString(3),
                NotNull: r.GetBoolean(4),
                IsPrimaryKey: r.GetBoolean(5)));
        }
        return list;
    }

    private static async Task<List<ForeignKeyInfo>> ReadForeignKeysAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            select con.oid::bigint, con.conname,
                   con.conrelid::bigint  as parent,
                   con.confrelid::bigint as referenced,
                   con.conkey  as parent_cols,
                   con.confkey as referenced_cols
            from pg_constraint con
            join pg_namespace n on n.oid = con.connamespace
            where con.contype = 'f'
              and n.nspname not in ('pg_catalog','information_schema')
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ForeignKeyInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ForeignKeyInfo(
                Id: r.GetInt64(0),
                Name: r.GetString(1),
                ParentTableId: r.GetInt64(2),
                ParentOrdinals: Array.ConvertAll(r.GetFieldValue<short[]>(4), x => (int)x),
                ReferencedTableId: r.GetInt64(3),
                ReferencedOrdinals: Array.ConvertAll(r.GetFieldValue<short[]>(5), x => (int)x)));
        }
        return list;
    }

    public async Task<IReadOnlyList<RoutineInfo>> GetRoutinesAsync(CancellationToken ct)
    {
        const string sql = """
            select p.oid::bigint, n.nspname, p.proname, p.prokind::text,
                   pg_get_function_arguments(p.oid) as args,
                   pg_get_function_result(p.oid)    as result,
                   obj_description(p.oid, 'pg_proc') as comment
            from pg_proc p
            join pg_namespace n on n.oid = p.pronamespace
            where p.prokind in ('f','p','a','w')
              and n.nspname not in ('pg_catalog','information_schema')
              and n.nspname not like 'pg\_temp%' and n.nspname not like 'pg\_toast%'
            order by n.nspname, p.proname
            """;
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<RoutineInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new RoutineInfo(
                Id: r.GetInt64(0),
                Schema: r.GetString(1),
                Name: r.GetString(2),
                Kind: MapProKind(r.GetString(3)[0]),
                Arguments: r.IsDBNull(4) ? "" : r.GetString(4),
                ReturnType: r.IsDBNull(5) ? "" : r.GetString(5),
                Comment: r.IsDBNull(6) ? null : r.GetString(6)));
        }
        return list;
    }

    /// <summary>
    /// Sequences, user-defined types, extensions and RLS policies (#119) — four catalog reads on one
    /// connection, since the tree wants all of them the moment a database is expanded.
    /// <para>
    /// Every read is best-effort at the <em>kind</em> level: a role without the privileges for one catalog
    /// must still get the other three, so a failure yields an empty list for its own kind rather than
    /// emptying the lot. Failing the whole call would take sequences away from anyone who cannot read
    /// policies, which is a common shape on a locked-down database.
    /// </para>
    /// </summary>
    public async Task<DatabaseObjectKinds> GetDatabaseObjectsAsync(CancellationToken ct)
    {
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return new DatabaseObjectKinds
        {
            Sequences = await Best(() => ReadSequencesAsync(conn, ct)).ConfigureAwait(false),
            Types = await Best(() => ReadTypesAsync(conn, ct)).ConfigureAwait(false),
            Extensions = await Best(() => ReadExtensionsAsync(conn, ct)).ConfigureAwait(false),
            Policies = await Best(() => ReadPoliciesAsync(conn, ct)).ConfigureAwait(false),
            Publications = await Best(() => Objects(conn, PublicationsSql, ct)).ConfigureAwait(false),
            Subscriptions = await Best(() => Objects(conn, SubscriptionsSql, ct)).ConfigureAwait(false),
            ForeignServers = await Best(() => Objects(conn, ForeignServersSql, ct)).ConfigureAwait(false),
            EventTriggers = await Best(() => Objects(conn, EventTriggersSql, ct)).ConfigureAwait(false),
            Collations = await Best(() => Objects(conn, CollationsSql, ct)).ConfigureAwait(false),
            Casts = await Best(() => Objects(conn, CastsSql, ct)).ConfigureAwait(false),
            Operators = await Best(() => Objects(conn, OperatorsSql, ct)).ConfigureAwait(false),
            OperatorClasses = await Best(() => Objects(conn, OperatorClassesSql, ct)).ConfigureAwait(false),
            TextSearchConfigs = await Best(() => Objects(conn, TextSearchConfigsSql, ct)).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Read a kind that is nothing but <c>(id, schema, name, detail, comment)</c>. One helper for a dozen
    /// queries — the shape is the point (see <see cref="SchemaObjectInfo"/>), so the only thing that differs
    /// between them is the SQL.
    /// </summary>
    private static async Task<List<SchemaObjectInfo>> Objects(
        NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<SchemaObjectInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new SchemaObjectInfo(
                Id: r.GetInt64(0),
                Schema: r.IsDBNull(1) ? "" : r.GetString(1),
                Name: r.GetString(2),
                Detail: r.IsDBNull(3) ? "" : r.GetString(3),
                Comment: r.IsDBNull(4) ? null : r.GetString(4)));
        }
        return list;
    }

    /// <summary>What a publication replicates, and how much of the database.</summary>
    private const string PublicationsSql = """
        select p.oid::bigint, '', p.pubname,
               case when p.puballtables then 'all tables'
                    else coalesce((select count(*)::text || ' tables'
                                   from pg_publication_rel pr where pr.prpubid = p.oid), '0 tables') end
               || ' · ' ||
               coalesce(nullif(concat_ws(', ',
                 case when p.pubinsert then 'insert' end,
                 case when p.pubupdate then 'update' end,
                 case when p.pubdelete then 'delete' end,
                 case when p.pubtruncate then 'truncate' end), ''), 'nothing'),
               obj_description(p.oid, 'pg_publication')
        from pg_publication p
        order by p.pubname
        """;

    /// <summary>
    /// Subscriptions belonging to the connected database.
    /// <para>
    /// <c>subconninfo</c> is <b>never</b> selected. It is the publisher's connection string and can carry a
    /// password, so reading it at all would put a credential into a tree row, a tooltip and a clipboard
    /// (§1.1). What a subscription is for — whether it is on and which publications it takes — needs none
    /// of it.
    /// </para>
    /// </summary>
    private const string SubscriptionsSql = """
        select s.oid::bigint, '', s.subname,
               case when s.subenabled then 'enabled' else 'disabled' end
               || coalesce(' · publications: ' || nullif(array_to_string(s.subpublications, ', '), ''), ''),
               obj_description(s.oid, 'pg_subscription')
        from pg_subscription s
        where s.subdbid = (select oid from pg_database where datname = current_database())
        order by s.subname
        """;

    /// <summary>
    /// Foreign servers and the wrapper behind each.
    /// <para>
    /// <c>srvoptions</c> is deliberately not rendered, and neither are user mappings: a foreign server's
    /// options and a mapping's options routinely contain a password, and <c>pg_user_mappings</c> shows them
    /// in full to the owner (§1.1). The name, the wrapper and the version answer "what does this foreign
    /// table point at" without any of that.
    /// </para>
    /// </summary>
    private const string ForeignServersSql = """
        select s.oid::bigint, '', s.srvname,
               w.fdwname || coalesce(' · version ' || s.srvversion, ''),
               obj_description(s.oid, 'pg_foreign_server')
        from pg_foreign_server s
        join pg_foreign_data_wrapper w on w.oid = s.srvfdw
        order by s.srvname
        """;

    /// <summary>Event triggers: the DDL event, the state, and the function called.</summary>
    private const string EventTriggersSql = """
        select t.oid::bigint, '', t.evtname,
               t.evtevent
               || case t.evtenabled when 'D' then ' · disabled' when 'O' then '' else ' · replica only' end
               || ' · ' || quote_ident(n.nspname) || '.' || quote_ident(p.proname) || '()',
               obj_description(t.oid, 'pg_event_trigger')
        from pg_event_trigger t
        join pg_proc p on p.oid = t.evtfoid
        join pg_namespace n on n.oid = p.pronamespace
        order by t.evtname
        """;

    /// <summary>
    /// Collations defined outside the catalog schemas. The provider and whether it is deterministic, which
    /// is what makes a collation behave surprisingly in a comparison.
    /// </summary>
    private const string CollationsSql = """
        select c.oid::bigint, n.nspname, c.collname,
               case c.collprovider when 'i' then 'icu' when 'c' then 'libc' when 'b' then 'builtin'
                                   else c.collprovider::text end
               || coalesce(' · ' || nullif(c.collcollate, ''), '')
               || case when not c.collisdeterministic then ' · nondeterministic' else '' end,
               obj_description(c.oid, 'pg_collation')
        from pg_collation c
        join pg_namespace n on n.oid = c.collnamespace
        where n.nspname <> 'pg_catalog' and n.nspname <> 'information_schema'
        order by n.nspname, c.collname
        """;

    /// <summary>
    /// User-defined casts, named by what they convert. A cast has no name of its own in the catalog, so the
    /// row is called <c>source → target</c> — which is what someone looking for one would search for.
    /// </summary>
    private const string CastsSql = """
        select c.oid::bigint, '',
               format_type(c.castsource, null) || ' → ' || format_type(c.casttarget, null),
               case c.castcontext when 'i' then 'implicit' when 'a' then 'in assignment'
                                  else 'explicit' end
               || case c.castmethod when 'b' then ' · binary-coercible' when 'i' then ' · via i/o'
                                    else '' end,
               obj_description(c.oid, 'pg_cast')
        from pg_cast c
        left join pg_type s on s.oid = c.castsource
        left join pg_namespace sn on sn.oid = s.typnamespace
        left join pg_type t on t.oid = c.casttarget
        left join pg_namespace tn on tn.oid = t.typnamespace
        where sn.nspname <> 'pg_catalog' or tn.nspname <> 'pg_catalog'
        order by 3
        """;

    /// <summary>User-defined operators, with their operand and result types.</summary>
    private const string OperatorsSql = """
        select o.oid::bigint, n.nspname, o.oprname,
               coalesce(format_type(o.oprleft, null) || ' ', '')
               || coalesce(format_type(o.oprright, null), '')
               || ' → ' || format_type(o.oprresult, null),
               obj_description(o.oid, 'pg_operator')
        from pg_operator o
        join pg_namespace n on n.oid = o.oprnamespace
        where n.nspname <> 'pg_catalog' and n.nspname <> 'information_schema'
        order by n.nspname, o.oprname
        """;

    /// <summary>Operator classes — which index method a type can be indexed with, and by default or not.</summary>
    private const string OperatorClassesSql = """
        select c.oid::bigint, n.nspname, c.opcname,
               a.amname || ' · ' || format_type(c.opcintype, null)
               || case when c.opcdefault then ' · default' else '' end,
               obj_description(c.oid, 'pg_opclass')
        from pg_opclass c
        join pg_namespace n on n.oid = c.opcnamespace
        join pg_am a on a.oid = c.opcmethod
        where n.nspname <> 'pg_catalog' and n.nspname <> 'information_schema'
        order by n.nspname, c.opcname
        """;

    /// <summary>Text-search configurations, with the parser each uses.</summary>
    private const string TextSearchConfigsSql = """
        select c.oid::bigint, n.nspname, c.cfgname,
               'parser ' || quote_ident(pn.nspname) || '.' || quote_ident(p.prsname),
               obj_description(c.oid, 'pg_ts_config')
        from pg_ts_config c
        join pg_namespace n on n.oid = c.cfgnamespace
        join pg_ts_parser p on p.oid = c.cfgparser
        join pg_namespace pn on pn.oid = p.prsnamespace
        where n.nspname <> 'pg_catalog' and n.nspname <> 'information_schema'
        order by n.nspname, c.cfgname
        """;

    /// <summary>
    /// Tablespaces — cluster-wide, so this is the server's own list rather than a database's.
    /// <para>
    /// <c>pg_tablespace_location</c> raises for a role that may not see it, and the size is not free, so
    /// both are wrapped: a tablespace whose location this role cannot read still appears, by name.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SchemaObjectInfo>> GetTablespacesAsync(CancellationToken ct)
    {
        const string sql = """
            select t.oid::bigint, '', t.spcname,
                   coalesce(nullif(pg_catalog.pg_tablespace_location(t.oid), ''), 'the data directory'),
                   obj_description(t.oid, 'pg_tablespace')
            from pg_tablespace t
            order by t.spcname
            """;
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await Best(() => Objects(conn, sql, ct)).ConfigureAwait(false);
    }

    /// <summary>Run one kind's read, or fall back to an empty list when the server refuses it. Never
    /// swallows a cancellation — the caller's Esc still stops the whole call.</summary>
    private static async Task<IReadOnlyList<T>> Best<T>(Func<Task<List<T>>> read)
    {
        try { return await read().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Array.Empty<T>(); }
    }

    /// <summary>
    /// Sequences, with the column each one backs where there is one.
    /// <para>
    /// <c>pg_sequences</c> rather than <c>pg_sequence</c>: the view is what exposes <c>last_value</c>, and it
    /// reports null both for a sequence never read from and for one this role may not read — two states we
    /// deliberately do not distinguish, because neither is a number to show.
    /// </para>
    /// <para>
    /// The owning column comes from <c>pg_depend</c> with <c>deptype in ('a','i')</c>: 'a' is a
    /// <c>serial</c>'s auto dependency and 'i' an identity column's internal one. Reading only 'a' would
    /// leave every <c>generated as identity</c> column with no route to its sequence, which is the modern
    /// spelling and so the one more likely to be in a new schema.
    /// </para>
    /// </summary>
    private static async Task<List<SequenceInfo>> ReadSequencesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            select c.oid::bigint, s.schemaname, s.sequencename, format_type(q.seqtypid, null),
                   s.last_value, s.increment_by, s.min_value, s.max_value, s.cycle,
                   case when d.refobjid is not null
                        then dn.nspname || '.' || dc.relname || '.' || a.attname end as owned_by,
                   obj_description(c.oid, 'pg_class') as comment
            from pg_sequences s
            join pg_namespace sn on sn.nspname = s.schemaname
            join pg_class c on c.relname = s.sequencename and c.relnamespace = sn.oid
            join pg_sequence q on q.seqrelid = c.oid
            left join pg_depend d
                   on d.objid = c.oid and d.classid = 'pg_class'::regclass
                  and d.refclassid = 'pg_class'::regclass and d.deptype in ('a', 'i')
            left join pg_class dc on dc.oid = d.refobjid
            left join pg_namespace dn on dn.oid = dc.relnamespace
            left join pg_attribute a on a.attrelid = d.refobjid and a.attnum = d.refobjsubid
            where s.schemaname <> 'pg_catalog' and s.schemaname <> 'information_schema'
              and s.schemaname not like 'pg=_temp%' escape '='
              and s.schemaname not like 'pg=_toast%' escape '='
            order by s.schemaname, s.sequencename
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<SequenceInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new SequenceInfo(
                Id: r.GetInt64(0),
                Schema: r.GetString(1),
                Name: r.GetString(2),
                DataType: r.IsDBNull(3) ? "bigint" : r.GetString(3),
                LastValue: r.IsDBNull(4) ? null : r.GetInt64(4),
                Increment: r.GetInt64(5),
                MinValue: r.GetInt64(6),
                MaxValue: r.GetInt64(7),
                Cycles: r.GetBoolean(8),
                OwnedBy: r.IsDBNull(9) ? null : r.GetString(9),
                Comment: r.IsDBNull(10) ? null : r.GetString(10)));
        }
        return list;
    }

    /// <summary>
    /// Enums, domains, composites and ranges, each with the server's own rendering of what it is.
    /// <para>
    /// The detail is assembled in SQL rather than here because only the server can produce it: an enum's
    /// labels have to come out in <c>enumsortorder</c>, a domain's check constraints through
    /// <c>pg_get_constraintdef</c>, and a composite's attributes through <c>format_type</c> — the same
    /// reasoning as <see cref="ConstraintInfo.Definition"/>.
    /// </para>
    /// <para>
    /// The composite type Postgres creates implicitly for every table and view is excluded (its
    /// <c>relkind</c> is not 'c'): otherwise every table in the database would appear a second time under
    /// Types, which is noise rather than information.
    /// </para>
    /// </summary>
    private static async Task<List<TypeInfo>> ReadTypesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            select t.oid::bigint, n.nspname, t.typname,
                   case t.typtype when 'e' then 'enum' when 'd' then 'domain'
                                  when 'c' then 'composite' when 'r' then 'range' end as kind,
                   case t.typtype
                     when 'e' then (select string_agg(quote_literal(e.enumlabel), ', ' order by e.enumsortorder)
                                    from pg_enum e where e.enumtypid = t.oid)
                     when 'd' then format_type(t.typbasetype, t.typtypmod)
                                   || case when t.typnotnull then ' not null' else '' end
                                   || coalesce((select ' ' || string_agg(pg_get_constraintdef(k.oid), ' ')
                                                from pg_constraint k where k.contypid = t.oid), '')
                     when 'c' then (select string_agg(a.attname || ' ' || format_type(a.atttypid, a.atttypmod),
                                                      ', ' order by a.attnum)
                                    from pg_attribute a
                                    where a.attrelid = t.typrelid and a.attnum > 0 and not a.attisdropped)
                     when 'r' then (select format_type(g.rngsubtype, null)
                                    from pg_range g where g.rngtypid = t.oid)
                   end as detail,
                   obj_description(t.oid, 'pg_type') as comment
            from pg_type t
            join pg_namespace n on n.oid = t.typnamespace
            left join pg_class c on c.oid = t.typrelid
            where t.typtype in ('e', 'd', 'c', 'r')
              and (t.typrelid = 0 or c.relkind = 'c')
              and n.nspname <> 'pg_catalog' and n.nspname <> 'information_schema'
              and n.nspname not like 'pg=_temp%' escape '='
              and n.nspname not like 'pg=_toast%' escape '='
            order by n.nspname, t.typname
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<TypeInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new TypeInfo(
                Id: r.GetInt64(0),
                Schema: r.GetString(1),
                Name: r.GetString(2),
                Kind: MapTypeKind(r.IsDBNull(3) ? "" : r.GetString(3)),
                Detail: r.IsDBNull(4) ? "" : r.GetString(4),
                Comment: r.IsDBNull(5) ? null : r.GetString(5)));
        }
        return list;
    }

    private static TypeKind MapTypeKind(string kind) => kind switch
    {
        "enum" => TypeKind.Enum,
        "domain" => TypeKind.Domain,
        "range" => TypeKind.Range,
        _ => TypeKind.Composite,
    };

    /// <summary>Installed extensions and their versions — the answer to "is pgcrypto here".</summary>
    private static async Task<List<ExtensionInfo>> ReadExtensionsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            select e.oid::bigint, e.extname, coalesce(n.nspname, ''), e.extversion,
                   obj_description(e.oid, 'pg_extension') as comment
            from pg_extension e
            left join pg_namespace n on n.oid = e.extnamespace
            order by e.extname
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ExtensionInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new ExtensionInfo(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    /// <summary>
    /// Row-level security policies, with their expressions rendered by <c>pg_get_expr</c>.
    /// <para>
    /// Carries its table's id, so the tree can show a policy under the table it applies to as well as in a
    /// per-database group — under the table is where you are standing when a row count surprises you.
    /// </para>
    /// <para>
    /// An empty <c>polroles</c> means the policy applies to everyone, which Postgres spells <c>PUBLIC</c>;
    /// it is reported as that rather than as no roles at all, because "applies to nobody" would be the
    /// opposite of the truth.
    /// </para>
    /// </summary>
    /// <param name="tableId">One relation's policies, or every relation's when null.</param>
    private static async Task<List<PolicyInfo>> ReadPoliciesAsync(
        NpgsqlConnection conn, CancellationToken ct, long? tableId = null)
    {
        const string sql = """
            select p.oid::bigint, p.polname, p.polrelid::bigint, p.polcmd::text, p.polpermissive,
                   coalesce(
                     (select array_agg(coalesce(r.rolname::text, 'public') order by r.rolname)
                      from unnest(p.polroles) as ro(oid)
                      left join pg_roles r on r.oid = ro.oid),
                     array['public']) as roles,
                   pg_get_expr(p.polqual, p.polrelid)      as using_expr,
                   pg_get_expr(p.polwithcheck, p.polrelid) as check_expr
            from pg_policy p
            join pg_class c on c.oid = p.polrelid
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname <> 'pg_catalog' and n.nspname <> 'information_schema'
              and n.nspname not like 'pg=_temp%' escape '='
              and n.nspname not like 'pg=_toast%' escape '='
            """;
        // The per-relation filter is appended rather than expressed as `$1 is null or …`: an untyped
        // parameter carrying a null gives Postgres nothing to infer the placeholder's type from, and the
        // whole read then fails — silently, because Best() turns a failed kind into an empty list. The
        // symptom was every policy in the database disappearing while the per-table read worked, which is
        // what the live test caught.
        var filtered = tableId is null
            ? sql + "\n            order by n.nspname, c.relname, p.polname"
            : sql + "\n              and p.polrelid = $1\n            order by p.polname";
        await using var cmd = new NpgsqlCommand(filtered, conn);
        if (tableId is { } id) cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<PolicyInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new PolicyInfo(
                Id: r.GetInt64(0),
                Name: r.GetString(1),
                TableId: r.GetInt64(2),
                Command: MapPolicyCommand(r.GetString(3)),
                Permissive: r.GetBoolean(4),
                Roles: r.IsDBNull(5) ? ["public"] : r.GetFieldValue<string[]>(5),
                Using: r.IsDBNull(6) ? null : r.GetString(6),
                WithCheck: r.IsDBNull(7) ? null : r.GetString(7)));
        }
        return list;
    }

    /// <summary>
    /// Every role on the server (#120), with its memberships.
    /// <para>
    /// <c>pg_roles</c>, never <c>pg_authid</c>. The view exists precisely because the table holds the
    /// password hash: it masks <c>rolpassword</c> to <c>********</c> and is readable by any role, so this
    /// works for a non-superuser and cannot leak a credential even by accident. The hash is not selected
    /// here either — there is nowhere in <see cref="RoleInfo"/> to put it (§1.1).
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<RoleInfo>> GetRolesAsync(CancellationToken ct)
    {
        const string sql = """
            select r.oid::bigint, r.rolname, r.rolcanlogin, r.rolsuper, r.rolcreatedb, r.rolcreaterole,
                   r.rolinherit, r.rolconnlimit, r.rolvaliduntil,
                   coalesce(
                     (select array_agg(g.rolname order by g.rolname)
                      from pg_auth_members m
                      join pg_roles g on g.oid = m.roleid
                      where m.member = r.oid),
                     array[]::name[]) as member_of
            from pg_roles r
            order by r.rolname
            """;
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<RoleInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new RoleInfo(
                Id: r.GetInt64(0),
                Name: r.GetString(1),
                CanLogin: r.GetBoolean(2),
                IsSuperuser: r.GetBoolean(3),
                CanCreateDb: r.GetBoolean(4),
                CanCreateRole: r.GetBoolean(5),
                InheritsPrivileges: r.GetBoolean(6),
                ConnectionLimit: r.GetInt32(7),
                // Null here is "no expiry", which is the default — not a permission problem. Kept as null all
                // the way to the row so the two cannot be conflated (§1.1).
                ValidUntil: r.IsDBNull(8) ? null : new DateTimeOffset(r.GetDateTime(8)),
                MemberOf: r.IsDBNull(9) ? [] : r.GetFieldValue<string[]>(9)));
        }
        return list;
    }

    /// <summary>
    /// What one role may do on this database (#120): the three database-level privileges, then the
    /// per-relation grants out of <c>relacl</c>.
    /// <para>
    /// <c>aclexplode</c> rather than parsing the <c>aclitem</c> text: the compact form (<c>arwdDxt</c>) is a
    /// rendering of a bitmask, and re-deriving privilege names from it here would be a second implementation
    /// of something the server already spells out. The result is <c>SELECT, INSERT</c>, which is what the
    /// question was.
    /// </para>
    /// <para>
    /// Read-only, and that is a design decision rather than an omission: generating <c>GRANT</c> /
    /// <c>REVOKE</c> is not in scope, because a mistake there is a production incident and the write guard
    /// has no lexer for ACLs (§1.2).
    /// </para>
    /// </summary>
    public async Task<RoleGrants> GetRoleGrantsAsync(string roleName, CancellationToken ct)
    {
        // Parameterized, not interpolated: a role name is user data here (it came from the tree, but the
        // tree got it from a catalog the user's own DDL writes), and `has_database_privilege` takes it as a
        // value.
        const string databaseSql = """
            select 'database ' || current_database(),
                   array_remove(array[
                     case when has_database_privilege($1, current_database(), 'CONNECT') then 'CONNECT' end,
                     case when has_database_privilege($1, current_database(), 'CREATE')  then 'CREATE'  end,
                     case when has_database_privilege($1, current_database(), 'TEMP')    then 'TEMPORARY' end
                   ], null)
            """;
        const string relationSql = """
            select n.nspname || '.' || c.relname,
                   array_agg(distinct a.privilege_type order by a.privilege_type)
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            cross join lateral aclexplode(c.relacl) as a
            join pg_roles g on g.oid = a.grantee
            where g.rolname = $1
              and c.relkind in ('r', 'v', 'm', 'f', 'p')
              and n.nspname <> 'pg_catalog' and n.nspname <> 'information_schema'
              and n.nspname not like 'pg=_temp%' escape '='
              and n.nspname not like 'pg=_toast%' escape '='
            group by n.nspname, c.relname
            order by n.nspname, c.relname
            """;

        try
        {
            await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            var grants = new List<RoleGrant>();
            // The database row first: it is the one privilege set that exists even for a role with no object
            // grants at all, so a role with nothing else still says something true.
            grants.AddRange(await ReadGrantsAsync(conn, databaseSql, roleName, ct).ConfigureAwait(false));
            grants.AddRange(await ReadGrantsAsync(conn, relationSql, roleName, ct).ConfigureAwait(false));
            return RoleGrants.Of(grants);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Reported as "could not see", never as "no grants": showing a refused read as an empty
            // privilege list is the one mistake a privilege screen must not make.
            return RoleGrants.NotVisible;
        }
    }

    private static async Task<List<RoleGrant>> ReadGrantsAsync(
        NpgsqlConnection conn, string sql, string roleName, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(roleName);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<RoleGrant>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var privileges = r.IsDBNull(1) ? [] : r.GetFieldValue<string[]>(1);
            // A row with no privileges is not a grant. It arrives from the database query for a role that
            // may do nothing at all here, and listing it would read as a grant of nothing.
            if (privileges.Length > 0) list.Add(new RoleGrant(r.GetString(0), privileges));
        }
        return list;
    }

    /// <summary>
    /// One relation's per-column extras: default, identity, generated expression, collation, comment.
    /// <para>
    /// The default comes from <c>pg_get_expr</c> rather than the raw <c>adbin</c> tree, so a
    /// <c>serial</c> reads as <c>nextval('film_film_id_seq'::regclass)</c> — the route from a column to its
    /// sequence, which is the half <see cref="SequenceInfo.OwnedBy"/> does not provide.
    /// </para>
    /// <para>
    /// The collation is reported only when it differs from the type's own (<c>attcollation</c> versus
    /// <c>typcollation</c>): every text column has a collation, and listing the database default on all of
    /// them would bury the one column that was given a different one.
    /// </para>
    /// </summary>
    private static async Task<List<ColumnDetail>> ReadColumnDetailsAsync(
        NpgsqlConnection conn, long tableId, CancellationToken ct)
    {
        var sql = $"""
            select a.attnum,
                   pg_get_expr(d.adbin, d.adrelid)                as default_expr,
                   a.attidentity::text                            as identity,
                   case when a.attgenerated <> '' then pg_get_expr(d.adbin, d.adrelid) end as generated,
                   case when a.attcollation <> 0 and a.attcollation <> t.typcollation
                        then co.collname end                      as collation,
                   col_description(a.attrelid, a.attnum)          as comment
            from pg_attribute a
            join pg_type t on t.oid = a.atttypid
            left join pg_attrdef d on d.adrelid = a.attrelid and d.adnum = a.attnum
            left join pg_collation co on co.oid = a.attcollation
            where a.attrelid = {tableId} and a.attnum > 0 and not a.attisdropped
            order by a.attnum
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ColumnDetail>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            // A generated column's expression arrives in the same adbin as a default would, so it is read
            // once and reported as whichever it is — never as both, which would say the column has a
            // default it can never use.
            var generated = r.IsDBNull(3) ? null : r.GetString(3);
            list.Add(new ColumnDetail(
                Ordinal: r.GetInt16(0),
                Default: generated is not null || r.IsDBNull(1) ? null : r.GetString(1),
                Identity: MapIdentity(r.IsDBNull(2) ? "" : r.GetString(2)),
                GeneratedExpression: generated,
                Collation: r.IsDBNull(4) ? null : r.GetString(4),
                Comment: r.IsDBNull(5) ? null : r.GetString(5)));
        }
        return list;
    }

    private static ColumnIdentity MapIdentity(string attidentity) => attidentity switch
    {
        "a" => ColumnIdentity.Always,
        "d" => ColumnIdentity.ByDefault,
        _ => ColumnIdentity.None,
    };

    /// <summary>
    /// One relation's rules. <c>_RETURN</c> is excluded: on a view that rule <em>is</em> the view, and the
    /// tree already shows it as a definition — listing it here would make every view look like it had a rule.
    /// </summary>
    private static async Task<List<RuleInfo>> ReadRulesAsync(
        NpgsqlConnection conn, long tableId, CancellationToken ct)
    {
        var sql = $"""
            select r.oid::bigint, r.rulename, pg_get_ruledef(r.oid, true)
            from pg_rewrite r
            where r.ev_class = {tableId} and r.rulename <> '_RETURN'
            order by r.rulename
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<RuleInfo>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new RuleInfo(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }

    /// <summary>The relation's comment, as a zero-or-one list so it can share <see cref="Best{T}"/>.</summary>
    private static async Task<List<string>> ReadTableCommentAsync(
        NpgsqlConnection conn, long tableId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"select obj_description({tableId}, 'pg_class')", conn);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is string { Length: > 0 } text ? [text] : [];
    }

    /// <summary><c>pg_policy.polcmd</c>'s single char, spelled the way the DDL does.</summary>
    private static string MapPolicyCommand(string cmd) => cmd switch
    {
        "r" => "SELECT",
        "a" => "INSERT",
        "w" => "UPDATE",
        "d" => "DELETE",
        _ => "ALL",
    };

    // The id is the catalog OID (pure digits, read as bigint) — safe to interpolate, and the pg_get_*def
    // functions take an oid, which an integer literal casts to implicitly.
    public Task<string> GetViewDefinitionAsync(long tableId, CancellationToken ct)
        => ScalarTextAsync($"select pg_get_viewdef({tableId}, true)", ct);

    public Task<string> GetRoutineDefinitionAsync(long routineId, CancellationToken ct)
        => ScalarTextAsync($"select pg_get_functiondef({routineId})", ct);

    /// <summary>
    /// One relation's constraints, indexes and triggers (#46). Three reads on one connection rather than
    /// three round trips: they are always wanted together, because what the tree does with them is build the
    /// folders under a table the user just expanded.
    /// </summary>
    public async Task<TableDetails> GetTableDetailsAsync(long tableId, CancellationToken ct)
    {
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var constraints = await ReadConstraintsAsync(conn, tableId, ct).ConfigureAwait(false);
        var indexes = await ReadIndexesAsync(conn, tableId, ct).ConfigureAwait(false);
        var triggers = await ReadTriggersAsync(conn, tableId, ct).ConfigureAwait(false);
        // Best-effort on its own, like the per-database kinds (#119): a role that cannot read pg_policy must
        // still get the constraints and indexes it can.
        var policies = await Best(() => ReadPoliciesAsync(conn, ct, tableId)).ConfigureAwait(false);
        var columns = await Best(() => ReadColumnDetailsAsync(conn, tableId, ct)).ConfigureAwait(false);
        var rules = await Best(() => ReadRulesAsync(conn, tableId, ct)).ConfigureAwait(false);
        var comment = await Best(() => ReadTableCommentAsync(conn, tableId, ct)).ConfigureAwait(false);
        return new TableDetails(constraints, indexes, triggers, policies)
        {
            Columns = columns,
            Rules = rules,
            Comment = comment.Count > 0 ? comment[0] : null,
        };
    }

    /// <summary>
    /// Every relation's size in this database (#76), in one pass. The three size functions are separate calls
    /// per relation but a single query overall — Postgres has no bulk form, and a round trip per table would
    /// be far worse than a wider one.
    /// </summary>
    public async Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(CancellationToken ct)
    {
        // pg_total_relation_size stats files per relation, so this is the expensive read in the app. Only
        // the relation kinds that have storage: a view has none, and asking costs the same as asking for a
        // table. reltuples is -1 on a never-analysed table, which the mapping turns into null rather than
        // letting a negative row count reach a label.
        const string sql = """
            select c.oid::bigint,
                   pg_total_relation_size(c.oid)::bigint,
                   pg_table_size(c.oid)::bigint,
                   pg_indexes_size(c.oid)::bigint,
                   coalesce(pg_total_relation_size(c.reltoastrelid), 0)::bigint,
                   c.reltuples::bigint
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('r','m','f','p','t')
              and n.nspname not in ('pg_catalog','information_schema')
              and n.nspname not like 'pg\_temp%' and n.nspname not like 'pg\_toast%'
            """;
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var list = new List<RelationSize>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            // A null size means the relation is no longer there.
            //
            // pg_class is read under the query's MVCC snapshot, but pg_total_relation_size stats files —
            // which is not, so a relation dropped by someone else while this query runs is still listed and
            // has no size. Skipped rather than reported as 0: zero bytes says "this table is empty", and the
            // row it belongs to is about to disappear from the tree anyway. Sizes are already best-effort
            // and arrive after the tree renders (#76), so one missing entry costs a label, not a feature —
            // whereas throwing loses every other relation's size to one concurrent DROP.
            if (r.IsDBNull(1)) continue;

            var rows = r.IsDBNull(5) ? -1 : r.GetInt64(5);
            list.Add(new RelationSize(
                TableId: r.GetInt64(0),
                TotalBytes: r.GetInt64(1),
                TableBytes: r.IsDBNull(2) ? 0 : r.GetInt64(2),
                IndexBytes: r.IsDBNull(3) ? 0 : r.GetInt64(3),
                ToastBytes: r.IsDBNull(4) ? 0 : r.GetInt64(4),
                // -1 means "never analysed". A row count of minus one is not a row count.
                EstimatedRows: rows < 0 ? null : rows));
        }
        return list;
    }

    /// <summary>
    /// Every database's size (#76). Guarded per row: <c>pg_database_size</c> raises for a database the caller
    /// cannot connect to, so it is only called where <c>has_database_privilege</c> says it will work — one
    /// inaccessible database must not cost the sizes of the rest, and an exception per row would.
    /// </summary>
    public async Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(CancellationToken ct)
    {
        const string sql = """
            select d.datname,
                   case when has_database_privilege(d.datname, 'CONNECT')
                        then pg_database_size(d.datname)::bigint
                   end
            from pg_database d
            where d.datistemplate = false
            order by d.datname
            """;
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var list = new List<DatabaseSize>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new DatabaseSize(r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1)));
        return list;
    }

    private static async Task<List<ConstraintInfo>> ReadConstraintsAsync(
        NpgsqlConnection conn, long tableId, CancellationToken ct)
    {
        // pg_get_constraintdef rather than a reassembly from catalog columns: a CHECK body cannot be rebuilt
        // from them at all, and where it could be, the server's own text is the one that matches the table.
        // contype 'n' is excluded: PostgreSQL 18 stores every NOT NULL as a real pg_constraint row, so a
        // three-column table reports three of them (verified on 18.3; 17.10 reports none). The column rows already say "not null", and listing them
        // here would bury the table's actual constraints under one node per column and inflate the folder's
        // count with information already on screen.
        const string sql = """
            select con.oid::bigint, con.conname, con.contype::text,
                   coalesce(con.conkey, '{}')::int[], pg_get_constraintdef(con.oid, true)
            from pg_constraint con
            where con.conrelid = $1 and con.contype <> 'n'
            order by case con.contype when 'p' then 0 when 'u' then 1 when 'f' then 2 when 'c' then 3 else 4 end,
                     con.conname
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(tableId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var list = new List<ConstraintInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ConstraintInfo(
                Id: r.GetInt64(0),
                Name: r.GetString(1),
                Kind: MapConType(r.GetString(2)[0]),
                Ordinals: r.GetFieldValue<int[]>(3),
                Definition: r.IsDBNull(4) ? "" : r.GetString(4)));
        }
        return list;
    }

    private static async Task<List<IndexInfo>> ReadIndexesAsync(
        NpgsqlConnection conn, long tableId, CancellationToken ct)
    {
        // The pg_constraint join is narrowed three ways, and each one matters. conrelid = indrelid and
        // contype in (p,u,x): a FOREIGN KEY also sets conindid, pointing at the *referenced* table's index —
        // so an unrestricted join both duplicated a parent table's index once per key referencing it and
        // marked a hand-made unique index as constraint-owned, which then vanished from generated DDL and
        // took the FK depending on it with it.
        //
        // indkey spans indnatts, so it includes INCLUDE (non-key) columns; only the first indnkeyatts of them
        // are the key the planner can search on. Reporting all of them made `create index … (a) include (b)`
        // read as a two-column key, which is the one thing an index row must not misstate.
        //
        // indkey is an int2vector, and casting one to an array keeps its **zero**-based bounds — `[0:0]={1}`,
        // which is not an int[] as far as a client is concerned. Rebuilding it with array_agg over unnest
        // gives an ordinary 1-based array, and drops the 0 entries while it is there: a 0 in indkey marks an
        // expression key rather than a column, and an ordinal that points at no column would resolve to the
        // wrong name. An index on nothing but expressions therefore reports no ordinals at all, which is
        // correct — its definition is what says what it covers.
        const string sql = """
            select i.indexrelid::bigint, c.relname, i.indisunique, i.indisprimary, i.indisvalid,
                   coalesce((select array_agg(k order by ord)
                             from unnest(i.indkey::int2[]) with ordinality as u(k, ord)
                             where k > 0 and ord <= i.indnkeyatts), '{}')::int[],
                   pg_get_indexdef(i.indexrelid, 0, true),
                   con.oid is not null,
                   pg_relation_size(i.indexrelid)::bigint
            from pg_index i
            join pg_class c on c.oid = i.indexrelid
            left join pg_constraint con
                   on con.conindid = i.indexrelid
                  and con.conrelid = i.indrelid
                  and con.contype in ('p', 'u', 'x')
            where i.indrelid = $1
            order by i.indisprimary desc, c.relname
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(tableId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var list = new List<IndexInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new IndexInfo(
                Id: r.GetInt64(0),
                Name: r.GetString(1),
                IsUnique: r.GetBoolean(2),
                IsPrimary: r.GetBoolean(3),
                IsValid: r.GetBoolean(4),
                Ordinals: r.GetFieldValue<int[]>(5),
                Definition: r.IsDBNull(6) ? "" : r.GetString(6),
                BackedByConstraint: r.GetBoolean(7),
                // Read here rather than in the bulk relation-size pass: an index's size is only wanted once
                // its table is expanded, and it comes free from a query already keyed on indexrelid (#76).
                SizeBytes: r.GetInt64(8)));
        }
        return list;
    }

    private static async Task<List<TriggerInfo>> ReadTriggersAsync(
        NpgsqlConnection conn, long tableId, CancellationToken ct)
    {
        // tgisinternal excludes the triggers Postgres creates to enforce foreign keys and deferred
        // constraints: they are the constraint, already listed as one, and there are three per FK.
        const string sql = """
            select t.oid::bigint, t.tgname, t.tgenabled::text, pg_get_triggerdef(t.oid, true)
            from pg_trigger t
            where t.tgrelid = $1 and not t.tgisinternal
            order by t.tgname
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(tableId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var list = new List<TriggerInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new TriggerInfo(
                Id: r.GetInt64(0),
                Name: r.GetString(1),
                // 'D' is disabled; 'O', 'R' and 'A' are all enabled, differing only in which
                // session_replication_role they fire under.
                Enabled: r.GetString(2)[0] != 'D',
                Definition: r.IsDBNull(3) ? "" : r.GetString(3)));
        }
        return list;
    }

    private static ConstraintKind MapConType(char contype) => contype switch
    {
        'p' => ConstraintKind.PrimaryKey,
        'u' => ConstraintKind.Unique,
        'c' => ConstraintKind.Check,
        'f' => ConstraintKind.ForeignKey,
        'x' => ConstraintKind.Exclusion,
        _ => ConstraintKind.Other,
    };

    private async Task<string> ScalarTextAsync(string sql, CancellationToken ct)
    {
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string ?? "";
    }

    private static RoutineKind MapProKind(char prokind) => prokind switch
    {
        'f' => RoutineKind.Function,
        'p' => RoutineKind.Procedure,
        'a' => RoutineKind.Aggregate,
        'w' => RoutineKind.Window,
        _ => RoutineKind.Function,
    };

    private static RelationKind MapRelKind(char relkind) => relkind switch
    {
        'r' => RelationKind.Table,
        'v' => RelationKind.View,
        'm' => RelationKind.MaterializedView,
        'f' => RelationKind.ForeignTable,
        'p' => RelationKind.Partitioned,
        _ => RelationKind.Table,
    };
}
