using System.Globalization;
using System.Linq;
using Microsoft.Data.SqlClient;
using Bearing.Core.Data;
using Bearing.Core.Schema;
// Bearing.Core.Data has its own driver-agnostic SqlParameter (see SqlWriteCommand), so the driver's is
// aliased rather than imported into an ambiguity.
using DriverParameter = Microsoft.Data.SqlClient.SqlParameter;

namespace Bearing.Data.SqlServer;

/// <summary>
/// Reads SQL Server catalog metadata into an immutable <see cref="SchemaSnapshot"/> — the sibling of
/// <see cref="Postgres.PostgresMetadataReader"/>, mapping <c>sys.*</c> onto the same neutral types.
/// <para>
/// The ids are SQL Server's: <c>object_id</c> and <c>column_id</c> are 32-bit, so they sit inside
/// <see cref="TableInfo.Id"/>'s <c>long</c> and <see cref="ColumnInfo.Ordinal"/>'s <c>int</c> without a
/// widening story. They are unique per database, which is exactly the snapshot's scope.
/// </para>
/// </summary>
public sealed class SqlServerMetadataReader : IMetadataReader
{
    private readonly SqlServerConnectionFactory _factory;

    public SqlServerMetadataReader(SqlServerConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// The databases this login can see. The four system databases are hidden: they are the server's own
    /// book-keeping, nobody browses them from a query tool, and leaving them in put <c>master</c> at the top
    /// of every database picker (the Postgres reader hides template databases for the same reason).
    /// <c>state = 0</c> keeps offline/restoring databases out — they cannot be connected to, so offering
    /// them only produces a login error. A row missing because the login may not see it is expected here,
    /// not an error.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(
            """
            select name from sys.databases
            where state = 0 and name not in ('master', 'tempdb', 'model', 'msdb')
            order by name
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var names = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            names.Add(reader.GetString(0));
        return names;
    }

    public async Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var searchPath = await ReadDefaultSchemaAsync(conn, ct).ConfigureAwait(false);
        var tables = await ReadTablesAsync(conn, ct).ConfigureAwait(false);
        var columns = await ReadColumnsAsync(conn, ct).ConfigureAwait(false);
        var fks = await ReadForeignKeysAsync(conn, ct).ConfigureAwait(false);

        // Schemas: the reachable one first, then any remaining schema that actually holds relations.
        var schemas = new List<string>(searchPath);
        foreach (var s in tables.Select(t => t.Schema).Distinct())
            if (!schemas.Contains(s, StringComparer.OrdinalIgnoreCase))
                schemas.Add(s);

        return new SchemaSnapshot(database, schemas, tables, columns, fks, searchPath);
    }

    /// <summary>
    /// The snapshot's "search path", which T-SQL has no real analogue for: there is one default schema per
    /// user, and an unqualified name is looked for there. Hence a single element.
    /// <para>
    /// SQL Server does fall back to <c>dbo</c> when the default schema is something else, and that fallback
    /// is deliberately not listed. The effect of leaving it out is that completion writes <c>dbo.Thing</c>
    /// qualified for a login whose default schema is not <c>dbo</c> — more typing than strictly needed, and
    /// always correct — whereas listing it would claim a bare name resolves in cases where a same-named
    /// relation in the default schema shadows it. Over-qualifying is the safe direction.
    /// </para>
    /// </summary>
    private static async Task<List<string>> ReadDefaultSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("select schema_name()", conn);
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return (scalar as string) is { Length: > 0 } schema ? new List<string> { schema } : new List<string>();
    }

    private static async Task<List<TableInfo>> ReadTablesAsync(SqlConnection conn, CancellationToken ct)
    {
        // type is char(2) — 'U ' and 'V ' arrive padded, hence the rtrim.
        const string sql = """
            select o.object_id, s.name, o.name, rtrim(o.type)
            from sys.objects o
            join sys.schemas s on s.schema_id = o.schema_id
            where o.type in ('U', 'V') and o.is_ms_shipped = 0
            order by s.name, o.name
            """;
        await using var cmd = new SqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<TableInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new TableInfo(r.GetInt32(0), r.GetString(1), r.GetString(2), MapObjectType(r.GetString(3))));
        return list;
    }

    private static async Task<List<ColumnInfo>> ReadColumnsAsync(SqlConnection conn, CancellationToken ct)
    {
        // user_type_id (not system_type_id) so an alias type reports its own name, the way it reads in DDL.
        // The PK join goes constraint → its unique index → that index's columns, which is the only path
        // from sys.key_constraints to the participating columns.
        const string sql = """
            select c.object_id, c.column_id, c.name,
                   t.name, c.max_length, c.precision, c.scale,
                   c.is_nullable,
                   case when pk.column_id is null then 0 else 1 end
            from sys.columns c
            join sys.objects o on o.object_id = c.object_id
            join sys.types t on t.user_type_id = c.user_type_id
            left join (
                select ic.object_id, ic.column_id
                from sys.key_constraints kc
                join sys.index_columns ic
                  on ic.object_id = kc.parent_object_id and ic.index_id = kc.unique_index_id
                where kc.type = 'PK'
            ) pk on pk.object_id = c.object_id and pk.column_id = c.column_id
            where o.type in ('U', 'V') and o.is_ms_shipped = 0
            order by c.object_id, c.column_id
            """;
        await using var cmd = new SqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ColumnInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ColumnInfo(
                TableId: r.GetInt32(0),
                Ordinal: r.GetInt32(1),
                Name: r.GetString(2),
                DataType: FormatType(r.GetString(3), r.GetInt16(4), r.GetByte(5), r.GetByte(6)),
                NotNull: !r.GetBoolean(7),
                IsPrimaryKey: r.GetInt32(8) == 1));
        }
        return list;
    }

    private static async Task<List<ForeignKeyInfo>> ReadForeignKeysAsync(SqlConnection conn, CancellationToken ct)
    {
        // One row per FK *column*, unlike Postgres' array columns, so the rows are folded back into one
        // ForeignKeyInfo below. `order by constraint_column_id` is load-bearing: it is what pairs
        // ParentOrdinals[i] with ReferencedOrdinals[i], and a composite key ordered any other way maps
        // columns to the wrong partners.
        const string sql = """
            select fk.object_id, fk.name,
                   fk.parent_object_id, fk.referenced_object_id,
                   fkc.parent_column_id, fkc.referenced_column_id
            from sys.foreign_keys fk
            join sys.foreign_key_columns fkc on fkc.constraint_object_id = fk.object_id
            where fk.is_ms_shipped = 0
            order by fk.object_id, fkc.constraint_column_id
            """;
        await using var cmd = new SqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var order = new List<int>();
        var byId = new Dictionary<int, (string Name, int Parent, int Referenced, List<int> P, List<int> R)>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = r.GetInt32(0);
            if (!byId.TryGetValue(id, out var fk))
            {
                fk = (r.GetString(1), r.GetInt32(2), r.GetInt32(3), new List<int>(), new List<int>());
                byId[id] = fk;
                order.Add(id);
            }
            fk.P.Add(r.GetInt32(4));
            fk.R.Add(r.GetInt32(5));
        }

        return order
            .Select(id => new ForeignKeyInfo(
                Id: id,
                Name: byId[id].Name,
                ParentTableId: byId[id].Parent,
                ParentOrdinals: byId[id].P,
                ReferencedTableId: byId[id].Referenced,
                ReferencedOrdinals: byId[id].R))
            .ToList();
    }

    public async Task<IReadOnlyList<RoutineInfo>> GetRoutinesAsync(CancellationToken ct)
    {
        const string routineSql = """
            select o.object_id, s.name, o.name, rtrim(o.type)
            from sys.objects o
            join sys.schemas s on s.schema_id = o.schema_id
            where o.type in ('P', 'FN', 'IF', 'TF', 'AF') and o.is_ms_shipped = 0
            order by s.name, o.name
            """;
        // parameter_id 0 is the scalar function's *return* value, not an argument — it carries the return
        // type and an empty name, which is how ReturnType is filled below.
        const string paramSql = """
            select p.object_id, p.parameter_id, p.name,
                   t.name, p.max_length, p.precision, p.scale, p.is_output
            from sys.parameters p
            join sys.objects o on o.object_id = p.object_id
            join sys.types t on t.user_type_id = p.user_type_id
            where o.type in ('P', 'FN', 'IF', 'TF', 'AF') and o.is_ms_shipped = 0
            order by p.object_id, p.parameter_id
            """;

        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var routines = new List<(long Id, string Schema, string Name, string Type)>();
        await using (var cmd = new SqlCommand(routineSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                routines.Add((r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }

        var args = new Dictionary<long, List<string>>();
        var returns = new Dictionary<long, string>();
        await using (var cmd = new SqlCommand(paramSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = (long)r.GetInt32(0);
                var type = FormatType(r.GetString(3), r.GetInt16(4), r.GetByte(5), r.GetByte(6));
                if (r.GetInt32(1) == 0) { returns[id] = type; continue; }

                var name = r.GetString(2);
                if (!args.TryGetValue(id, out var list)) args[id] = list = new List<string>();
                list.Add(r.GetBoolean(7) ? $"{name} {type} output" : $"{name} {type}");
            }
        }

        return routines
            .Select(x => new RoutineInfo(
                Id: x.Id,
                Schema: x.Schema,
                Name: x.Name,
                Kind: MapRoutineType(x.Type),
                Arguments: args.TryGetValue(x.Id, out var list) ? string.Join(", ", list) : "",
                // A procedure returns nothing to describe, and a table-valued function's shape is its own
                // column list rather than a type — "table" is what T-SQL itself writes in the header.
                ReturnType: x.Type switch
                {
                    "P" => "",
                    "IF" or "TF" => "table",
                    _ => returns.TryGetValue(x.Id, out var ret) ? ret : "",
                }))
            .ToList();
    }

    // OBJECT_DEFINITION takes the id as a *parameter*, not interpolated. The Postgres reader interpolates
    // because its value is a catalog OID it read as bigint, which is a defensible exception rather than a
    // habit worth copying into a new file. The cast is because object_id is int-wide while the neutral
    // contract carries a long.
    public Task<string> GetViewDefinitionAsync(long tableId, CancellationToken ct)
        => DefinitionAsync(tableId, ct);

    public Task<string> GetRoutineDefinitionAsync(long routineId, CancellationToken ct)
        => DefinitionAsync(routineId, ct);

    /// <summary>The object's source text, or empty when there is none to give — an encrypted or
    /// natively-compiled object returns NULL, and so does an id this login cannot see.</summary>
    private async Task<string> DefinitionAsync(long objectId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand("select object_definition(cast(@id as int))", conn);
        cmd.Parameters.Add(new DriverParameter("@id", System.Data.SqlDbType.BigInt) { Value = objectId });
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string ?? "";
    }

    /// <summary>
    /// The type as it reads in DDL — <c>nvarchar(50)</c>, <c>decimal(18,2)</c> — because that is what the
    /// schema browser shows and what a user comparing the tree to their own script expects.
    /// <para>
    /// <c>max_length</c> is in <b>bytes</b>, so a Unicode type's declared length is half of it (an
    /// <c>nvarchar(50)</c> reports 100), and <c>-1</c> is the sentinel for <c>(max)</c>.
    /// </para>
    /// </summary>
    private static string FormatType(string typeName, short maxLength, byte precision, byte scale)
    {
        var name = typeName.ToLowerInvariant();
        switch (name)
        {
            case "char" or "varchar" or "binary" or "varbinary":
                return maxLength < 0 ? $"{name}(max)" : $"{name}({maxLength.ToString(CultureInfo.InvariantCulture)})";
            case "nchar" or "nvarchar":
                return maxLength < 0 ? $"{name}(max)" : $"{name}({(maxLength / 2).ToString(CultureInfo.InvariantCulture)})";
            case "decimal" or "numeric":
                return $"{name}({precision.ToString(CultureInfo.InvariantCulture)},{scale.ToString(CultureInfo.InvariantCulture)})";
            // The three types whose only modifier is fractional-second precision. Written even when it is
            // the default 7: the declared type is what is being reported, not the shortest way to spell it.
            case "datetime2" or "datetimeoffset" or "time":
                return $"{name}({scale.ToString(CultureInfo.InvariantCulture)})";
            default:
                // Everything else — int, bit, uniqueidentifier, xml, sql_variant, a CLR/alias type — has no
                // modifier worth rendering. float(n) is skipped deliberately: SQL Server stores it as one of
                // two precisions, so the number it reports is rarely the number that was declared.
                return name;
        }
    }

    // ---- On-demand detail (#46) and sizes (#76) --------------------------------------------------

    /// <summary>SQL Server's page size. Every size below is a page count from a catalog view, so this is
    /// the one place the multiplier lives.</summary>
    private const int PageBytes = 8192;

    /// <summary>
    /// Constraints, indexes and triggers of one relation, by <c>object_id</c>.
    /// <para>
    /// Where the Postgres reader has <c>pg_get_constraintdef</c> and <c>pg_get_indexdef</c> to render a
    /// definition for it, T-SQL has no equivalent for either: <c>object_definition</c> covers modules
    /// (triggers, procedures, check constraints) and returns null for a key constraint or an index. So the
    /// key-constraint and index definitions are <b>composed</b> here from the catalog, the way
    /// <c>TableDdlGenerator</c> composes a CREATE TABLE for the same reason.
    /// </para>
    /// <para>
    /// <see cref="ConstraintKind.Exclusion"/> never appears: SQL Server has no exclusion constraints. A
    /// foreign key is reported so the tree can list it, even though the DDL generator already renders
    /// outgoing FKs from the snapshot.
    /// </para>
    /// </summary>
    public async Task<TableDetails> GetTableDetailsAsync(long tableId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var columns = await ColumnNamesAsync(conn, tableId, ct).ConfigureAwait(false);
        var indexes = await ReadIndexesAsync(conn, tableId, columns, ct).ConfigureAwait(false);
        var constraints = await ReadConstraintsAsync(conn, tableId, indexes, ct).ConfigureAwait(false);
        var triggers = await ReadTriggersAsync(conn, tableId, ct).ConfigureAwait(false);

        return new TableDetails(constraints, indexes, triggers);
    }

    /// <summary>Column id → name for one relation, so a composed definition can name its columns.</summary>
    private static async Task<Dictionary<int, string>> ColumnNamesAsync(
        SqlConnection conn, long tableId, CancellationToken ct)
    {
        const string sql = "select column_id, name from sys.columns where object_id = @id";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new DriverParameter("@id", (int)tableId));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var byId = new Dictionary<int, string>();
        while (await r.ReadAsync(ct).ConfigureAwait(false)) byId[r.GetInt32(0)] = r.GetString(1);
        return byId;
    }

    /// <summary>
    /// The relation's indexes, with a composed <c>CREATE INDEX</c> and a size from
    /// <c>sys.dm_db_partition_stats</c>. <c>index_id = 0</c> is the heap and is skipped — it is the absence
    /// of an index, not one; hypothetical (statistics-only) indexes are skipped for the same reason.
    /// <c>is_disabled</c> is the closest thing to Postgres' <c>indisvalid</c>: a disabled index still exists
    /// and is still not usable.
    /// </summary>
    private static async Task<List<IndexInfo>> ReadIndexesAsync(
        SqlConnection conn, long tableId, Dictionary<int, string> columns, CancellationToken ct)
    {
        const string sql = """
            select i.index_id, i.name, i.is_unique, i.is_primary_key, i.is_disabled,
                   i.is_unique_constraint, i.type_desc,
                   (select sum(ps.used_page_count) from sys.dm_db_partition_stats ps
                     where ps.object_id = i.object_id and ps.index_id = i.index_id) as used_pages
            from sys.indexes i
            where i.object_id = @id and i.index_id > 0 and i.is_hypothetical = 0
            order by i.is_primary_key desc, i.name
            """;
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new DriverParameter("@id", (int)tableId));

        var rows = new List<(int IndexId, string Name, bool Unique, bool Primary, bool Disabled,
                             bool ByConstraint, string TypeDesc, long? UsedPages)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                rows.Add((r.GetInt32(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetBoolean(2),
                          r.GetBoolean(3), r.GetBoolean(4), r.GetBoolean(5),
                          r.IsDBNull(6) ? "" : r.GetString(6),
                          r.IsDBNull(7) ? null : r.GetInt64(7)));
        }

        var keyColumns = await IndexColumnsAsync(conn, tableId, ct).ConfigureAwait(false);
        var indexes = new List<IndexInfo>(rows.Count);
        foreach (var row in rows)
        {
            var ordinals = keyColumns.TryGetValue(row.IndexId, out var cols) ? cols : new List<int>();
            indexes.Add(new IndexInfo(
                Id: row.IndexId,
                Name: row.Name,
                IsUnique: row.Unique,
                IsPrimary: row.Primary,
                IsValid: !row.Disabled,
                Ordinals: ordinals,
                Definition: ComposeIndexDefinition(row.Name, row.Unique, row.TypeDesc, ordinals, columns),
                BackedByConstraint: row.Primary || row.ByConstraint,
                SizeBytes: row.UsedPages is { } pages ? pages * PageBytes : null));
        }
        return indexes;
    }

    /// <summary>Index id → its key column ids, in key order. Included (non-key) columns are excluded: they
    /// are payload, not the index's shape, and listing them as ordinals would misreport what it is on.</summary>
    private static async Task<Dictionary<int, List<int>>> IndexColumnsAsync(
        SqlConnection conn, long tableId, CancellationToken ct)
    {
        const string sql = """
            select index_id, column_id
            from sys.index_columns
            where object_id = @id and is_included_column = 0
            order by index_id, key_ordinal
            """;
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new DriverParameter("@id", (int)tableId));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var byIndex = new Dictionary<int, List<int>>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var indexId = r.GetInt32(0);
            if (!byIndex.TryGetValue(indexId, out var list)) byIndex[indexId] = list = new List<int>();
            list.Add(r.GetInt32(1));
        }
        return byIndex;
    }

    /// <summary>A readable <c>CREATE INDEX</c>. Composed, because T-SQL has no <c>pg_get_indexdef</c>; for
    /// display and copy, like the rest of the generated DDL, so filtered and included columns are omitted
    /// rather than guessed at.</summary>
    private static string ComposeIndexDefinition(
        string name, bool unique, string typeDesc, IReadOnlyList<int> ordinals,
        Dictionary<int, string> columns)
    {
        if (name.Length == 0) return "";
        var names = ordinals
            .Select(o => columns.TryGetValue(o, out var n) ? SqlServerIdentifierQuote(n) : $"?{o}")
            .ToList();
        var clustered = typeDesc.Contains("CLUSTERED", StringComparison.OrdinalIgnoreCase)
                        && !typeDesc.Contains("NONCLUSTERED", StringComparison.OrdinalIgnoreCase)
            ? "clustered "
            : "";
        return $"create {(unique ? "unique " : "")}{clustered}index {SqlServerIdentifierQuote(name)} "
             + $"({string.Join(", ", names)})";
    }

    /// <summary>
    /// Key, check and foreign-key constraints. The key-constraint definition is composed from its index's
    /// columns (T-SQL renders none); a check constraint has a real <c>definition</c> column, which is the
    /// server's own text and so is used as-is.
    /// </summary>
    private static async Task<List<ConstraintInfo>> ReadConstraintsAsync(
        SqlConnection conn, long tableId, IReadOnlyList<IndexInfo> indexes, CancellationToken ct)
    {
        var list = new List<ConstraintInfo>();

        const string keySql = """
            select kc.object_id, kc.name, rtrim(kc.type), kc.unique_index_id
            from sys.key_constraints kc
            where kc.parent_object_id = @id
            order by kc.type desc, kc.name
            """;
        await using (var cmd = new SqlCommand(keySql, conn))
        {
            cmd.Parameters.Add(new DriverParameter("@id", (int)tableId));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var kind = r.GetString(2) == "PK" ? ConstraintKind.PrimaryKey : ConstraintKind.Unique;
                var indexId = r.IsDBNull(3) ? -1 : r.GetInt32(3);
                var backing = indexes.FirstOrDefault(i => i.Id == indexId);
                var ordinals = backing?.Ordinals ?? [];
                list.Add(new ConstraintInfo(
                    Id: r.GetInt32(0),
                    Name: r.GetString(1),
                    Kind: kind,
                    Ordinals: ordinals,
                    Definition: ComposeKeyConstraint(kind, backing)));
            }
        }

        const string checkSql = """
            select cc.object_id, cc.name, cc.definition, cc.parent_column_id
            from sys.check_constraints cc
            where cc.parent_object_id = @id
            order by cc.name
            """;
        await using (var cmd = new SqlCommand(checkSql, conn))
        {
            cmd.Parameters.Add(new DriverParameter("@id", (int)tableId));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                // parent_column_id is 0 for a table-level check; only a column-level one names a column.
                var column = r.GetInt32(3);
                list.Add(new ConstraintInfo(
                    Id: r.GetInt32(0),
                    Name: r.GetString(1),
                    Kind: ConstraintKind.Check,
                    Ordinals: column > 0 ? new[] { column } : [],
                    Definition: r.IsDBNull(2) ? "" : $"check {r.GetString(2)}"));
            }
        }

        const string fkSql = """
            select fk.object_id, fk.name
            from sys.foreign_keys fk
            where fk.parent_object_id = @id
            order by fk.name
            """;
        await using (var cmd = new SqlCommand(fkSql, conn))
        {
            cmd.Parameters.Add(new DriverParameter("@id", (int)tableId));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                // Ordinals and the referenced side are already in the snapshot's ForeignKeyInfo, which is
                // what the DDL generator renders from; this entry exists so the tree can list the
                // constraint by name. An empty definition keeps it out of the CREATE TABLE body, where the
                // snapshot's FK is rendered instead — see TableDdlGenerator.Inline.
                list.Add(new ConstraintInfo(
                    Id: r.GetInt32(0), Name: r.GetString(1), Kind: ConstraintKind.ForeignKey,
                    Ordinals: [], Definition: ""));
            }
        }

        return list;
    }

    private static string ComposeKeyConstraint(ConstraintKind kind, IndexInfo? backing)
    {
        if (backing is null) return "";
        var inner = backing.Definition;
        var open = inner.LastIndexOf('(');
        var cols = open >= 0 ? inner[open..] : "";
        return kind == ConstraintKind.PrimaryKey ? $"primary key {cols}".TrimEnd() : $"unique {cols}".TrimEnd();
    }

    /// <summary>Triggers on the relation. <c>object_definition</c> gives the real CREATE TRIGGER text — a
    /// module, unlike an index — and comes back null for an encrypted one, which reads as no definition
    /// rather than as an error.</summary>
    private static async Task<List<TriggerInfo>> ReadTriggersAsync(
        SqlConnection conn, long tableId, CancellationToken ct)
    {
        const string sql = """
            select t.object_id, t.name, t.is_disabled, object_definition(t.object_id)
            from sys.triggers t
            where t.parent_id = @id
            order by t.name
            """;
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new DriverParameter("@id", (int)tableId));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<TriggerInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new TriggerInfo(
                Id: r.GetInt32(0),
                Name: r.GetString(1),
                Enabled: !r.GetBoolean(2),
                Definition: r.IsDBNull(3) ? "" : r.GetString(3)));
        return list;
    }

    /// <summary>
    /// Every relation's size in the connected database, from <c>sys.dm_db_partition_stats</c> — one read
    /// for the whole database, as the interface requires.
    /// <para>
    /// The split onto Postgres' four numbers is the honest approximation, not an exact analogue.
    /// <c>index_id</c> 0 (heap) and 1 (clustered index) <em>are</em> the table, so their pages are
    /// <see cref="RelationSize.TableBytes"/>; 2 and above are separate indexes. LOB and row-overflow pages
    /// are reported as <see cref="RelationSize.ToastBytes"/>: SQL Server has no TOAST, but off-row storage
    /// for oversized values is the same idea and the same thing a reader wants to see separated.
    /// </para>
    /// <para>
    /// <c>used_page_count</c> rather than <c>reserved_page_count</c> for the parts, so the numbers add up to
    /// what is actually occupied; <see cref="RelationSize.TotalBytes"/> uses reserved, which is what the
    /// database is actually costing.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(CancellationToken ct)
    {
        const string sql = """
            select ps.object_id,
                   sum(ps.reserved_page_count) as reserved_pages,
                   sum(case when ps.index_id in (0, 1) then ps.in_row_data_page_count else 0 end) as table_pages,
                   sum(case when ps.index_id > 1 then ps.used_page_count else 0 end) as index_pages,
                   sum(ps.lob_used_page_count + ps.row_overflow_used_page_count) as offrow_pages,
                   sum(case when ps.index_id in (0, 1) then ps.row_count else 0 end) as rows_estimate
            from sys.dm_db_partition_stats ps
            join sys.objects o on o.object_id = ps.object_id
            where o.is_ms_shipped = 0
            group by ps.object_id
            """;
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var list = new List<RelationSize>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new RelationSize(
                TableId: r.GetInt32(0),
                TotalBytes: Pages(r, 1),
                TableBytes: Pages(r, 2),
                IndexBytes: Pages(r, 3),
                ToastBytes: Pages(r, 4),
                EstimatedRows: r.IsDBNull(5) ? null : r.GetInt64(5)));
        }
        return list;

        static long Pages(SqlDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i) * PageBytes;
    }

    /// <summary>
    /// Every database's size on the server, from <c>sys.master_files</c> — data and log files summed, which
    /// is the space the database occupies.
    /// <para>
    /// A size that cannot be read comes back null rather than throwing, as the interface requires:
    /// <c>sys.master_files</c> shows a row only for a database the login owns or can see (it needs
    /// CREATE DATABASE / ALTER ANY DATABASE / VIEW ANY DEFINITION), so on a least-privilege login most rows
    /// are simply absent. Listing those with a null size is the honest report — one invisible database must
    /// not cost the sizes of the rest.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(CancellationToken ct)
    {
        const string sql = """
            select d.name,
                   (select sum(cast(mf.size as bigint)) from sys.master_files mf
                     where mf.database_id = d.database_id) as pages
            from sys.databases d
            where d.state = 0
            order by d.name
            """;
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var list = new List<DatabaseSize>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new DatabaseSize(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetInt64(1) * PageBytes));
        return list;
    }

    /// <summary>Bracket-quote for a composed definition. The dialect owns this rule, but
    /// <c>Bearing.Data</c> does not reference <c>Bearing.Sql</c> (§2.2) — and the rule is two characters
    /// and an escape, so restating it here beats a lateral project edge.</summary>
    private static string SqlServerIdentifierQuote(string identifier)
        => "[" + identifier.Replace("]", "]]") + "]";

    /// <summary>
    /// <c>sys.objects.type</c> → the neutral kind. Only the two relation types are mapped, because SQL
    /// Server has only those two: an indexed view is still <c>V</c> (there is no materialized-view object
    /// type), there are no foreign tables, and a partitioned table is an ordinary <c>U</c> with a partition
    /// scheme on its index — so <see cref="RelationKind.MaterializedView"/>,
    /// <see cref="RelationKind.ForeignTable"/> and <see cref="RelationKind.Partitioned"/> are simply unused
    /// here rather than approximated onto something they are not.
    /// </summary>
    private static RelationKind MapObjectType(string type) => type switch
    {
        "V" => RelationKind.View,
        _ => RelationKind.Table,
    };

    /// <summary>
    /// <c>sys.objects.type</c> → the neutral routine kind. Inline (<c>IF</c>) and multi-statement
    /// (<c>TF</c>) table-valued functions are both functions here; the distinction is in their rendered
    /// return type. <see cref="RoutineKind.Window"/> has no counterpart — T-SQL's window functions are
    /// built into the language, not user objects.
    /// </summary>
    private static RoutineKind MapRoutineType(string type) => type switch
    {
        "P" => RoutineKind.Procedure,
        "AF" => RoutineKind.Aggregate,
        _ => RoutineKind.Function,
    };
}
