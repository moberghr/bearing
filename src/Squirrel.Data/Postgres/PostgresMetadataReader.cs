using Npgsql;
using Squirrel.Core.Data;
using Squirrel.Core.Schema;

namespace Squirrel.Data.Postgres;

/// <summary>Reads PostgreSQL catalog metadata into an immutable <see cref="SchemaSnapshot"/>.</summary>
public sealed class PostgresMetadataReader : IMetadataReader
{
    private readonly NpgsqlConnectionFactory _factory;

    public PostgresMetadataReader(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct)
    {
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select datname from pg_database where datistemplate = false order by datname", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var names = new List<string>();
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));
        return names;
    }

    public async Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct)
    {
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct);

        var searchPath = await ReadSearchPathAsync(conn, ct);
        var tables = await ReadTablesAsync(conn, ct);
        var columns = await ReadColumnsAsync(conn, ct);
        var fks = await ReadForeignKeysAsync(conn, ct);

        // Schemas ordered by search_path, then any remaining schemas that actually hold tables.
        var schemas = new List<string>(searchPath);
        foreach (var s in tables.Select(t => t.Schema).Distinct())
            if (!schemas.Contains(s, StringComparer.OrdinalIgnoreCase))
                schemas.Add(s);

        return new SchemaSnapshot(database, schemas, tables, columns, fks);
    }

    private static async Task<List<string>> ReadSearchPathAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select s from unnest(current_schemas(false)) s", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    private static async Task<List<PgTable>> ReadTablesAsync(NpgsqlConnection conn, CancellationToken ct)
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
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<PgTable>();
        while (await r.ReadAsync(ct))
        {
            var oid = (uint)r.GetInt64(0);
            var kind = MapRelKind(r.GetString(3)[0]);
            list.Add(new PgTable(oid, r.GetString(1), r.GetString(2), kind));
        }
        return list;
    }

    private static async Task<List<PgColumn>> ReadColumnsAsync(NpgsqlConnection conn, CancellationToken ct)
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
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<PgColumn>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new PgColumn(
                TableOid: (uint)r.GetInt64(0),
                AttNum: r.GetInt16(1),
                Name: r.GetString(2),
                DataType: r.GetString(3),
                NotNull: r.GetBoolean(4),
                IsPrimaryKey: r.GetBoolean(5)));
        }
        return list;
    }

    private static async Task<List<PgForeignKey>> ReadForeignKeysAsync(NpgsqlConnection conn, CancellationToken ct)
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
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<PgForeignKey>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new PgForeignKey(
                ConstraintOid: (uint)r.GetInt64(0),
                Name: r.GetString(1),
                ParentOid: (uint)r.GetInt64(2),
                ParentAttNums: r.GetFieldValue<short[]>(4),
                ReferencedOid: (uint)r.GetInt64(3),
                ReferencedAttNums: r.GetFieldValue<short[]>(5)));
        }
        return list;
    }

    public async Task<IReadOnlyList<PgRoutine>> GetRoutinesAsync(CancellationToken ct)
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
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<PgRoutine>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new PgRoutine(
                Oid: (uint)r.GetInt64(0),
                Schema: r.GetString(1),
                Name: r.GetString(2),
                Kind: MapProKind(r.GetString(3)[0]),
                Arguments: r.IsDBNull(4) ? "" : r.GetString(4),
                ReturnType: r.IsDBNull(5) ? "" : r.GetString(5)));
        }
        return list;
    }

    // The OID is a uint we read from the catalog (pure digits) — safe to interpolate, and both
    // pg_get_*def overloads take oid, which an integer literal casts to implicitly.
    public Task<string> GetViewDefinitionAsync(uint relOid, CancellationToken ct)
        => ScalarTextAsync($"select pg_get_viewdef({relOid}, true)", ct);

    public Task<string> GetRoutineDefinitionAsync(uint routineOid, CancellationToken ct)
        => ScalarTextAsync($"select pg_get_functiondef({routineOid})", ct);

    private async Task<string> ScalarTextAsync(string sql, CancellationToken ct)
    {
        await using var conn = await _factory.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string ?? "";
    }

    private static PgRoutineKind MapProKind(char prokind) => prokind switch
    {
        'f' => PgRoutineKind.Function,
        'p' => PgRoutineKind.Procedure,
        'a' => PgRoutineKind.Aggregate,
        'w' => PgRoutineKind.Window,
        _ => PgRoutineKind.Function,
    };

    private static PgRelKind MapRelKind(char relkind) => relkind switch
    {
        'r' => PgRelKind.Table,
        'v' => PgRelKind.View,
        'm' => PgRelKind.MaterializedView,
        'f' => PgRelKind.ForeignTable,
        'p' => PgRelKind.Partitioned,
        _ => PgRelKind.Table,
    };
}
