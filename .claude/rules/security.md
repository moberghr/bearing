# Security Rules (§1.x)

Local desktop tool that stores DB credentials and runs user SQL against real servers.
Shared checklist: `.claude/references/security-checklist.md`.

**Recurring principle (§1.1's rule):** never assert a cause, guarantee or arrangement nobody checked — in
code, UI text, docs or refusal messages. "Not visible", "refused" and "none" are different answers; keep
absences typed.

## §1.1 — Secrets
- NEVER log, print, or write passwords to disk outside `ISecretStore`. Only the three OS stores store
  anything (`SecretToolSecretStore`, `WindowsCredentialSecretStore`, `MacKeychainSecretStore`).
- No keychain → `NoSecretStore`: `SetPasswordAsync` throws `SecretStorageRefusedException`, reads return
  null, the password is held in memory for the session. **No on-disk fallback, and DO NOT add one.**
- Surface the posture (`SecretStorageSecure` in the status bar, the dialog's amber block). The warning says
  the keychain **couldn't be reached** and shows `ISecretStore.UnavailableReason` — never a guessed cause.

## §1.2 — Write guard
- `Bearing.Sql.WriteGuard` flags DML/DDL and data-modifying CTEs on `RequireWriteConfirmation` connections.
- Lexer-based and conservative. Widening is allowed; narrowing what counts as risky needs a test and an
  argument.

## §1.3 — Query log
- `SqliteQueryLog`, retention `QueryLogRetentionDays` (default 180, ≤0 = forever). No PII beyond the SQL.
- Literal redaction (`QueryLogRedactLiterals`, `SqlRedactor`) is **opt-in, default off** — never assume a
  log is sanitized. It runs in `Append`; a redactor that throws stores `(redaction failed)`, never the
  verbatim SQL. It reaches the store as `Func<string,string>` (`Persistence` may not reference `Sql`).
- Files (incl. `-wal`/`-shm`) are narrowed to the owner on start (`LocalFilePermissions.HardenDatabase`),
  best-effort. No encryption at rest (it would need a user-supplied key — a new design, see §1.1).

## §1.4 — Connections / TLS
- Transport security is `ConnectionInfo.Tls` (`TlsMode`); `sslmode` is **reserved** in
  `PostgresConnectionString` so the options bag can't outrank the field. `TlsPolicy` resolves it (legacy bag
  value only while the field is untouched); new non-loopback connections default to requiring encryption.
- `Require` encrypts but accepts any certificate — never describe it as verified; don't collapse modes to a
  bool. Never hardcode credentials or disable cert validation.

## §1.5 — Write confirmation row counts
`UpdateDeleteTarget.TryReduce` + `ExecutionViewModel.CountRowImpactsAsync`. Three refusals, each a bug if
removed:
- Never **connects** — only `Sessions.TryGet`. No live session → no numbers.
- Never **blocks** — `CountTimeout` 2 s / `CountBudget` 4 s via linked CTS; a miss is `RowImpact.Uncounted`.
- Never **guesses** — declines multi-table forms, data-modifying CTEs, placeholders, anything it can't
  flatten.
- Three distinguishable states: a number, **every row** (no `WHERE`), *could not count*. Multi-write batches
  carry `ImpactCaveat`.
- The count query is `select * from <target>` (with alias) wrapped by `CountAsync` — not `count(*)`.

## §1.5a
`TryReduce` declines a `WHERE` with nothing after it — `delete from t where` is not "every row".

## §1.6 — Query log schema and audit export (#113)
- Migrations are **stepwise and additive** (`MigrateToN`), never rebuild. Each runs under
  `BEGIN IMMEDIATE` and is idempotent per column (`HasColumn` before each `ALTER`). Current: v4 (§1.11d).
- `connection_id` is null on pre-v2 rows — reports say how many, never imply an id match. `environment` is
  stored at write time, never resolved later; `ResolveEnvironment` prefers recorded value, then id, never
  name.
- Writes-only filter re-lexes with `WriteGuard.HasRisk` at report time (in `App`, §2.2).
- Report notes travel with the file: xlsx "About" sheet; CSV sibling `<name>.about.txt`. **Never `#` lines
  in a CSV** — header stays on row 1.
- Connection filter matches recorded name OR id → current name.
- Day bounds use the local zone's offset for the picked day (`ReportPeriod`); inverted ranges are swapped.
- Everything that can fail in `ExportHistoryAsync` is inside its `try` (async void caller, §5.2).

## §1.7 — Roles (#120)
- Read `pg_roles`, never `pg_authid`; `RoleInfo` has no hash field.
- Grants are read-only — never generate `GRANT`/`REVOKE`. Render privileges as words, not `arwd`.
- `RoleGrants.Visible` distinguishes "refused" from "none". `ConnectionLimit == -1` is *unlimited*,
  `ValidUntil == null` is *no expiry*.

## §1.8 — Never select credential-bearing catalog columns
`pg_authid.rolpassword`, `pg_subscription.subconninfo`, `pg_foreign_server.srvoptions`,
`pg_user_mappings.umoptions` (user mappings are not read at all). WHEN adding a catalog read, check what it
holds before selecting `*` or an options bag.

## §1.9 — Read-only and statement timeout ride the startup packet (#99 / #105)
- `ConnectionInfo.ReadOnly` / `StatementTimeoutSeconds` are fields; `options` is **reserved** in
  `PostgresConnectionString`. `StartupOptionsFor` composes `-c default_transaction_read_only=on -c
  statement_timeout=<ms>`. **DO NOT replace with a `SET`** — it reaches one pooled socket, not all
  (`SessionPolicyTests` pins this).
- Read-only is **not a privilege boundary** (user can `SET` it off) — never describe it as one.
- Read-only connections **refuse** writes (`WriteRefusal`, before the guard prompt); the guard is not
  narrowed. Client refusal is the early half; the server is the enforcement.
- Grid editing: `EditabilityResolver.ResolveWithReason` reports read-only first. Read-only never touches the
  beacon (§9.3a) — show it in `StatusTip` / tab tooltip.
- These settings define a pool: `SameConnection` and `SamePool` compare them. `SameNetwork` must NOT (it
  governs tree/snapshot/import identity).
- Timeout clamped to `SessionPolicy.MaxTimeoutSeconds`.
- `57014` attribution (own Esc / our timeout / external cancel) lives in `Results/QueryErrorText`; `25006`
  names our setting only when it's ours. Every route carrying a result must carry its `ConnectionInfo`.
- `ConnectionClipboard` reflects over `ConnectionInfo` — new fields must round-trip.

### §1.9a — SQL Server has only half
- Timeout = `SqlConnectionStringBuilder.CommandTimeout`, applied after the options bag (client-side cancel).
- No server read-only exists; `WriteRefusal` is the whole guarantee. `IDbProvider.EnforcesReadOnlyOnServer`
  picks the dialog wording. A new engine must answer this flag (default `true`) before shipping.

## §1.10 — Manual commit (#131)
`ConnectionInfo.ManualCommit`: a **write** opens a transaction held until Commit/Rollback. `CommitPolicy`;
not part of `SamePool`/`SameConnection`.
- Opens on `StatementRisk.NamesAWrite`, **not** `IsRisky`. Once open, everything in that tab joins it. A
  dialect the guard can't read counts as a write. The write guard still confirms.
- Seam: `IDbProvider.CreateTransactionScope` → `ITransactionScope` with a pinned `IQueryExecutor`.
  `ExecuteWriteAsync` commits only what it opened. `ExecutorFor(tab, session)` is the only chooser — every
  user-SQL call site goes through it.
- EXPLAIN ANALYZE refused while a transaction is held; plan-only EXPLAIN runs on it. Metadata, schema and
  activity reads stay pooled.
- `CountAsync` is savepoint-protected and exempt from the abort rule / statement count; every failure
  unwinds using `CancellationToken.None`. Batch retries (Msg 334) unwind to `bearing_write_batch` first.
- Any failure marks the transaction `Aborted`; Commit refused (conservative on SQL Server — don't probe
  `XACT_STATE()` without re-deciding).
- Typed `BEGIN`/`COMMIT`/`ROLLBACK` refused on manual-commit connections only, via per-dialect
  `ISqlDialect.TransactionControl` (T-SQL `BEGIN`/`END` are blocks).
- Commit/Rollback refused while `tab.IsRunning`. Forced rollbacks cancel the statement first. Idle sweep
  skips running tabs, is not re-entrant, and activity is stamped at statement start and finish.
- Per tab (`TabTransactions`), each holding a `SessionLease`; capped at `CommitPolicy.MaxOpenPerPool`.
- Guard table:

  | Path | Behaviour |
  |---|---|
  | Switch tab connection/database | Refuse |
  | Disconnect, refresh server metadata | Ask and act (`TransactionGuard.ConfirmAsync`) |
  | Quit, close tab | Ask now, roll back later (`AskAsync` … `RollbackAsync`) — other prompts may still cancel |
  | Pool-rebuilding edit, manual commit turned off, connection/project/script deleted | Roll back and report |
  | Idle sweep | Roll back past threshold |

  Every route that removes a tab or disposes/retires/re-keys a session must decide what happens to an open
  transaction (`RollbackForTabsAsync`). A project *switch* parks tabs and is safe.
- Mode pill / `transaction.toggleMode`: session-only override in `CommitModes`, never written to
  `project.json`; applied only in `WorkspaceContext.EffectiveConnection`. `CommitModes` takes an id except
  `IsManualCommit`. Saving the connection clears the override. Switching to auto refused while any tab on
  that connection holds a transaction. Override marked with a dot, not italics.
- Clocks: `IdleTransactionWarnMinutes` (5, amber chip), `IdleTransactionRollbackMinutes` (15, toast); 0
  disables. Chip sits beside the beacon, never on it. Commit/Rollback have no default binding.
  `ConfirmDiscardTransactionDialog`'s "Keep it open" is both `IsCancel` and `IsDefault`.
- Query log v3: `transaction_id` on each statement; commit/rollback logged as entries from `TabTransactions`
  (in `finally`, failures included).

## §1.11 — External access (`bearing` CLI)
`ConnectionInfo.ExternalAccess` (default `None`) gates **discovery**, not access, and only for reads. The
boundary is "anything running as you" — never describe it as a sandbox.
- `ExternalAccessPolicy.ForExternalHost` is the only route to an exposed connection and forces three
  settings: `ReadOnly` on, `ManualCommit` off, a missing timeout filled with
  `SessionPolicy.PresetTimeoutSeconds` (a set one is kept). Everything else untouched;
  `ExternalAccessTests.Nothing_but_the_three_forced_settings…` pins the list.
- Exposure is shown on the row (`ServerNodeViewModel.Detail` "· bearing"). It travels in `project.json` but
  `ConnectionClipboard` **drops it**.
- No `ReadWrite` level — there is nobody to confirm a write to.
- `bearing` is the console exe; the GUI apphost is `bearing-app`. DO NOT rename back. Scripts naming the
  binary (`build/release.sh` uses `APP_EXE`) must follow.
- CLI is the front end; an MCP server, if wanted again, is another skin over `IBearingHost`.
- An external host inside the app must get its own `ConnectionSessionManager` (or `SessionKey` a principal).
- `BearingHost` reuses `WriteRefusal`'s verdict but writes its own sentence ("exposed for reads only…").
- `ExternalAccessPolicy.UnavailableReason` reports only what `CredentialKind` settles, never runtime state.

### §1.11a — Allow-list, not deny-list
`ExternalSqlPolicy.Refuse` runs a statement only if its leading keyword is in the engine's
`ISqlDialect.ExternalReadVerbs` (`WriteGuard` still runs first). Needed because `begin read write` / `set
transaction read write` lift server read-only (pinned by `LiveExposureTests`). Tables are per engine, not
translated; T-SQL omits `DECLARE` and `VALUES`. An untrusted scanner gets an empty allow-list.

### §1.11a-bis — T-SQL needs no separator
One T-SQL span can hold several statements, so `TSqlWriteGuard.Describe` counts a risky verb anywhere in the
top-level words (`TSqlToken.Depth`). Column names like `copy` false-positive — acceptable; `[copy]` escapes.
Refusal sentences use `NamesAWrite`, not `IsRisky`.

### §1.11b — Read-only doesn't bound reads or signals
`ISqlDialect.ExternalDeniedFunctions` (session signals, server filesystem, large-object I/O, `set_config`)
and `ExternalDeniedRelations` (`pg_authid`, `pg_subscription`, `pg_user_mappings`; T-SQL qualified `sys.*`
plus bare legacy views). Stops accidents, not a determined caller — the real answer is a role.
- Scan **tokens** (`SqlNameScan`), not text, so quoting/comments don't evade and string literals don't
  match. Text fallback only when the lexer fails (`Scan` returns null vs empty set — keep them distinct).

### §1.11c — Concurrency
One invocation = one connection. Concurrency across invocations is bounded server-side (`CONNECTION
LIMIT`), not client-side.

### §1.11d — Query log v4: `origin`
Null = Bearing, `QueryOrigin.Cli` = the command. Refusals are logged (`Success = false`, verbatim SQL).
CLI honours the user's retention/redaction settings. `tables`/`describe` not logged. Audit export has an
`origin` column (Bearing rows read `bearing`). The command disposes the log before exit. Nothing on the way
to finding a project (e.g. a corrupt `recent.json`) may end the command.

### §1.11e — CLI output and flags
- Responses are `ICliResponse` records serialised by **runtime type** (`CliRunner.Serialize`). `--table`
  switches on response type; `--tsv` is the same layout, COPY-text escaped (`\N` null), rows never trimmed.
- `connections` names the project and unexposed connections (by name only).
- Export: unlimited by default, explicit `--max-rows` honoured. xlsx = sheet per result; CSV refuses
  multiple result sets.
- `--timeout` may only lower, and must be > 0 (enforced in `WithTimeout`).
- Options irrelevant to a command are refused by name. Blank option values are missing values.
- `CellValue.For` / `TextTable.Cell`: every driver CLR type needs an arm (arrays → JSON arrays, hstore →
  object, `byte[]` base64 first). WHEN changing a converter's output shape, check every consumer.
- `Bearing.Cli` must not set `InvariantGlobalization` (breaks IANA zones on Windows).
- File-write failures are `ExportWriteException` → "Could not write …", and are **not** logged.
- `explain` gated by `ISqlDialect.SupportsExplainPlan` in both hosts; it validates the caller's statement
  and sends the wrapped one.

### §1.11f — Windows installer PATH / shortcut
Velopack hooks add/remove the install dir in the per-user PATH (`WindowsPath` / `WindowsPathEntry`).
- Read with `DoNotExpandEnvironmentNames`, write back the existing `RegistryValueKind`; read the value
  before `GetValueKind` (it throws on missing); create missing as `ExpandString`. Append, don't prepend.
- `WindowsShortcut` repairs 1.0.x shortcuts that point at the renamed exe — only when the target is in the
  install dir, has the stale name, and the fix differs.
- WHEN a release renames anything the installer wrote, decide what happens to existing copies. Best-effort
  (§5.2).
