using Npgsql;
using Squirrel.Core.Data;
using Squirrel.Core.Schema;

namespace Squirrel.Data.Postgres;

/// <summary>
/// Reads PostgreSQL catalog metadata. M2 implements database listing; the full schema snapshot
/// (tables/columns/FKs from pg_catalog) lands in M3 when live completion is wired.
/// </summary>
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

    public Task<ISchemaSnapshot> LoadSnapshotAsync(string database, CancellationToken ct)
        => throw new NotImplementedException("Schema snapshot loading arrives in M3.");
}
