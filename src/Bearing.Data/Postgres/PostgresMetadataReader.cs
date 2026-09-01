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

    private static async Task<List<TableInfo>> ReadTablesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            select c.oid::bigint, n.nspname, c.relname, c.relkind::text
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
            list.Add(new TableInfo(id, r.GetString(1), r.GetString(2), kind));
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
                   pg_get_function_result(p.oid)    as result
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
                ReturnType: r.IsDBNull(5) ? "" : r.GetString(5)));
        }
        return list;
    }

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
        return new TableDetails(constraints, indexes, triggers);
    }

    private static async Task<List<ConstraintInfo>> ReadConstraintsAsync(
        NpgsqlConnection conn, long tableId, CancellationToken ct)
    {
        // pg_get_constraintdef rather than a reassembly from catalog columns: a CHECK body cannot be rebuilt
        // from them at all, and where it could be, the server's own text is the one that matches the table.
        const string sql = """
            select con.oid::bigint, con.conname, con.contype::text,
                   coalesce(con.conkey, '{}')::int[], pg_get_constraintdef(con.oid, true)
            from pg_constraint con
            where con.conrelid = $1
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
                             where k > 0), '{}')::int[],
                   pg_get_indexdef(i.indexrelid, 0, true)
            from pg_index i
            join pg_class c on c.oid = i.indexrelid
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
                Definition: r.IsDBNull(6) ? "" : r.GetString(6)));
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
