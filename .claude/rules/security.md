# Security Rules (§1.x)

This is a local desktop tool that stores DB credentials and executes user SQL against real servers.
Shared checklist: `.claude/references/security-checklist.md`.

## §1.1 — Secrets
- NEVER log, print, or write connection passwords to disk outside the secret store.
- Passwords go through `ISecretStore`, and the only implementations that store anything are the three OS
  credential stores (`SecretToolSecretStore`, `WindowsCredentialSecretStore`, `MacKeychainSecretStore`).
- WHEN no keychain is reachable the store is `NoSecretStore`: `SetPasswordAsync` throws
  `SecretStorageRefusedException`, reads return null, and the connection prompts and holds the password in
  memory for the session. **There is no on-disk fallback and no setting to re-enable one** (removed
  2026-08-19, with `LegacySecretFiles.Purge` deleting what the old opt-in wrote). DO NOT add a "save it
  anyway" path — an encrypted-at-rest store keyed by something the user supplies would be a new design, not
  a flag.
- The posture is surfaced, never silent: `SecretStorageSecure` in the status bar, and the connection
  dialog's amber block. That warning must say the keychain **couldn't be reached** and show the reason the
  probe reported (`ISecretStore.UnavailableReason` → `SecretStorageAdvice`) — never assert a cause nobody
  checked.

## §1.2 — Write guard (destructive SQL)
- `Bearing.Sql.WriteGuard` flags data-modifying / DDL statements (INSERT/UPDATE/DELETE/MERGE, DROP/
  TRUNCATE/ALTER, and data-modifying CTEs) on connections marked `RequireWriteConfirmation`.
- Keep the guard lexer-based and conservative. If you touch it, do not narrow what counts as risky without
  a test; the Production connection preset relies on it.

## §1.3 — Query log
- Executed SQL is logged to a local SQLite store (`SqliteQueryLog`) with retention pruning
  (`QueryLogRetentionDays`, default 180; ≤0 = keep forever). Don't add PII beyond the SQL text itself.
- Literal stripping is **opt-in** (`QueryLogRedactLiterals`, default off): on, `Bearing.Sql.SqlRedactor`
  replaces string/number/dollar-quoted literals with placeholders before the entry is written. Default off
  because a redacted statement can't be re-run from the history panel, and silently rewriting the user's own
  record of what they did isn't ours to decide. **Do not assume a log is sanitized** — check the setting.
  - Redaction happens in `Append`, at the boundary: the database row, the `Appended` event and the history
    panel then agree. A redactor that throws stores `(redaction failed)` — never the verbatim SQL, which is
    the one outcome the feature exists to prevent.
  - The redactor is lexer-based (`PgParsing`), like `WriteGuard`: a regex can't tell a quote inside a
    dollar-quoted body from one that ends a string. It is **not** anonymisation — identifiers are untouched.
  - It reaches the store as a `Func<string,string>` because `Persistence` may not reference `Sql` (§2.2).
- The log's files are narrowed to their owner on start (`LocalFilePermissions.HardenDatabase`), sidecars
  included — the `-wal` holds un-checkpointed entries, so hardening the `.sqlite` alone leaves the newest ones
  readable. A no-op on Windows (`%LOCALAPPDATA%` is already ACL'd), reported as `PlatformDefault` rather than
  as success. Best-effort: a filesystem that can't express the mode must not stop startup (§5.2).
- **Encryption at rest is deliberately not implemented.** It needs a key the user supplies, which §1.1 already
  rules is a new design rather than a flag — and a key held next to the file it encrypts protects nobody.

## §1.4 — Connections
- Transport security is `ConnectionInfo.Tls` (a `TlsMode`), not an options-bag entry (#23). `sslmode` is
  **reserved** in `PostgresConnectionString` so the bag — which travels in a shared project.json — cannot
  outrank the field, the same rule that stops a stray `Password` key beating the secret store.
- `TlsPolicy` is the pure policy: `Resolve` (the field, falling back to a legacy bag `sslmode` only while the
  field is untouched, so older projects and DBeaver imports keep working), `DefaultFor` (a **new** connection
  requires encryption unless the host is loopback), and `Advice`, which names *which* guarantee a mode is
  missing. `TlsPolicy.Default` stays `Prefer` — it exists so a project file without the field keeps the
  behaviour it had, and is deliberately not the default for anything new.
- WHEN touching this, keep encryption and identity distinct: `Require` encrypts and accepts **any**
  certificate. Do not describe it as verified, and do not collapse the modes into a bool.
- Don't hardcode credentials or disable certificate validation.

## §1.5 — The write confirmation counts rows, and refuses three things to do it (#112 / #100)
`Bearing.Sql.UpdateDeleteTarget.TryReduce` flattens a single-table `UPDATE`/`DELETE` to `(relation, target,
where)`, and `ExecutionViewModel.CountRowImpactsAsync` turns that into the prompt's `RowImpact`. Three
refusals hold the design up, and each of them is a bug if removed:
- It never **connects**. Counting uses `Sessions.TryGet` — an *already live* session — because a count is
  worth less than the credential prompt a connect could raise in front of a confirmation the user has not
  answered yet. No session → no numbers, and the prompt reads as it did before #112.
- It never **blocks**. `CountTimeout` (2 s) per statement, `CountBudget` (4 s) for the batch; anything that
  misses reports `RowImpact.Uncounted` ("could not count … in time"). `CommandTimeout` is 0 globally (§9.x),
  so the deadline is a linked `CancellationTokenSource`, not a connection-string setting.
- It never **guesses**. `TryReduce` declines multi-table `UPDATE … FROM` / `DELETE … USING`, data-modifying
  CTEs, placeholders and anything else it cannot flatten. A count of the wrong rows is worse than none: it is
  a reassurance about a statement nobody checked.

Three display states, deliberately distinguishable: a number, **every row** (#100 — no `WHERE`, answered
without a round trip), and *could not count*. `RowImpact.IsAlarming` covers the last two. A multi-write batch
carries `ImpactCaveat`, because the counts predate every statement in it.

The count query is `select * from <target>` wrapped by `IQueryExecutor.CountAsync` — **not**
`select count(*)`, which the wrapper would then count. The alias travels in `Target`: without it
`update payment p … where p.amount > 5` cannot be counted at all.

## §1.5a — The row count declines an unterminated predicate
`delete from t` and `delete from t where` look alike to a token scan and mean opposite things: the first
deletes everything, the second does not parse. Reporting the second as "every row" stated a fact the
statement did not have, in amber, about a statement that would fail on confirm. `TryReduce` tracks whether a
`WHERE` keyword was *seen* — not the target's terminator, which is `SET` for an `UPDATE` — and declines when
nothing follows it.

## §1.6 — The query log is schema v2, and the audit export says what it does not know (#113)
`query_log` is at `user_version = 2`: v2 added `connection_id` and `environment`, populated at write time by
`ExecutionViewModel.LogExecution`.
- Migration is **stepwise and additive** (`MigrateTo1`, `MigrateTo2`). An existing log is the user's own
  record of what they ran; a version bump adds to it and never rebuilds it.
- `MigrateTo2` runs under `BeginTransaction(deferred: false)` — `BEGIN IMMEDIATE`, taking the write lock up
  front, so two processes opening the log on the first launch after an upgrade serialise on `busy_timeout`
  rather than both reading "no such column" and the second throwing "duplicate column" out of the
  constructor. (A first rewrite of this method *said* it ran in a transaction and did not; the review caught
  the comment lying.)
- Each step is **idempotent per column** (`HasColumn` before each `ALTER`), and that is not belt-and-braces.
  `ALTER TABLE ADD COLUMN` throws on a column that already exists, so a plain batch interrupted between the
  two ALTERs left `connection_id` present with `user_version` still 1 — and every later start then threw
  "duplicate column name" and could not open the log at all, the crash §5.2 exists to prevent. Guarding the
  batch alone would have fixed it for everyone except the people who had already hit it.
- Rows written before v2 have a **null** `connection_id`. They can be matched to a connection by name at
  best, and `QueryLogReport` says how many such rows a report contains rather than implying an id lookup
  succeeded.
- `Environment` is stored, not resolved at report time: it is a fact about the past, and a connection
  re-pointed from production to staging must not rewrite what last week's statements ran against.
  `QueryLogReport.ResolveEnvironment` prefers the recorded value, then the connection **by id**, never by
  name.
- `QueryLogQuery` grew `From`/`To` (compared with `julianday()`, offset-safe like the retention prune) and a
  nullable `Limit` — null is unbounded, for a report that must cover its period. The default 200 is the
  history panel's screenful and is unchanged.
- The **writes-only** filter is a re-lex with `WriteGuard.HasRisk` at report time, not a stored verdict —
  which is what makes it work over history recorded before the filter existed. In the App layer, because
  `Persistence` may not reference `Sql` (§2.2), the same constraint that made redaction a `Func<string,string>`.
- The report's notes travel **with** the file (`ResultExport.WriteReport`): a second "About" sheet in xlsx,
  and for CSV a sibling `<name>.about.txt`. **Not `#` lines above the CSV header** — that was the first
  version, on the theory that CSV readers skip them. Excel, LibreOffice and pandas do not: the header landed
  on row 7, a note with commas split into cells, and `read_csv` raised. RFC 4180 has no comment syntax; a CSV
  must stay a pure table with its header on row 1. The status line names both files so the notes are not
  left behind. The notes state the period, that there is one user and why there is no user column, and the
  redaction setting in **both** directions (§1.3: it is a property of when a row was written, never of the
  rows in hand, and a redacted statement can never be un-redacted).
- **The connection filter matches by recorded name OR by id → current name.** The dialog offers *current*
  names; rows logged before a rename carry the old name and the right id. Name-only matching silently dropped
  everything a connection ran under its previous name — for "what ran against production last week", the
  wrong answer with no sign that it is wrong. Rows with no id (pre-v2) still match by recorded name.
- **Day bounds use the local zone's offset for the picked day** (`ReportPeriod.StartOfDay`/`EndOfDay`), not
  the picker value's own. Avalonia's date picker keeps its seed value's offset while the user scrolls to
  another month, so a January day picked in September carried +02:00 into a +01:00 date and the report's edges
  sat an hour off local midnight. An inverted range is swapped (`ReportPeriod.Ordered`), because a range that
  matches nothing was reported as "nothing ran in that period" — a false negative handed to an auditor.
- **Everything that can fail in `ExportHistoryAsync` is inside its `try`** — the store read included. The
  sidebar reaches it through an `async void` click handler, so a locked or corrupt log used to surface as a
  crash report instead of the status bar (§5.2).

## §1.7 — Roles are read from `pg_roles`, never `pg_authid`, and nothing is granted (#120)
- `GetRolesAsync` reads the **view**, which masks `rolpassword`. `RoleInfo` has no field for a hash and the
  query does not select one. `pg_authid` is not touched.
- Grants are **read-only**. No `GRANT` / `REVOKE` is generated: a mistake there is a production incident and
  the write guard has no lexer for ACLs (§1.2). Privileges come from `aclexplode`, rendered as
  `SELECT, INSERT` rather than the `arwd` bitmask shorthand.
- `RoleGrants.Visible` exists so a refused read is never shown as an empty privilege list. "This role can do
  nothing here" and "you are not allowed to find out" are different answers, and conflating them is the one
  mistake a privilege screen must not make.
- Absences are typed, not blank: `ConnectionLimit == -1` is *unlimited* (never printed as a number) and
  `ValidUntil == null` is *no expiry*. Neither is a permission problem, and rendering either as "not visible"
  would assert a cause nobody checked (§1.1).

## §1.8 — Three catalog columns hold credentials and are never selected
The schema explorer reads a lot of catalogs, and three of them carry secrets. None is read, and none has a
field to land in:
- **`pg_authid.rolpassword`** — the password hash. Roles come from the `pg_roles` **view**, which masks it
  (§1.7).
- **`pg_subscription.subconninfo`** — the publisher's connection string, password included. A subscription's
  row says whether it is enabled and which publications it takes, which needs none of it. Selecting it would
  put a credential into a tree row, a tooltip and the clipboard.
- **`pg_foreign_server.srvoptions` and `pg_user_mappings.umoptions`** — a foreign server's options and a
  mapping's options routinely contain a password, and `pg_user_mappings` shows them in full to the owner. The
  server's name, wrapper and version answer "what does this foreign table point at" without them. User
  mappings are not read at all.

WHEN adding a catalog read, check what the catalog holds before selecting `*` or an options bag.
