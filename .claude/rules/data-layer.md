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
