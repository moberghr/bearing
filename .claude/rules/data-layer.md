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

### §5.4a — The one value that is not a parameter, and the two conditions that bound it (#149)
A cell may stand for a server-side expression — `now()`, `default`, `gen_random_uuid()` — carried as a
`Core.Data.SqlExpression` and emitted by `DmlGenerator` where its parameter would have gone. It is the only
break in the rule above, and what keeps it bounded is **where the decision is made**, not the emitting:

- `ResultEditModel.ExpressionFor` says yes only when the column is one the driver does **not** map to
  `string` **and** `Coerce` already failed on the text. That set is exactly what used to be drawn amber — a
  value that cannot be written at all today — so this can never change the meaning of an edit that works. A
  `text` cell holding `now()` is a value, because `Coerce` accepts it, and is never reinterpreted.
- The emitted SQL is the **dialect's** own canonical spelling, never the user's string, so nothing typed is
  interpolated into a statement. Nothing in either table takes an argument — which is what stops one
  carrying a subquery or a second statement, and is asserted rather than assumed.
- **The table is per engine, and is not a translation.** `EditExpression` (Postgres) and
  `TSqlEditExpression` (SQL Server) are reached through `ISqlDialect.TryEditExpression`, the shape
  `WriteGuard`/`TSqlWriteGuard` already has: separate tables, one mechanism (`EditExpression.Lookup` owns
  the normalization, because normalization decides which strings can reach a statement at all). `now()`
  typed on a SQL Server connection stays **refused and amber** rather than becoming `getdate()` — the
  statement that runs has to be the one the cell shows. Only `default`, `current_timestamp`,
  `current_user` and `session_user` are common to both, and only because T-SQL genuinely spells them that
  way. Asking the wrong table is visible twice: the cell claims the server will evaluate something, and
  the save emits SQL that engine cannot run — which is why `ExpressionFor`, `WillReachServerAsText` and
  `ResultCellFactory` all take the dialect rather than defaulting to one.
- A **key predicate refuses one outright** (`DmlGenerator.BuildWhere` throws). Keys come from the row's
  stored originals, so it is an invariant check — but SQL in a `WHERE` re-aims the write at rows nobody
  picked, which is the one failure worth stating rather than trusting.
- **The menu offers a subset of what a cell accepts** (`ISqlDialect.OfferedEditExpressions` → the results
  grid's `Set value ▸`, beside NULL), and it is the **connection's** dialect that supplies it: `now()` on
  Postgres, `getdate()` on SQL Server, out of the same table the save will judge the cell against. Offering
  is a stronger claim than accepting: a cell takes anything in the table because the user typed it and meant
  it, while a menu item says "this works here". So `uuid_generate_v4()` is typeable but not offered — it
  needs the `uuid-ossp` extension, and claiming it works on a stock server is ours to get wrong — and the
  `now()` near-duplicates (`clock_timestamp()`, `statement_timestamp()`, `localtimestamp`, …) are left out
  because they differ in ways a menu cannot explain. T-SQL draws the same line in its own vocabulary:
  `sysdatetime()` / `sysutcdatetime()` / `getutcdate()` stay typeable, while `sysdatetimeoffset()` is
  offered because carrying the offset is a different answer rather than a finer one. Each list indexes its
  own table rather than restating it, so a spelling cannot drift between what the menu writes and what the
  recogniser reads.
- **The menu has no write path of its own.** It calls `SetCell` with the canonical text, exactly as typing
  it would, so the cell's colour, the generated SQL and the read-back all follow from the one decision in
  `ExpressionFor`. A cell in a **text** column refuses the menu and is counted in the status line — there
  the text would be stored as characters, which is a legitimate value and not what the item promised.
- WHEN extending the list, add argument-less expressions only. Anything taking an argument — `nextval('s')`
  — or referencing a column — `amount * 1.1` — is a different feature and wants an explicit mark from the
  user, not a wider allowlist.

### §5.4b — An UPDATE reads back the columns the grid shows
`DmlGenerator.Update` takes a `returning` list, and `ResultEditModel` passes the edit target's base columns.
An expression's value exists only on the server, so without it the grid keeps displaying the text that was
typed — and the same was already quietly true of a trigger's rewrite and an `on update` default.
- **Named columns, not `returning *`.** Postgres grants privileges per column: a `*` would make a save fail
  on a table the user may update but not read in full. These columns are the ones already on screen, so
  `SELECT` on them is proven.
- `ApplyReturnedRow` matches on the **base** column name and falls back to the displayed one, then keeps the
  locally committed value for anything the statement did not return. Matching displayed names alone breaks
  `select note as n` — and the version of this that built a fresh row wrote nulls over what had just saved.
- **Where the clause goes is the dialect's call** (`ISqlDialect.UpdateStatement`), like the INSERT's before
  it: Postgres ends with `returning c1, c2`, T-SQL puts `output inserted.c1, inserted.c2` **between the SET
  list and the WHERE**, where a trailing one is a syntax error. `inserted` is the new row for an UPDATE;
  `deleted` is the old one.
- **The read-back has a retry path on SQL Server, and needs one.** Msg 334 refuses `OUTPUT` without `INTO`
  on a table with an enabled trigger for an UPDATE exactly as for an INSERT, so `Update` fills
  `SqlWriteCommand.SqlWithoutReturning` whenever it added a clause — and null when it did not, which is what
  tells the executor there is nothing to retry with. An audited table must lose the refill, never the save.

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

## §5.5a — The session time zone is the client's, and rides the startup packet (#163)
A Postgres session that is sent no `TimeZone` keeps the server's — UTC on RDS — while pgJDBC sends the
client's, so DBeaver and Bearing returned different numbers for the same query whenever it depended on the
zone: `timestamptz::timestamp`, `::date`, `date_trunc('day', …)`, `timestamptz - timestamp`. The display
zone (§5.5, #77) could not help: it renders a value the server already computed, and the grid could show
local time above SQL that ran in UTC.

- `ConnectionInfo.SessionTimeZone`: **null is this machine's zone**, `server` sends nothing, anything else is
  a zone id. Null-means-local moved every existing connection on upgrade, and was chosen to: parity with
  other clients is the fix. A legacy `Timezone` in the options bag still wins while the field is null
  (`SessionTimeZonePolicy.Setting`, the `sslmode` precedence), and the bag key is **reserved** in
  `PostgresConnectionString` so it cannot be applied twice.
- **Sent as Npgsql's `Timezone`**, i.e. the startup packet's own `TimeZone` parameter — every pooled socket,
  for §1.9's reason. Not a `SET`.
- **IANA, always.** On Windows `TimeZoneInfo.Local.Id` is `Central Europe Standard Time`, which Postgres
  refuses at startup, so the connection would not open at all. `ZoneFor` converts a Windows id — and leaves an
  id alone when it is *also* IANA (`UTC` is both; converting sends `Etc/UTC`). A local zone with no IANA name
  (.NET's `"Local"`) sends nothing rather than a fixed offset, and `Describe` says so. Anything else is passed
  through: Postgres knows spellings .NET does not, and a typo fails the connect with the server's own error,
  which is visible — computing in another zone would not be.
- It is part of the pool (`SamePool`, `SameConnection`), compared as the **resolved** zone. It is not part of
  `SameNetwork`: the catalog does not depend on it.
- **Named in the status tip always** where the engine has one (`IDbProvider.SupportsSessionTimeZone`), not
  only when set — the #163 mismatch was invisible because nothing ever named the zone. SQL Server has no
  session zone, so the dialog hides the group there rather than offer a setting that does nothing.
- **A `timestamptz` rendered as SQL text carries `+00:00`** (`SqlValue.Literal`, `Kind = Utc`). Copy as SQL,
  the SQL export and the FK lookup build literals as text, and an offset-less one is read in the session's
  zone — harmless while every session was UTC, two hours off from Zagreb once it was not. Found by review,
  not by the suite. A `timestamp` (`Unspecified`) stays offset-less: it has no zone to state.
- **A Windows id is mapped with the machine's region** (`FromWindowsId`). "Central European Standard Time"
  is Warsaw by default and Zagreb for HR; they agree today and not before 1983, so the region-less mapping
  moves `::date` on old rows away from what DBeaver computes.
- A live test that writes a bare timestamp literal now reads it in the test machine's zone. `valid until
  '2030-01-01'` came back as 2029 in UTC on a Zagreb machine; pin an offset in the literal.

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

## §5.7 — A streamed batch names its columns, and a row-returning stream always yields one
`RowBatch` carries `Columns` **positionally**, and every batch carries them rather than only the first
(`IQueryExecutor.StreamRowsAsync`). It is really a property of the stream, but an async iterator has nowhere
else to put one, and "set on the first batch only" is the kind of implicit rule a consumer gets wrong once
and then works around forever. Positional so a provider cannot quietly omit it: the consumer that needs it —
a CSV written straight off the stream, with no result on screen to take headers from — would otherwise get an
empty header row and no error. The app never noticed the gap because it streams only to *append* to a grid it
already built.

The tail batch is **unconditional**. It was emitted only when it held rows or when the cap had stopped the
read, which meant a query matching nothing yielded no batches at all and a file built from the stream had no
header — a CSV `read_csv` cannot parse, where every other export path writes one. A statement with no result
shape (an UPDATE, DDL) still yields nothing, so "no batches" means *nothing to stream* rather than *no rows*,
and `ResultExport.WriteCsvStreamAsync` moves nothing into place for it.

- `TableFormats.WriteCsvHeader` / `WriteCsvRow` are the one rendering. The batch writer (`TableFormats.Csv`)
  and the streaming one both go through them, and `StreamedCsvExportTests` asserts the two files are
  **byte-identical** on the same rows rather than asserting what CSV looks like — two renderings of one
  format is how a quoting rule, a line ending or a BOM drifts between "exported from the app" and "exported
  from the command".
- **The fakes follow the real loop, not arithmetic over a row list.** `DemoProvider` and the app's
  `PageableExecutor` were rewritten to fill-and-yield the way the executors do, so an exact multiple of the
  batch size ends with an empty batch there as it does against a server (§4.6 — a fixture that behaves
  unlike the thing it stands in for lets a consumer pass here and break live).
- xlsx has no streaming form: `XlsxWriter` needs every row to build the sheet. That is a stated limit of the
  export, not an omission to be fixed by buffering somewhere else.
