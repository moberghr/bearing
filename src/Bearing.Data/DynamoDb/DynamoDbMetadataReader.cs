using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Amazon.DynamoDBv2.Model;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Data.MySql;

namespace Bearing.Data.DynamoDb;

/// <summary>
/// The schema tree's data for one region (<i>Behaviour 4</i>): <c>ListTables</c> (paged) and a
/// <c>DescribeTable</c> per table, at most <see cref="DescribeConcurrency"/> at a time.
/// <para>
/// <b>One database, named after the region, with one schema named <c>""</c></b> — DynamoDB has neither, and an
/// empty schema keeps every generated statement unqualified (<c>select * from "T"</c>).
/// </para>
/// <para>
/// <b>Columns are the declared attributes only</b>: the table's key attributes and its indexes' — DynamoDB
/// declares no others. The partition key is ordinal 1 and the sort key ordinal 2 by construction, both flagged
/// <see cref="ColumnInfo.IsPrimaryKey"/>; index-only key attributes follow, unflagged and nullable (an index is
/// sparse). <see cref="GetTableDetailsAsync"/> numbers them the same way, so an index's
/// <see cref="IndexInfo.Ordinals"/> point at the snapshot's columns.
/// </para>
/// <para>
/// <b>Ids are derived, not read</b> — the MySQL reader's approach (<see cref="MySqlMetadataReader.StableId"/>):
/// a hash of the table name, resolved back by re-hashing the table list. A table dropped since the snapshot is
/// "not found", which is what it is.
/// </para>
/// </summary>
public sealed class DynamoDbMetadataReader : IMetadataReader
{
    /// <summary>How many <c>DescribeTable</c> calls run at once — enough to load a few hundred tables in seconds,
    /// few enough not to trip the control plane's request rate.</summary>
    internal const int DescribeConcurrency = 8;

    /// <summary>The name of the primary key in <see cref="TableDetails.Indexes"/>.</summary>
    internal const string PrimaryKeyName = "PRIMARY";

    internal const string SizesIncompleteReason =
        "DynamoDB did not report a size for every table in this region, so there is no total to add up.";

    private const string Schema = "";

    private readonly DynamoDbConnectionFactory _factory;
    private readonly ConcurrentDictionary<long, string> _names = new();

    public DynamoDbMetadataReader(DynamoDbConnectionFactory factory) => _factory = factory;

    public Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>([_factory.Region]);

    public async Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct)
    {
        // A snapshot is a refresh: what this session learned about its tables is read again.
        _factory.ForgetTables();

        var names = await ListTableNamesAsync(ct).ConfigureAwait(false);
        var described = await DescribeAllAsync(names, ct).ConfigureAwait(false);

        var tables = new List<TableInfo>(names.Count);
        var columns = new List<ColumnInfo>();

        foreach (var name in names)
        {
            var id = StableId(name);
            tables.Add(new TableInfo(id, Schema, name, RelationKind.Table));

            if (described[name] is not { } table) continue;

            columns.AddRange(DeclaredAttributes(table)
                .Select((attribute, i) =>
                    new ColumnInfo(
                        TableId: id,
                        Ordinal: i + 1,
                        Name: attribute.Name,
                        DataType: attribute.Type,
                        NotNull: attribute.IsTableKey,
                        IsPrimaryKey: attribute.IsTableKey)));
        }

        string[] schemas = [Schema];

        return new SchemaSnapshot(database, schemas, tables, columns, [], searchPath: schemas);
    }

    public Task<IReadOnlyList<RoutineInfo>> GetRoutinesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RoutineInfo>>([]);

    public Task<string> GetViewDefinitionAsync(long tableId, CancellationToken ct)
        => Task.FromException<string>(new NotSupportedException("Amazon DynamoDB has no views."));

    public Task<string> GetRoutineDefinitionAsync(long routineId, CancellationToken ct)
        => Task.FromException<string>(new NotSupportedException("Amazon DynamoDB has no stored routines."));

    /// <summary>
    /// The table's indexes: the primary key, then each global and each local secondary index, with its partition
    /// and sort key (<see cref="IndexInfo.Ordinals"/>, in that order) and its projection. No constraints,
    /// triggers or policies — DynamoDB has none to report.
    /// </summary>
    public async Task<TableDetails> GetTableDetailsAsync(long tableId, CancellationToken ct)
    {
        if (await ResolveAsync(tableId, ct).ConfigureAwait(false) is not { } name) return TableDetails.Empty;

        TableDescription table;

        try
        {
            table = await _factory.DescribeTableAsync(name, ct).ConfigureAwait(false);
        }
        catch (ResourceNotFoundException)
        {
            // Dropped between the list and the read.
            return TableDetails.Empty;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Rethrow(e);
            throw;
        }

        return new TableDetails([], Indexes(table), [], []);
    }

    /// <summary>
    /// DynamoDB's own approximate sizes (<c>TableSizeBytes</c> and <c>ItemCount</c>, refreshed by the service
    /// about every six hours), plus the indexes' <c>IndexSizeBytes</c> as index bytes. A table whose size the
    /// service did not report is skipped rather than shown as 0 (§5.6).
    /// </summary>
    public async Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(CancellationToken ct)
    {
        var names = await ListTableNamesAsync(ct).ConfigureAwait(false);
        var described = await DescribeAllAsync(names, ct).ConfigureAwait(false);

        return names
            .Select(name => described[name] is { } table ? SizeOf(name, table) : null)
            .OfType<RelationSize>()
            .ToList();
    }

    /// <summary>The region's size as the sum of its tables' — or null, saying why, when a table's size is
    /// missing: a partial sum would read as the whole.</summary>
    public async Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(CancellationToken ct)
    {
        var names = await ListTableNamesAsync(ct).ConfigureAwait(false);
        var described = await DescribeAllAsync(names, ct).ConfigureAwait(false);
        var sizes = names
            .Select(name => described[name] is { } table ? SizeOf(name, table) : null)
            .ToList();

        return sizes.Any(size => size is null)
            ? [new DatabaseSize(_factory.Region, null, SizesIncompleteReason)]
            : [new DatabaseSize(_factory.Region, sizes.Sum(size => size!.TotalBytes))];
    }

    public Task<DatabaseObjectKinds> GetDatabaseObjectsAsync(CancellationToken ct)
        => Task.FromResult(DatabaseObjectKinds.Empty);

    /// <summary>DynamoDB has no roles: access is an IAM policy, which this reader does not read.</summary>
    public Task<IReadOnlyList<RoleInfo>> GetRolesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RoleInfo>>([]);

    public Task<RoleGrants> GetRoleGrantsAsync(string roleName, CancellationToken ct)
        => Task.FromResult(RoleGrants.NotVisible);

    public Task<IReadOnlyList<SchemaObjectInfo>> GetTablespacesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SchemaObjectInfo>>([]);

    // ---- shared with the executor ------------------------------------------------------------------

    /// <summary>The key attributes a key schema names, partition key (<c>HASH</c>) first, then sort key
    /// (<c>RANGE</c>).</summary>
    internal static IReadOnlyList<string> KeyAttributes(IEnumerable<KeySchemaElement>? keySchema)
    {
        var elements = keySchema?.Where(e => !string.IsNullOrEmpty(e.AttributeName)).ToList() ?? [];

        return elements
            .Where(e => e.KeyType?.Value == "HASH")
            .Concat(elements.Where(e => e.KeyType?.Value == "RANGE"))
            .Select(e => e.AttributeName)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A table id: the name hashed in an empty schema (see the class remarks).</summary>
    internal static long StableId(string tableName, string kind = "") => MySqlMetadataReader.StableId(Schema, tableName, kind);

    internal sealed record DeclaredAttribute(string Name, string Type, bool IsTableKey);

    /// <summary>
    /// The attributes a table declares, in column order: the table's partition and sort key, then each global
    /// and each local secondary index's key attributes not already listed. Position + 1 is the ordinal.
    /// </summary>
    internal static IReadOnlyList<DeclaredAttribute> DeclaredAttributes(TableDescription table)
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var definition in table.AttributeDefinitions ?? [])
        {
            if (!string.IsNullOrEmpty(definition.AttributeName))
            {
                types[definition.AttributeName] = TypeName(definition.AttributeType?.Value);
            }
        }

        var names = new List<string>();
        AddKeys(table.KeySchema);
        var tableKeys = names.Count;

        foreach (var index in table.GlobalSecondaryIndexes ?? []) AddKeys(index.KeySchema);
        foreach (var index in table.LocalSecondaryIndexes ?? []) AddKeys(index.KeySchema);

        return names
            .Select((name, i) => new DeclaredAttribute(name, types.GetValueOrDefault(name, "string"), i < tableKeys))
            .ToList();

        void AddKeys(IEnumerable<KeySchemaElement>? keySchema)
        {
            foreach (var name in KeyAttributes(keySchema))
            {
                if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
            }
        }
    }

    /// <summary>A key attribute's declared type, in the words the result grid uses for the same values.</summary>
    internal static string TypeName(string? scalarType) => scalarType switch
    {
        "S" => DynamoDbValues.TypeName(DynamoDbColumnType.Text),
        "N" => DynamoDbValues.TypeName(DynamoDbColumnType.Number),
        "B" => DynamoDbValues.TypeName(DynamoDbColumnType.Binary),
        null or "" => DynamoDbValues.TypeName(DynamoDbColumnType.Text),
        _ => scalarType,
    };

    /// <summary>The primary key, then each global, then each local secondary index.</summary>
    internal static IReadOnlyList<IndexInfo> Indexes(TableDescription table)
    {
        var attributes = DeclaredAttributes(table);
        var ordinals = attributes
            .Select((attribute, i) => (attribute.Name, Ordinal: i + 1))
            .ToDictionary(x => x.Name, x => x.Ordinal, StringComparer.Ordinal);
        var types = attributes.ToDictionary(a => a.Name, a => a.Type, StringComparer.Ordinal);
        var tableName = table.TableName ?? "";

        var indexes = new List<IndexInfo>
        {
            new(
                Id: StableId($"{tableName}.{PrimaryKeyName}", "index"),
                Name: PrimaryKeyName,
                IsUnique: true,
                IsPrimary: true,
                IsValid: true,
                Ordinals: OrdinalsOf(table.KeySchema),
                Definition: $"primary key ({KeyText(table.KeySchema)})",
                BackedByConstraint: true),
        };

        foreach (var index in table.GlobalSecondaryIndexes ?? [])
        {
            var status = index.IndexStatus?.Value;
            var active = string.IsNullOrEmpty(status) || status == "ACTIVE";

            indexes.Add(new IndexInfo(
                Id: StableId($"{tableName}.{index.IndexName}", "index"),
                Name: index.IndexName ?? "",
                IsUnique: false,
                IsPrimary: false,
                IsValid: active,
                Ordinals: OrdinalsOf(index.KeySchema),
                Definition: $"global secondary index {Quote(index.IndexName ?? "")} ({KeyText(index.KeySchema)}) "
                    + ProjectionText(index.Projection)
                    + (active ? "" : $" — {status!.ToLowerInvariant()}"),
                SizeBytes: index.IndexSizeBytes));
        }

        foreach (var index in table.LocalSecondaryIndexes ?? [])
        {
            indexes.Add(new IndexInfo(
                Id: StableId($"{tableName}.{index.IndexName}", "index"),
                Name: index.IndexName ?? "",
                IsUnique: false,
                IsPrimary: false,
                IsValid: true,
                Ordinals: OrdinalsOf(index.KeySchema),
                Definition: $"local secondary index {Quote(index.IndexName ?? "")} ({KeyText(index.KeySchema)}) "
                    + ProjectionText(index.Projection),
                SizeBytes: index.IndexSizeBytes));
        }

        return indexes;

        IReadOnlyList<int> OrdinalsOf(IEnumerable<KeySchemaElement>? keySchema)
            => KeyAttributes(keySchema)
                .Where(ordinals.ContainsKey)
                .Select(name => ordinals[name])
                .ToList();

        string KeyText(IEnumerable<KeySchemaElement>? keySchema)
        {
            var sortKeys = (keySchema ?? [])
                .Where(e => e.KeyType?.Value == "RANGE")
                .Select(e => e.AttributeName)
                .ToHashSet(StringComparer.Ordinal);

            return string.Join(", ", KeyAttributes(keySchema).Select(name =>
                $"{(sortKeys.Contains(name) ? "sort key" : "partition key")} {Quote(name)} {types.GetValueOrDefault(name, "string")}"));
        }
    }

    /// <summary>A projection in words: <c>projection ALL</c>, <c>projection KEYS_ONLY</c>, or
    /// <c>projection INCLUDE ("a", "b")</c>.</summary>
    internal static string ProjectionText(Projection? projection)
    {
        var type = projection?.ProjectionType?.Value;
        if (string.IsNullOrEmpty(type)) return "projection unknown";

        return projection!.NonKeyAttributes is { Count: > 0 } included
            ? $"projection {type} ({string.Join(", ", included.Select(Quote))})"
            : $"projection {type}";
    }

    // ---- reads -------------------------------------------------------------------------------------

    private static RelationSize? SizeOf(string name, TableDescription table)
    {
        if (table.TableSizeBytes is not { } bytes) return null;

        var indexBytes = (table.GlobalSecondaryIndexes ?? []).Sum(i => i.IndexSizeBytes ?? 0)
            + (table.LocalSecondaryIndexes ?? []).Sum(i => i.IndexSizeBytes ?? 0);

        return new RelationSize(
            TableId: StableId(name),
            TotalBytes: bytes + indexBytes,
            TableBytes: bytes,
            IndexBytes: indexBytes,
            ToastBytes: 0,
            EstimatedRows: table.ItemCount);
    }

    /// <summary>Every table in the region, following <c>LastEvaluatedTableName</c>. Remembers each name's id so
    /// a later detail request resolves without listing again.</summary>
    private async Task<IReadOnlyList<string>> ListTableNamesAsync(CancellationToken ct)
    {
        var names = new List<string>();
        string? start = null;

        try
        {
            do
            {
                var response = await _factory.Api
                    .ListTablesAsync(new ListTablesRequest { ExclusiveStartTableName = start }, ct)
                    .ConfigureAwait(false);

                names.AddRange(response.TableNames ?? []);
                start = string.IsNullOrEmpty(response.LastEvaluatedTableName) ? null : response.LastEvaluatedTableName;
            }
            while (start is not null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Rethrow(e);
            throw;
        }

        foreach (var name in names) _names[StableId(name)] = name;

        return names;
    }

    /// <summary>
    /// Each table's description, at most <see cref="DescribeConcurrency"/> at once, through the factory's
    /// per-session cache. A table that cannot be described (dropped since the list, or refused by a policy that
    /// allows <c>ListTables</c> but not <c>DescribeTable</c>) maps to null and keeps its row; a sign-in failure
    /// fails the whole read, since every other table would fail the same way.
    /// </summary>
    private async Task<Dictionary<string, TableDescription?>> DescribeAllAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        var described = new ConcurrentDictionary<string, TableDescription?>(StringComparer.Ordinal);
        var options = new ParallelOptions { MaxDegreeOfParallelism = DescribeConcurrency, CancellationToken = ct };

        try
        {
            await Parallel.ForEachAsync(names.Distinct(StringComparer.Ordinal), options, async (name, token) =>
            {
                try
                {
                    described[name] = await _factory.DescribeTableAsync(name, token).ConfigureAwait(false);
                }
                catch (Exception e) when (!token.IsCancellationRequested && !DynamoDbErrors.IsSignInFailure(e))
                {
                    described[name] = null;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Rethrow(e);
            throw;
        }

        return names
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, name => described.GetValueOrDefault(name), StringComparer.Ordinal);
    }

    private async Task<string?> ResolveAsync(long tableId, CancellationToken ct)
    {
        if (_names.TryGetValue(tableId, out var known)) return known;

        await ListTableNamesAsync(ct).ConfigureAwait(false);

        return _names.TryGetValue(tableId, out var listed) ? listed : null;
    }

    /// <summary>Rethrow <paramref name="e"/> as the sign-in sentence when it is a sign-in failure, else as itself
    /// with its stack intact.</summary>
    private void Rethrow(Exception e)
    {
        var translated = DynamoDbErrors.Translate(e, _factory.Profile);
        if (!ReferenceEquals(translated, e)) throw translated;

        ExceptionDispatchInfo.Capture(e).Throw();
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
