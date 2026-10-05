# Data Layer Rules (§5.x)

Raw ADO.NET — **no ORM**. Postgres / SQL Server are query targets; SQLite is local app state.

## §5.1 — Postgres (`Bearing.Data/Postgres`)
- Npgsql directly, behind `Core` provider interfaces.
- Column origin = table OID + attnum (`TableOID`/`ColumnAttributeNumber`); `BaseTableName` is null.
- Propagate `CancellationToken` through every async DB call.

## §5.2 — SQLite (`Bearing.Persistence`)
Best-effort: persistence failures must never crash the app — surface in the status bar, swallow on
shutdown/save. Pruning uses `julianday()` + FTS `'rebuild'`.

## §5.3 — Connection lifecycle
Pooled by `ConnectionSessionManager` (`SessionKey`, §9.4a) and `SchemaBrowser`. Long-running reads take a
`SessionLease`. `MaxPoolSize` 10 per pool.

## §5.4 — Writes
Inline edits generate **parameterized** DML (`ResultEditModel`) run as one transaction via
`ExecuteWriteAsync`. SQL-preview inlining is display-only.

### §5.4a — Server expressions in cells (#149)
The one non-parameter value: `Core.Data.SqlExpression` (`now()`, `default`, …).
- `ResultEditModel.ExpressionFor` says yes only for a non-`string` column **and** after `Coerce` failed.
- Emit the dialect's canonical spelling, never the user's text. Per-engine tables via
  `ISqlDialect.TryEditExpression` (`EditExpression` / `TSqlEditExpression`) — no translation (`now()` on
  SQL Server stays refused). Pass the dialect everywhere; never default one.
- `DmlGenerator.BuildWhere` throws on an expression in a key predicate.
- Menu (`OfferedEditExpressions`) is a subset of what's accepted, indexing the same table; it calls
  `SetCell` like typing does. Text columns refuse it.
- Argument-less expressions only.

### §5.4b — UPDATE reads back the shown columns
- `returning` / `output inserted.…` lists named base columns, never `*`. Placement is
  `ISqlDialect.UpdateStatement`'s call.
- `ApplyReturnedRow` matches base name, then display name, keeps local values for anything not returned.
- SQL Server Msg 334 (trigger + `OUTPUT`): `SqlWithoutReturning` is filled whenever a clause was added; lose
  the refill, never the save.

## §5.5 — Temporal mappings (Npgsql 10, pinned by `TemporalMappingTests`)
| Postgres | .NET |
|---|---|
| `timestamptz` | `DateTime`, `Kind = Utc` |
| `timestamp` | `DateTime`, `Kind = Unspecified` |
| `timetz` | `DateTimeOffset` |
| `date` / `time` / `interval` | `DateOnly` / `TimeOnly` / `TimeSpan` |

- `CellFormat` branches on `Kind`: Utc → `CellFormat.Zone` with offset; Unspecified as-is, no offset (header
  badge `ColumnKinds.IsTimestampWithoutZone`, never in cell text).
- Keep the edit round trip (`TryParseDate`'s `utcColumn`).

## §5.5a — Session time zone rides the startup packet (#163)
- `ConnectionInfo.SessionTimeZone`: null = this machine's zone, `server` = send nothing, else a zone id.
  Legacy bag `Timezone` wins while null; the key is reserved.
- Sent as Npgsql `Timezone` (startup packet, never `SET`). Always IANA (`ZoneFor`, Windows ids mapped with
  the machine's region via `FromWindowsId`); a zone with no IANA name sends nothing.
- Part of `SamePool`/`SameConnection` (resolved zone), not `SameNetwork`. Named in the status tip where
  `IDbProvider.SupportsSessionTimeZone`; hidden on SQL Server.
- `SqlValue.Literal` writes `timestamptz` with `+00:00`. Live tests: pin offsets in timestamp literals.

## §5.6 — Size reads tolerate vanished relations
A null from `pg_total_relation_size` and similar is skipped, not reported as 0. WHEN calling size/location
functions, decide what absence means before `GetInt64` does.

## §5.7 — Streamed batches
- Every `RowBatch` carries `Columns` (positional). A row-returning stream always yields a tail batch, even
  when empty; a no-result statement yields nothing.
- `TableFormats.WriteCsvHeader` / `WriteCsvRow` are the one CSV rendering (byte-identical streamed vs
  batch, `StreamedCsvExportTests`). Fakes fill-and-yield like the real executors.
- xlsx has no streaming form.
