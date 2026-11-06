using System.Globalization;
using System.Linq;
using System.Text;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using MySqlConnector;

namespace Bearing.Data.MySql;

/// <summary>
/// Reads MySQL / MariaDB catalog metadata from <c>information_schema</c> — the one catalog both engines
/// share — into the neutral types. A database <em>is</em> a schema here: the snapshot has exactly one
/// schema, named after the connected database, and that is its search path.
/// <para>
/// <b>Ids are derived, not read.</b> <c>information_schema</c> exposes no object ids (MySQL 8 has internal
/// ones in its data dictionary, MariaDB none), and the neutral contract identifies a relation by a
/// <see cref="long"/>. So <see cref="StableId"/> hashes the (schema, name) pair — stable across reads, so a
/// detail request made long after the snapshot still finds its table — and the readers that take an id
/// resolve it back by hashing the current table list (<see cref="ResolveTableAsync"/>). A rename between
/// the snapshot and the request is reported as "not found", which is what it is.
/// </para>
/// </summary>
public sealed class MySqlMetadataReader : IMetadataReader
{
    private readonly MySqlConnectionFactory _factory;

    public MySqlMetadataReader(MySqlConnectionFactory factory) => _factory = factory;

    /// <summary>The four system schemas are the server's own book-keeping and stay out of the picker, as
    /// the other engines hide theirs.</summary>
    private const string SystemSchemas = "('information_schema', 'mysql', 'performance_schema', 'sys')";

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(
            $"select schema_name from information_schema.schemata where schema_name not in {SystemSchemas} order by schema_name",
            conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var names = new List<string>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            names.Add(r.GetString(0));
        }
        return names;
    }

    public async Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var schema = await CurrentDatabaseAsync(conn, ct).ConfigureAwait(false) ?? database;

        var tables = await ReadTablesAsync(conn, schema, ct).ConfigureAwait(false);
        var columns = await ReadColumnsAsync(conn, schema, ct).ConfigureAwait(false);
        var fks = await ReadForeignKeysAsync(conn, schema, columns, ct).ConfigureAwait(false);

        var schemas = new[] { schema };
        return new SchemaSnapshot(database, schemas, tables, columns, fks, searchPath: schemas);
    }

    private static async Task<string?> CurrentDatabaseAsync(MySqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("select database()", conn);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    /// <summary>A 63-bit FNV-1a hash of the qualified name, never 0 (0 means "no id" to the resolvers).
    /// Case-sensitive: whether <c>Orders</c> and <c>orders</c> are one table is the server's
    /// <c>lower_case_table_names</c>, and the catalog already reports each name as stored.</summary>
    internal static long StableId(string schema, string name, string kind = "")
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes($"{kind}\u0001{schema}\u0001{name}"))
        {
            hash ^= b;
            hash *= prime;
        }
        var id = (long)(hash & long.MaxValue);
        return id == 0 ? 1 : id;
    }

    private static async Task<List<TableInfo>> ReadTablesAsync(MySqlConnection conn, string schema, CancellationToken ct)
    {
        // MariaDB reports a sequence as table_type 'SEQUENCE'; it is listed under its own kind, not as a
        // relation. 'SYSTEM VIEW' only occurs in information_schema itself, which is never the schema here.
        const string sql = """
            select table_name, table_type
            from information_schema.tables
            where table_schema = database() and table_type in ('BASE TABLE', 'VIEW', 'SYSTEM VERSIONED')
            order by table_name
            """;
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<TableInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = r.GetString(0);
            var kind = r.GetString(1) == "VIEW" ? RelationKind.View : RelationKind.Table;
            list.Add(new TableInfo(StableId(schema, name), schema, name, kind));
        }
        return list;
    }

    private static async Task<List<ColumnInfo>> ReadColumnsAsync(MySqlConnection conn, string schema, CancellationToken ct)
    {
        // column_type is the declared type with its modifiers (varchar(255), int unsigned, enum('a','b')) —
        // what reads in DDL, rather than data_type's bare name.
        const string sql = """
            select table_name, ordinal_position, column_name, column_type, is_nullable, column_key
            from information_schema.columns
            where table_schema = database()
            order by table_name, ordinal_position
            """;
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ColumnInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ColumnInfo(
                TableId: StableId(schema, r.GetString(0)),
                Ordinal: Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture),
                Name: r.GetString(2),
                DataType: r.GetString(3),
                NotNull: r.GetString(4) == "NO",
                IsPrimaryKey: !r.IsDBNull(5) && r.GetString(5) == "PRI"));
        }
        return list;
    }

    private static async Task<List<ForeignKeyInfo>> ReadForeignKeysAsync(
        MySqlConnection conn, string schema, IReadOnlyList<ColumnInfo> columns, CancellationToken ct)
    {
        // One row per FK column, ordered by its position in the key so ParentOrdinals[i] pairs with
        // ReferencedOrdinals[i]. A key referencing another database is skipped: the snapshot holds one, and
        // an id for a table it does not contain would resolve to nothing at best.
        const string sql = """
            select constraint_name, table_name, column_name, referenced_table_name, referenced_column_name
            from information_schema.key_column_usage
            where table_schema = database()
              and referenced_table_name is not null
              and referenced_table_schema = database()
            order by table_name, constraint_name, ordinal_position
            """;
        var ordinal = columns.ToDictionary(c => (c.TableId, c.Name.ToLowerInvariant()), c => c.Ordinal);

        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var order = new List<(string Table, string Name)>();
        var byKey = new Dictionary<(string Table, string Name), (string Referenced, List<int> P, List<int> R)>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = (r.GetString(1), r.GetString(0));
            var referenced = r.GetString(3);
            if (!byKey.TryGetValue(key, out var fk))
            {
                fk = (referenced, new List<int>(), new List<int>());
                byKey[key] = fk;
                order.Add(key);
            }
            fk.P.Add(ordinal.GetValueOrDefault((StableId(schema, key.Item1), r.GetString(2).ToLowerInvariant())));
            fk.R.Add(ordinal.GetValueOrDefault((StableId(schema, referenced), r.GetString(4).ToLowerInvariant())));
        }

        return order
            .Where(k => byKey[k].P.All(o => o > 0) && byKey[k].R.All(o => o > 0))
            .Select(k => new ForeignKeyInfo(
                Id: StableId(schema, $"{k.Table}.{k.Name}", "fk"),
                Name: k.Name,
                ParentTableId: StableId(schema, k.Table),
                ParentOrdinals: byKey[k].P,
                ReferencedTableId: StableId(schema, byKey[k].Referenced),
                ReferencedOrdinals: byKey[k].R))
            .ToList();
    }

    public async Task<IReadOnlyList<RoutineInfo>> GetRoutinesAsync(CancellationToken ct)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var schema = await CurrentDatabaseAsync(conn, ct).ConfigureAwait(false) ?? "";

        var routines = new List<(string Name, string Type, string? Returns, string? Comment)>();
        await using (var cmd = new MySqlCommand(
            """
            select routine_name, routine_type, dtd_identifier, routine_comment
            from information_schema.routines
            where routine_schema = database()
            order by routine_name
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                routines.Add((r.GetString(0), r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) || r.GetString(3).Length == 0 ? null : r.GetString(3)));
            }
        }

        // ordinal_position 0 is a function's return value, not an argument.
        var args = new Dictionary<(string, string), List<string>>();
        await using (var cmd = new MySqlCommand(
            """
            select specific_name, routine_type, parameter_mode, parameter_name, dtd_identifier
            from information_schema.parameters
            where specific_schema = database() and ordinal_position > 0
            order by specific_name, ordinal_position
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var key = (r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1));
                if (!args.TryGetValue(key, out var list))
                {
                    args[key] = list = new List<string>();
                }
                var mode = r.IsDBNull(2) ? "" : r.GetString(2);
                var text = $"{(r.IsDBNull(3) ? "" : r.GetString(3))} {(r.IsDBNull(4) ? "" : r.GetString(4))}".Trim();
                list.Add(mode is "" or "IN" ? text : $"{mode.ToLowerInvariant()} {text}");
            }
        }

        return routines
            .Select(x => new RoutineInfo(
                Id: StableId(schema, x.Name, x.Type),
                Schema: schema,
                Name: x.Name,
                Kind: x.Type == "PROCEDURE" ? RoutineKind.Procedure : RoutineKind.Function,
                Arguments: args.TryGetValue((x.Name, x.Type), out var list) ? string.Join(", ", list) : "",
                ReturnType: x.Type == "PROCEDURE" ? "" : x.Returns ?? "",
                Comment: x.Comment))
            .ToList();
    }

    public async Task<string> GetViewDefinitionAsync(long tableId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        if (await ResolveTableAsync(conn, tableId, ct).ConfigureAwait(false) is not { } table)
        {
            return "";
        }
        return await ShowCreateAsync(conn, "view", table.Schema, table.Name, column: 1, ct).ConfigureAwait(false);
    }

    public async Task<string> GetRoutineDefinitionAsync(long routineId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var schema = await CurrentDatabaseAsync(conn, ct).ConfigureAwait(false) ?? "";
        await using var cmd = new MySqlCommand(
            "select routine_name, routine_type from information_schema.routines where routine_schema = database()", conn);
        string? name = null, type = null;
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                if (StableId(schema, r.GetString(0), r.GetString(1)) == routineId)
                {
                    name = r.GetString(0);
                    type = r.GetString(1);
                    break;
                }
            }
        }
        if (name is null || type is null)
        {
            return "";
        }
        // Column 2 is the body; it is NULL when the account may not see the routine's source, which is
        // reported as an empty definition rather than an error.
        return await ShowCreateAsync(conn, type.ToLowerInvariant(), schema, name, column: 2, ct).ConfigureAwait(false);
    }

    /// <summary><c>SHOW CREATE …</c> takes an identifier, not a parameter; both parts are quoted with
    /// backticks (doubling any embedded one), which is the whole of MySQL's identifier escaping.</summary>
    private static async Task<string> ShowCreateAsync(
        MySqlConnection conn, string what, string schema, string name, int column, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand($"show create {what} {Quote(schema)}.{Quote(name)}", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false) || r.FieldCount <= column || r.IsDBNull(column))
        {
            return "";
        }
        return r.GetString(column);
    }

    private static string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    /// <summary>A relation an id resolved to, with the table-level facts its detail view needs read off the
    /// same row (a view has no comment of its own here).</summary>
    private sealed record ResolvedTable(string Schema, string Name, string? Collation, string? Comment);

    /// <summary>The relation an id names in the connected database, or null when none does any more.</summary>
    private static async Task<ResolvedTable?> ResolveTableAsync(
        MySqlConnection conn, long tableId, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(
            """
            select table_schema, table_name, table_type, table_collation, table_comment
            from information_schema.tables where table_schema = database()
            """, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            if (StableId(r.GetString(0), r.GetString(1)) == tableId)
            {
                var isView = string.Equals(r.GetString(2), "VIEW", StringComparison.OrdinalIgnoreCase);
                return new ResolvedTable(
                    r.GetString(0),
                    r.GetString(1),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    isView || r.IsDBNull(4) || r.GetString(4).Length == 0 ? null : r.GetString(4));
            }
        }
        return null;
    }

    // ---- On-demand detail (#46) and sizes (#76) --------------------------------------------------

    public async Task<TableDetails> GetTableDetailsAsync(long tableId, CancellationToken ct)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        if (await ResolveTableAsync(conn, tableId, ct).ConfigureAwait(false) is not { } resolved)
        {
            return TableDetails.Empty;
        }

        var table = (resolved.Schema, resolved.Name);
        var (columns, details) = await ReadColumnDetailsAsync(conn, table.Name, ct).ConfigureAwait(false);
        var indexes = await ReadIndexesAsync(conn, table, columns, ct).ConfigureAwait(false);
        var constraints = await ReadConstraintsAsync(conn, table, indexes, ct).ConfigureAwait(false);
        var triggers = await ReadTriggersAsync(conn, table, ct).ConfigureAwait(false);

        // Collations only where they differ from the table's own (§9.9a).
        var shown = details
            .Select(d => d.Collation is { } c && string.Equals(c, resolved.Collation, StringComparison.OrdinalIgnoreCase)
                ? d with { Collation = null }
                : d)
            .ToList();

        return new TableDetails(constraints, indexes, triggers, []) { Columns = shown, Comment = resolved.Comment };
    }

    private static async Task<(Dictionary<string, int> Ordinals, List<ColumnDetail> Details)>
        ReadColumnDetailsAsync(MySqlConnection conn, string table, CancellationToken ct)
    {
        const string sql = """
            select ordinal_position, column_name, column_default, extra, generation_expression,
                   collation_name, column_comment
            from information_schema.columns
            where table_schema = database() and table_name = @t
            order by ordinal_position
            """;
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@t", table);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var details = new List<ColumnDetail>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var ordinal = Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture);
            ordinals[r.GetString(1)] = ordinal;
            var extra = r.IsDBNull(3) ? "" : r.GetString(3);
            var generated = r.IsDBNull(4) || r.GetString(4).Length == 0 ? null : r.GetString(4);
            // AUTO_INCREMENT is MySQL's identity: the server fills it unless a value is given, which is
            // exactly BY DEFAULT. Identity outranks a default, and a generated column reports no default.
            var identity = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase)
                ? ColumnIdentity.ByDefault
                : ColumnIdentity.None;
            var @default = generated is not null || identity != ColumnIdentity.None || r.IsDBNull(2) ? null : r.GetString(2);
            details.Add(new ColumnDetail(
                ordinal, @default, identity, generated,
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) || r.GetString(6).Length == 0 ? null : r.GetString(6)));
        }
        return (ordinals, details);
    }

    private static async Task<List<IndexInfo>> ReadIndexesAsync(
        MySqlConnection conn, (string Schema, string Name) table, Dictionary<string, int> columns, CancellationToken ct)
    {
        const string sql = """
            select index_name, non_unique, column_name, index_type, sub_part
            from information_schema.statistics
            where table_schema = database() and table_name = @t
            order by case when index_name = 'PRIMARY' then 0 else 1 end, index_name, seq_in_index
            """;
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@t", table.Name);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var order = new List<string>();
        var byName = new Dictionary<string, (bool Unique, string Type, List<int> Ordinals, List<string> Parts)>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = r.GetString(0);
            if (!byName.TryGetValue(name, out var index))
            {
                index = (Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture) == 0,
                         r.IsDBNull(3) ? "" : r.GetString(3), new List<int>(), new List<string>());
                byName[name] = index;
                order.Add(name);
            }
            // A functional index part (MySQL 8) has no column name.
            if (r.IsDBNull(2))
            {
                index.Parts.Add("(expression)");
                continue;
            }
            var column = r.GetString(2);
            if (columns.TryGetValue(column, out var ordinal))
            {
                index.Ordinals.Add(ordinal);
            }
            index.Parts.Add(r.IsDBNull(4) ? Quote(column) : $"{Quote(column)}({r.GetValue(4)})");
        }

        return order.Select(name =>
        {
            var x = byName[name];
            var primary = name == "PRIMARY";
            var kind = x.Type is "FULLTEXT" or "SPATIAL" ? x.Type.ToLowerInvariant() + " " : x.Unique && !primary ? "unique " : "";
            var definition = primary
                ? $"primary key ({string.Join(", ", x.Parts)})"
                : $"create {kind}index {Quote(name)} on {Quote(table.Name)} ({string.Join(", ", x.Parts)})";
            return new IndexInfo(
                Id: StableId(table.Schema, $"{table.Name}.{name}", "index"),
                Name: name,
                IsUnique: x.Unique,
                IsPrimary: primary,
                IsValid: true,
                Ordinals: x.Ordinals,
                Definition: definition,
                // In MySQL a PRIMARY KEY or UNIQUE constraint *is* its index; there is no separate object.
                BackedByConstraint: primary || x.Unique);
        }).ToList();
    }

    private static async Task<List<ConstraintInfo>> ReadConstraintsAsync(
        MySqlConnection conn, (string Schema, string Name) table, IReadOnlyList<IndexInfo> indexes, CancellationToken ct)
    {
        var list = new List<ConstraintInfo>();
        var checks = await ReadCheckClausesAsync(conn, table.Name, ct).ConfigureAwait(false);

        const string sql = """
            select constraint_name, constraint_type
            from information_schema.table_constraints
            where table_schema = database() and table_name = @t
            order by case constraint_type when 'PRIMARY KEY' then 0 when 'UNIQUE' then 1 else 2 end, constraint_name
            """;
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@t", table.Name);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = r.GetString(0);
            var type = r.GetString(1);
            var id = StableId(table.Schema, $"{table.Name}.{name}", "constraint");
            switch (type)
            {
                case "PRIMARY KEY" or "UNIQUE":
                {
                    var backing = indexes.FirstOrDefault(i => i.Name == name);
                    var kind = type == "PRIMARY KEY" ? ConstraintKind.PrimaryKey : ConstraintKind.Unique;
                    list.Add(new ConstraintInfo(id, name, kind, backing?.Ordinals ?? [],
                        backing is null ? "" : (kind == ConstraintKind.PrimaryKey ? "primary key " : "unique ")
                                               + ColumnsOf(backing.Definition)));
                    break;
                }
                case "FOREIGN KEY":
                    // Rendered from the snapshot's ForeignKeyInfo, as on SQL Server — listed here by name only.
                    list.Add(new ConstraintInfo(id, name, ConstraintKind.ForeignKey, [], ""));
                    break;
                case "CHECK":
                    list.Add(new ConstraintInfo(id, name, ConstraintKind.Check, [],
                        checks.TryGetValue(name, out var clause) ? $"check ({clause})" : ""));
                    break;
            }
        }
        return list;
    }

    /// <summary>The parenthesised column list at the end of an index definition this reader composed.</summary>
    private static string ColumnsOf(string definition)
    {
        var at = definition.LastIndexOf(" (", StringComparison.Ordinal);
        return at >= 0 ? definition[(at + 1)..] : "";
    }

    /// <summary>
    /// CHECK clauses by constraint name. <c>information_schema.check_constraints</c> is MySQL 8.0.16+ and
    /// MariaDB 10.2+, and only MariaDB's carries the table name — so the join goes through
    /// <c>table_constraints</c>, which both have. Best-effort: on a server without the view the constraints
    /// are still listed, without their text.
    /// </summary>
    private static async Task<Dictionary<string, string>> ReadCheckClausesAsync(
        MySqlConnection conn, string table, CancellationToken ct)
    {
        var clauses = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await using var cmd = new MySqlCommand(
                """
                select cc.constraint_name, cc.check_clause
                from information_schema.check_constraints cc
                join information_schema.table_constraints tc
                  on tc.constraint_schema = cc.constraint_schema and tc.constraint_name = cc.constraint_name
                 and tc.constraint_type = 'CHECK'
                where cc.constraint_schema = database() and tc.table_name = @t
                """, conn);
            cmd.Parameters.AddWithValue("@t", table);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                clauses[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
            }
        }
        catch (MySqlException)
        {
            // An older server: no check_constraints view. The constraints are still listed below.
        }
        return clauses;
    }

    private static async Task<List<TriggerInfo>> ReadTriggersAsync(
        MySqlConnection conn, (string Schema, string Name) table, CancellationToken ct)
    {
        const string sql = """
            select trigger_name, action_timing, event_manipulation, action_statement
            from information_schema.triggers
            where trigger_schema = database() and event_object_table = @t
            order by trigger_name
            """;
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@t", table.Name);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<TriggerInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = r.GetString(0);
            // MySQL triggers cannot be disabled, so every one listed is enabled.
            list.Add(new TriggerInfo(
                StableId(table.Schema, $"{table.Name}.{name}", "trigger"), name, Enabled: true,
                Definition: $"create trigger {Quote(name)} {r.GetString(1).ToLowerInvariant()} "
                            + $"{r.GetString(2).ToLowerInvariant()} on {Quote(table.Name)} for each row "
                            + (r.IsDBNull(3) ? "" : r.GetString(3))));
        }
        return list;
    }

    /// <summary>
    /// Sizes from <c>information_schema.tables</c>: <c>data_length</c> + <c>index_length</c>. They are the
    /// storage engine's own estimates (InnoDB's refresh with its statistics), and <c>table_rows</c> is an
    /// estimate outright, which is what <see cref="RelationSize.EstimatedRows"/> means. A null length — a
    /// view, or a table whose engine cannot answer — is skipped rather than reported as 0 (§5.6).
    /// </summary>
    public async Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(CancellationToken ct)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(
            """
            select table_schema, table_name, data_length, index_length, table_rows
            from information_schema.tables
            where table_schema = database() and table_type in ('BASE TABLE', 'SYSTEM VERSIONED')
            """, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<RelationSize>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            if (r.IsDBNull(2))
            {
                continue;
            }
            var data = Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture);
            var index = r.IsDBNull(3) ? 0 : Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture);
            list.Add(new RelationSize(
                TableId: StableId(r.GetString(0), r.GetString(1)),
                TotalBytes: data + index,
                TableBytes: data,
                IndexBytes: index,
                ToastBytes: 0,
                EstimatedRows: r.IsDBNull(4) ? null : Convert.ToInt64(r.GetValue(4), CultureInfo.InvariantCulture)));
        }
        return list;
    }

    /// <summary>Each visible database's size as the sum of its tables'. A database with no table this
    /// account can see comes back null — "could not tell", since <c>information_schema</c> hides tables the
    /// account has no privilege on, and that is indistinguishable from having none.</summary>
    public async Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(CancellationToken ct)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(
            $"""
            select s.schema_name,
                   (select sum(t.data_length + coalesce(t.index_length, 0)) from information_schema.tables t
                     where t.table_schema = s.schema_name and t.data_length is not null) as bytes
            from information_schema.schemata s
            where s.schema_name not in {SystemSchemas}
            order by s.schema_name
            """, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<DatabaseSize>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new DatabaseSize(r.GetString(0),
                r.IsDBNull(1) ? null : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture)));
        }
        return list;
    }

    /// <summary>
    /// MariaDB's sequences (10.3+) — the one long-tail kind either engine has that the explorer models.
    /// MySQL has none, and on it the read finds no rows. Best-effort per kind (§9.9): a failure here is an
    /// empty list, not a failed tree.
    /// </summary>
    public async Task<DatabaseObjectKinds> GetDatabaseObjectsAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
            var schema = await CurrentDatabaseAsync(conn, ct).ConfigureAwait(false) ?? "";

            var names = new List<string>();
            await using (var cmd = new MySqlCommand(
                "select table_name from information_schema.tables where table_schema = database() and table_type = 'SEQUENCE' order by table_name",
                conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    names.Add(r.GetString(0));
                }
            }

            var sequences = new List<SequenceInfo>();
            foreach (var name in names)
            {
                // A MariaDB sequence is readable as a one-row table of its own settings. The last value
                // handed out is not among them (next_not_cached_value runs ahead by the cache), so it is
                // reported as unknown rather than approximated.
                await using var cmd = new MySqlCommand(
                    $"select minimum_value, maximum_value, increment, cycle_option from {Quote(schema)}.{Quote(name)}", conn);
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    continue;
                }
                sequences.Add(new SequenceInfo(
                    Id: StableId(schema, name, "sequence"),
                    Schema: schema,
                    Name: name,
                    DataType: "bigint",
                    LastValue: null,
                    Increment: Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture),
                    MinValue: Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    MaxValue: Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
                    Cycles: Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture) != 0,
                    OwnedBy: null));
            }
            return new DatabaseObjectKinds { Sequences = sequences };
        }
        catch (MySqlException)
        {
            return DatabaseObjectKinds.Empty;
        }
    }

    /// <summary>
    /// Not read. MySQL's accounts live in <c>mysql.user</c> (MariaDB: <c>mysql.global_priv</c>), both of
    /// which carry the password hash in the same row (§1.8), and there is no hash-free view equivalent to
    /// <c>pg_roles</c> that every account may read. An empty list says "nothing shown", as on SQL Server.
    /// </summary>
    public Task<IReadOnlyList<RoleInfo>> GetRolesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RoleInfo>>([]);

    public Task<RoleGrants> GetRoleGrantsAsync(string roleName, CancellationToken ct)
        => Task.FromResult(RoleGrants.NotVisible);

    public Task<IReadOnlyList<SchemaObjectInfo>> GetTablespacesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SchemaObjectInfo>>([]);
}
