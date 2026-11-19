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

    /// <summary>The reason a sample gives for a table DynamoDB says is not there.</summary>
    internal const string SampleTableGone = "The table no longer exists.";

    private readonly DynamoDbConnectionFactory _factory;
    private readonly ConcurrentDictionary<long, string> _names = new();

    // Samples that were read, per table name, until the next snapshot (a refresh). A refused or failed one is
    // never kept: the next expand asks again.
    private readonly ConcurrentDictionary<string, AttributeSample> _samples = new(StringComparer.Ordinal);

    public DynamoDbMetadataReader(DynamoDbConnectionFactory factory) => _factory = factory;

    public Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>([_factory.Region]);

    public async Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct)
    {
        // A snapshot is a refresh: what this session learned about its tables is read again.
        _factory.ForgetTables();
        _samples.Clear();

        var names = await ListTableNamesAsync(ct).ConfigureAwait(false);
        var described = await DescribeAllAsync(names, ct).ConfigureAwait(false);

        // A table whose description failed for a reason that may pass (a throttle, the network, a service
        // error) fails the whole read rather than be kept without its keys: the session caches the snapshot, and
        // a cached table with no partition key reads as "keys not loaded" for as long as the session lives (SF1).
        // Nothing is kept, and the next read tries again.
        if (TransientFailure(described) is { } failure) throw failure;

        var tables = new List<TableInfo>(names.Count);
        var columns = new List<ColumnInfo>();

        foreach (var name in names)
        {
            var outcome = described[name];

            // Dropped between the list and the describe: it is not a table any more.
            if (outcome.Kind == DescribeKind.Gone) continue;

            var id = StableId(name);
            tables.Add(new TableInfo(id, Schema, name, RelationKind.Table));

            // A policy that allows ListTables but refuses DescribeTable: the table is listed, its keys are not
            // known, and asking again would get the same answer — kept without columns, which the scan check
            // reads as "the partition key was not loaded".
            if (outcome.Table is not { } table) continue;

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
            .Select(name => described[name].Table is { } table ? SizeOf(name, table) : null)
            .OfType<RelationSize>()
            .ToList();
    }

    /// <summary>
    /// One table's size entry from one <c>DescribeTable</c> (through the session's cache) — what the full-scan
    /// check reads for the item count it names, rather than describing every table in the region. Null when the
    /// table is gone or DynamoDB reported no size for it.
    /// </summary>
    public async Task<RelationSize?> GetRelationSizeAsync(long tableId, CancellationToken ct)
    {
        if (await ResolveAsync(tableId, ct).ConfigureAwait(false) is not { } name) return null;

        try
        {
            return SizeOf(name, await _factory.DescribeTableAsync(name, ct).ConfigureAwait(false));
        }
        catch (Exception e) when (DynamoDbErrors.IsResourceNotFound(e))
        {
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Rethrow(e);
            throw;
        }
    }

    /// <summary>The region's size as the sum of its tables' — or null, saying why, when a table's size is
    /// missing: a partial sum would read as the whole. A table dropped since the list is not one of them.</summary>
    public async Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(CancellationToken ct)
    {
        var names = await ListTableNamesAsync(ct).ConfigureAwait(false);
        var described = await DescribeAllAsync(names, ct).ConfigureAwait(false);
        var sizes = names
            .Where(name => described[name].Kind != DescribeKind.Gone)
            .Select(name => described[name].Table is { } table ? SizeOf(name, table) : null)
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

    /// <summary>
    /// The attributes found in at most <paramref name="items"/> of the table's items — <b>one</b>
    /// <c>ExecuteStatement</c> of <c>select * from "T"</c> with <c>Limit</c> set, no continuation, so the read is
    /// bounded by construction (which is why it is not put to the full-scan check). Read when the tree expands the
    /// table, never on connect, and not logged (a metadata read, like the others here).
    /// <para>
    /// The declared attributes (the keys the tree already lists) are left out; the rest are ordinal by name, each
    /// with the type its values had, or <c>mixed</c> when they differed. A refusal is typed (§1.1): a policy that
    /// refuses the read, a table that is gone, a failure that may pass and the connection's timeout each come back
    /// as <see cref="AttributeSample.Refused"/> with the scrubbed reason. Only a sign-in failure is thrown — as
    /// the sign-in sentence, as on every other read. A sample that was read is kept until the next snapshot; a
    /// refused one is not.
    /// </para>
    /// </summary>
    public async Task<AttributeSample?> SampleAttributesAsync(TableInfo table, int items, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (_samples.TryGetValue(table.Name, out var kept)) return kept;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_factory.StatementTimeoutSeconds > SessionPolicy.NoTimeout)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(_factory.StatementTimeoutSeconds));
        }

        HashSet<string> declared;

        try
        {
            var description = await _factory.DescribeTableAsync(table.Name, deadline.Token).ConfigureAwait(false);
            declared = DeclaredAttributes(description).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // A policy that refuses DescribeTable left the table in the tree with no key rows (see
            // LoadSnapshotAsync), so nothing is listed already and nothing is left out — the read still runs.
            if (deadline.IsCancellationRequested || !DynamoDbErrors.IsAccessDenied(e)) return Refusal(e, deadline);

            declared = [];
        }

        ExecuteStatementResponse response;

        try
        {
            response = await _factory.Api
                .ExecuteStatementAsync(
                    new ExecuteStatementRequest { Statement = $"select * from {Quote(table.Name)}", Limit = Math.Max(1, items) },
                    deadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            if (DynamoDbErrors.IsResourceNotFound(e)) _factory.ForgetTable(table.Name);

            return Refusal(e, deadline);
        }

        var found = Sampled(response.Items ?? [], declared);
        var sample = new AttributeSample(found, response.Items?.Count ?? 0);

        _samples[table.Name] = sample;

        return sample;
    }

    /// <summary>
    /// What a failed sample read comes back as: the timeout sentence when the connection's own clock fired, the
    /// gone sentence for <c>ResourceNotFoundException</c>, else DynamoDB's scrubbed reason. A sign-in failure is
    /// thrown as the sign-in sentence (<see cref="Rethrow"/>). The caller's own cancellation never reaches here.
    /// </summary>
    private AttributeSample Refusal(Exception e, CancellationTokenSource deadline)
    {
        if (deadline.IsCancellationRequested)
        {
            return AttributeSample.RefusedBecause(DynamoDbErrors.TimeoutSentence(_factory.StatementTimeoutSeconds));
        }

        if (DynamoDbErrors.IsSignInFailure(e))
        {
            Rethrow(e);
        }

        if (DynamoDbErrors.IsResourceNotFound(e)) return AttributeSample.RefusedBecause(SampleTableGone);

        return AttributeSample.RefusedBecause(
            DynamoDbErrors.Scrub(string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message));
    }

    /// <summary>
    /// Every attribute the items carry that is not in <paramref name="declared"/>, ordinal by name, with its type
    /// (<see cref="SampleTypeName"/>) — <see cref="MixedType"/> when two items disagree.
    /// </summary>
    internal static IReadOnlyList<SampledAttribute> Sampled(
        IEnumerable<IReadOnlyDictionary<string, AttributeValue>> items, IReadOnlySet<string> declared)
    {
        var types = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            foreach (var (name, value) in item)
            {
                if (declared.Contains(name)) continue;

                var type = SampleTypeName(value);
                types[name] = types.TryGetValue(name, out var seen) && seen != type ? MixedType : type;
            }
        }

        return types.Select(entry => new SampledAttribute(entry.Key, entry.Value)).ToList();
    }

    /// <summary>The type an attribute seen with values of more than one type is listed as.</summary>
    internal const string MixedType = "mixed";

    /// <summary>
    /// One value's DynamoDB type in words. Scalars first, then a non-empty collection, then an empty one — the
    /// same reading <see cref="DynamoDbValues"/> gives the SDK's value type (its class remarks).
    /// </summary>
    internal static string SampleTypeName(AttributeValue value)
    {
        if (value.S is not null) return "string";
        if (value.N is not null) return "number";
        if (value.BOOL is not null) return "boolean";
        if (value.NULL is true) return "null";
        if (value.B is not null) return "binary";

        if (value.M is { Count: > 0 }) return "map";
        if (value.L is { Count: > 0 }) return "list";
        if (value.SS is { Count: > 0 }) return "string set";
        if (value.NS is { Count: > 0 }) return "number set";
        if (value.BS is { Count: > 0 }) return "binary set";

        if (value.M is not null) return "map";
        if (value.L is not null) return "list";

        return "null";
    }

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

    /// <summary>
    /// A table's size entry, or null when any part of it was not reported — the table's bytes or any index's
    /// (SF4): a total missing an index would read as the whole, and the region's sum would add it up.
    /// <para>
    /// <b>An item count of 0 is not reported as a fact</b> (SF7). DynamoDB refreshes <c>ItemCount</c> about every
    /// six hours, so a new table full of items still says 0; the count is null ("not reported") unless it is a
    /// positive number.
    /// </para>
    /// </summary>
    internal static RelationSize? SizeOf(string name, TableDescription table)
    {
        if (table.TableSizeBytes is not { } bytes) return null;

        var indexSizes = (table.GlobalSecondaryIndexes ?? []).Select(i => i.IndexSizeBytes)
            .Concat((table.LocalSecondaryIndexes ?? []).Select(i => i.IndexSizeBytes))
            .ToList();
        if (indexSizes.Any(size => size is null)) return null;

        var indexBytes = indexSizes.Sum(size => size!.Value);

        return new RelationSize(
            TableId: StableId(name),
            TotalBytes: bytes + indexBytes,
            TableBytes: bytes,
            IndexBytes: indexBytes,
            ToastBytes: 0,
            EstimatedRows: table.ItemCount is > 0 ? table.ItemCount : null);
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

    /// <summary>How one table's <c>DescribeTable</c> went — kept typed, because "gone", "refused" and "failed
    /// for now" are different answers (§1.1).</summary>
    internal enum DescribeKind
    {
        Described,

        /// <summary><c>ResourceNotFoundException</c>: dropped since the list.</summary>
        Gone,

        /// <summary><c>AccessDeniedException</c>: a policy that allows <c>ListTables</c> refuses this.</summary>
        Refused,

        /// <summary>Anything else — a throttle, the network, a service error. May pass.</summary>
        Failed,
    }

    internal readonly record struct DescribeOutcome(DescribeKind Kind, TableDescription? Table, Exception? Error);

    /// <summary>
    /// Each table's description, at most <see cref="DescribeConcurrency"/> at once, through the factory's
    /// per-session cache, with how each went (<see cref="DescribeKind"/>). A sign-in failure fails the whole
    /// read, since every other table would fail the same way.
    /// </summary>
    private async Task<Dictionary<string, DescribeOutcome>> DescribeAllAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        var described = new ConcurrentDictionary<string, DescribeOutcome>(StringComparer.Ordinal);
        var options = new ParallelOptions { MaxDegreeOfParallelism = DescribeConcurrency, CancellationToken = ct };

        try
        {
            await Parallel.ForEachAsync(names.Distinct(StringComparer.Ordinal), options, async (name, token) =>
            {
                try
                {
                    var table = await _factory.DescribeTableAsync(name, token).ConfigureAwait(false);
                    described[name] = new DescribeOutcome(DescribeKind.Described, table, null);
                }
                catch (Exception e) when (!token.IsCancellationRequested && !DynamoDbErrors.IsSignInFailure(e))
                {
                    var kind = DynamoDbErrors.IsResourceNotFound(e) ? DescribeKind.Gone
                        : DynamoDbErrors.IsAccessDenied(e) ? DescribeKind.Refused
                        : DescribeKind.Failed;
                    described[name] = new DescribeOutcome(kind, null, e);
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
            .ToDictionary(
                name => name,
                name => described.GetValueOrDefault(name, new DescribeOutcome(DescribeKind.Failed, null, null)),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// The failure a snapshot read must not hide — the first table (by name) whose description failed for a
    /// reason that may pass — as one sentence naming it and DynamoDB's scrubbed reason; null when there is none.
    /// </summary>
    private static Exception? TransientFailure(Dictionary<string, DescribeOutcome> described)
    {
        var failed = described
            .Where(entry => entry.Value.Kind == DescribeKind.Failed)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToList();
        if (failed.Count == 0) return null;

        var (name, outcome) = (failed[0].Key, failed[0].Value);
        var reason = outcome.Error is { } error
            ? DynamoDbErrors.Scrub(string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message)
            : "no answer arrived";
        var others = failed.Count - 1;
        var more = others switch
        {
            0 => "",
            1 => " (and 1 more table could not be read either)",
            _ => $" (and {others} more tables could not be read either)",
        };

        return new DynamoDbScrubbedException(
            $"Keys not loaded for {Quote(name)}: {reason.TrimEnd('.')}{more}. "
            + "The table list was not kept; the next read tries again.",
            outcome.Error is { } e ? DynamoDbErrors.ErrorCodeOf(e) : null);
    }

    private async Task<string?> ResolveAsync(long tableId, CancellationToken ct)
    {
        if (_names.TryGetValue(tableId, out var known)) return known;

        await ListTableNamesAsync(ct).ConfigureAwait(false);

        return _names.TryGetValue(tableId, out var listed) ? listed : null;
    }

    /// <summary>Rethrow <paramref name="e"/> as the sign-in sentence when it is a sign-in failure, scrubbed when
    /// its message holds key material (<see cref="DynamoDbErrors.ForCaller"/>), else as itself with its stack
    /// intact.</summary>
    private void Rethrow(Exception e)
    {
        var surfaced = DynamoDbErrors.ForCaller(e, _factory.Profile);
        if (!ReferenceEquals(surfaced, e)) throw surfaced;

        ExceptionDispatchInfo.Capture(e).Throw();
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
