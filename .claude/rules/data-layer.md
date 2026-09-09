# Data Layer Rules (§5.x)

Raw ADO.NET — **no ORM**. Two data surfaces: Postgres (query targets) and SQLite (local app state).

## §5.1 — Postgres (`Bearing.Data/Postgres`)
- Access is via Npgsql directly (`NpgsqlConnection`, readers, `NpgsqlDbColumn`). Keep it behind the `Core`
  provider abstractions (`IDbProvider`, executor/metadata/completion interfaces).
- Column origin uses table OID + attnum (`TableOID`/`ColumnAttributeNumber` from the RowDescription) —
  `NpgsqlDbColumn.BaseTableName` is null here, so do not rely on it.
- Propagate `CancellationToken` through every async DB call (execution is cancelable via the Run/Esc path).

## §5.2 — SQLite (`Bearing.Persistence`)
- Local stores (query log, workspace/session, recent projects) use `Microsoft.Data.Sqlite`, best-effort:
  persistence failures must not crash the app (they surface in the status bar and are swallowed on the
  shutdown/save path).
- Query-log pruning uses `julianday()` (offset-safe) + FTS `'rebuild'`.

## §5.3 — Connection lifecycle
- Live connections are pooled/reused by `ConnectionSessionManager` (keyed by connection Id **+ database** —
  `SessionKey`, §9.4) and the `SchemaBrowser` (per conn+db reader cache). Long-running reads take a
  `SessionLease` so an idle sweep or evict cannot dispose the pool mid-query. Respect the lease when adding
  new DB operations. `MaxPoolSize` is capped at 10 per pool (`NpgsqlConnectionFactory`).

## §5.4 — Writes
- Inline edits generate parameterized DML (`ResultEditModel`) and run as one transactional batch via
  `ExecuteWriteAsync`. Keep values parameterized; the SQL-preview inlining path is display-only.

## §5.5 — Temporal mappings are the driver's choice, and load-bearing
Confirmed against **Npgsql 10** (`tests/Bearing.Data.Tests/TemporalMappingTests.cs` pins it live):

| Postgres | .NET | Notes |
|---|---|---|
| `timestamptz` | `DateTime`, `Kind = Utc` | a real instant; `Kind` is the discriminator (#77) |
| `timestamp` | `DateTime`, `Kind = Unspecified` | no zone, and never had one |
| `timetz` | `DateTimeOffset` | keeps its offset — the offset arm was never dead for this type |
| `date` / `time` / `interval` | `DateOnly` / `TimeOnly` / `TimeSpan` | |

- `CellFormat` branches on `Kind`: Utc converts to `CellFormat.Zone` and renders **with** the offset;
  Unspecified renders as-is **without** one, because printing `+00:00` beside a column of local wall times
  would invent information. The zone-less column is marked with a header badge instead
  (`ColumnKinds.IsTimestampWithoutZone`) — never in the cell text, which travels into the clipboard, the
  exports and the edit round-trip.
- WHEN touching the display path, keep the round trip: a UTC 15:00 shown as `18:00+03:00` and edited must
  write back 15:00 UTC. `CellFormat.TryParseDate`'s `utcColumn` flag is what makes an offset-less edit a wall
  time in the display zone rather than a UTC instant — the lenient reading would silently move the row.
- A driver major version that changed any row above would stop timestamps showing their zone with nothing
  else in the suite noticing, which is why the mapping has a test of its own rather than being assumed.

## §5.6 — A size read tolerates a relation that vanished under it
`pg_class` is read under the query's MVCC snapshot; `pg_total_relation_size` **stats files**, which is not.
So a relation someone else drops while `GetRelationSizesAsync` runs is still listed and has no size, and the
reader used to call `GetInt64` on that null and throw — losing every other relation's size to one concurrent
`DROP`. Such a row is skipped, not reported as 0: zero bytes says "this table is empty", and the tree row it
belongs to is about to disappear anyway. Sizes are already best-effort and arrive after the tree renders
(#76), so a missing entry costs a label rather than a feature.

WHEN adding a catalog read that calls a size or location function (`pg_total_relation_size`,
`pg_table_size`, `pg_tablespace_location`), assume the object can be gone by the time the function runs, and
decide what the absence means before `GetInt64` decides for you. Found by CI: this suite's own
schema-creating tests race the enumerating ones, and a shared runner lost a race one fast machine kept
winning for a whole session.
