# Data Layer Rules (§5.x)

Raw ADO.NET — **no ORM**. Postgres / SQL Server are query targets; SQLite is local app state.

## §5.1 — Postgres (`Bearing.Data/Postgres`)
- Npgsql directly, behind `Core` provider interfaces.
- Column origin = table OID + attnum (`TableOID`/`ColumnAttributeNumber`); `BaseTableName` is null.
- Propagate `CancellationToken` through every async DB call.

## §5.1a — MySQL / MariaDB (`Bearing.Data/MySql`)
One provider (`mysql`) and one dialect for both engines. Differences are server facts, read from the handshake
version (`MySqlServerFlavor`) where they matter — never a user setting.
- `INSERT … RETURNING` only on MariaDB 10.5+; the executor picks `SqlWithoutReturning` up front elsewhere. No
  engine reads back an UPDATE.
- Ids are derived (`MySqlMetadataReader.StableId`, FNV over schema+name) and resolved back by re-hashing the
  table list. A database is the snapshot's one schema; column origin is by name with the database as catalog.
- A run of non-row statements comes back as one result with the batch total (measured; like SqlClient).
- A killed `SLEEP()`/`BENCHMARK()` *returns* (1 / 0) instead of failing — use a real query in timeout tests.
- `AllowUserVariables` on, `ConvertZeroDateTime` on, `AllowLoadLocalInfile`/`AllowPublicKeyRetrieval` reserved.
- `ExecuteWriteAsync` **prepares** each parameterized command (binary binding): the driver inlines unprepared
  values with escaping that ignores the server's `NO_BACKSLASH_ESCAPES`. `NoBackslashEscapes`/`IgnorePrepare` reserved.
- Reads are lossless: `TreatTinyAsBoolean` **off** (`tinyint(1)` holding 2 reads 2, not True) and `GuidFormat=None`
  (`char(36)` is text — a non-GUID value would fail the whole result). Both stay settable from the options bag.
- Session time zone is **not** supported (named zones need the server's tz tables).

## §5.1b — DynamoDB (`Bearing.Data/DynamoDb`)
Provider `dynamodb`, read through PartiQL `ExecuteStatement` over the AWS SDK for .NET — the one engine with no
connection, no password and no pool. Everything is behind `IDynamoDbApi` (the three calls Bearing makes:
`ExecuteStatement`, `ListTables`, `DescribeTable`), so the executor and reader are tested against a hand-rolled fake.
- Settings: region = `ConnectionInfo.Host` (required), AWS profile = `User` (blank = the SDK's default credential
  chain), `Options["Endpoint"]` for DynamoDB Local or a VPC endpoint. `https://` to any host; `http://` only for a
  loopback host (`localhost`, `127.0.0.0/8`, `::1`) — a refused setting fails the **connect** with its sentence, not
  the constructor. Loopback + blank profile signs with letters-only placeholder keys (DynamoDB Local rejects a hyphen
  in the key id); anything else is the SDK's. Secrets: §1.9c.
- Ids are derived, as MySQL's (§5.1a): a hash of the table name, re-hashed against the table list. One database,
  named after the region, with one schema named `""`; columns are the declared key attributes only (partition key
  ordinal 1, sort key 2; index-only keys follow, nullable), and `GetTableDetailsAsync` lists the indexes the
  snapshot's ordinals point at. `DescribeTable` is cached per session; a snapshot refresh forgets it.
- Attribute sample (`SampleAttributesAsync`, 2026-11-19): **only on a table's expand**, one `ExecuteStatement`
  with `Limit` 20 and no continuation, keys left out, rows marked `sampled` — a sample, never the schema. Refusals
  are typed (`AttributeSample.Refused`, scrubbed), only successes are cached until the next snapshot; it never feeds
  completion or the snapshot (the key ordinals drive the scan check). Other engines are **never asked**
  (`ProviderTraits.SamplesAttributes`, checked in the node and in `SchemaBrowser` before a reader is built — asking
  would connect, and a failure would hang "couldn't sample" under a table the catalog already described).
- **No `LIMIT`/`OFFSET`.** A read continues from DynamoDB's `NextToken`, carried opaquely in
  `QueryResult.Continuation` and handed back to `IQueryExecutor.ExecuteContinuationAsync`; `Truncated` means "a
  token remains", never a row count. The token holds statement text and a server token: **in memory only — never
  logged, persisted or shown** (§1.3), and decoding refuses anything but a token the class wrote.
- A page ends at the row cap, when DynamoDB sends no `NextToken`, or after 10 requests — whichever is first. A page
  cut short by the 10 says so in `Message`. Fetch all reads to the end with no request budget.
- **`Limit` only on a read without `WHERE`** (user decision 2026-11-19, PR #192 review; measured by
  `DynamoDbLiveTests.Limit_bounds_the_items_evaluated_not_the_items_returned`). `Limit` caps items *evaluated*, not
  returned. No `WHERE` (`PartiQlBatch.HasWhere` false): `Limit` = rows still wanted, the page is exact, and an
  unfiltered scan's first page reads about one page of items (§1.9c). Any `WHERE`: no `Limit`, each request evaluates
  up to 1 MB and returns every match, so a page can exceed the cap by one request's matches — **never drop them**,
  the token is past them. `ExecuteContinuationAsync` therefore promises *about* `maxRows`. Whoever must hold a cap
  trims and says truncated: `StreamRowsAsync` (exact), the `bearing` command's `--max-rows` (`BearingHost.Capped`,
  both export paths). The App shows the overshoot.
- Columns are fixed by the first page (§9.10): key attributes first, then the rest alphabetically — plus every name
  a plain projection lists (`PartiQlBatch.Projection`: one name per item, else null), so a filtered read whose first
  page is empty still gets its columns. `SELECT *` with an empty first page has nothing to name them from: key
  columns only, the rest in the overflow column (known limitation). A result that **continues** always ends with an
  `(other attributes)` JSON column — projections included — because a later page can bring an attribute the first
  did not show, or a value the first page's column type cannot hold; a result that fits one page never has it. A real
  attribute of that name pushes the overflow column to `(other attributes 2)` (`DynamoDbColumnLayout.OverflowName`).
- A continuation goes back only to the session that issued it: `ResultSetViewModel.SourceSession`, compared in
  `ContinuationPaging`; a set without one is never continued. Count is never offered on a token result
  (`ResultSetViewModel.CanCount`).
- Values: `S` text; `N` a decimal when every value in the column fits one exactly, else text; `BOOL`; `B` bytes;
  `M`, `L` and the sets as JSON text. A missing attribute and a DynamoDB `NULL` both read as null (a known
  limitation — keep them indistinguishable on purpose, never invent a difference).
- Never offered, and each answers "no" rather than an approximation: grid editing (`GridEditUnavailable`), manual
  commit and transactions (`SupportsManualCommit` false, `CreateTransactionScope` throws), counts and offset paging
  (`CountWrap`/`TryAppendPage` null), query plans, server activity, TLS modes, session time zone, `:name` variables,
  SSH tunnels (`SupportsSshTunnel` false: the SSH tab is hidden, and a tunnel a hand-edited project names is refused
  in `ConnectionFactoryBuilder.RouteAsync`, never routed).
  The timeout is a client-side cancel of the page read (the SQL Server arrangement, §1.9a).

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
