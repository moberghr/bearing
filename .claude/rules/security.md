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

## §1.9 — The two session-level safety settings ride the startup packet, and a `SET` cannot replace it (#99 / #105)

`ConnectionInfo.ReadOnly` asks the server to refuse writes; `ConnectionInfo.StatementTimeoutSeconds` asks it
to cancel a statement that outruns a limit. Both are **fields**, not `Options` entries, for §1.4's reason —
and `options` is **reserved** in `PostgresConnectionString`, because that keyword is the whole packet rather
than one entry in it, so a bag key in a shared `project.json` would not merge with what we composed, it would
replace it and turn read-only back off.

**Both issues proposed a `SET`, and neither can be done that way.** `PostgresConnectionString.StartupOptionsFor`
composes `-c default_transaction_read_only=on -c statement_timeout=<ms>` instead. A pool holds up to
`MaxPoolSize` physical connections per (connection, database) and every read path opens one straight from the
data source, so a `SET` reaches the one socket it was issued on and none of the others — and the idle sweep
plus Npgsql's own pruning means those sockets come and go under a live session. The connection would be
read-only or not depending on which socket a statement landed on. Postgres applies the startup packet to
**every** physical connection before it can run anything, which is the only mechanism here that is true of.
`SessionPolicyTests` pins it against a live server with ten concurrent statements, which is the test a `SET`
would fail. **Do not "simplify" this into a `SET`.**

- **Read-only is not a privilege boundary, and must never be described as one.** `default_transaction_read_only`
  is a `USERSET` GUC, so a user who types `SET default_transaction_read_only = off` or `BEGIN READ WRITE` lifts
  it. It stops mistakes, which is what #99 is for. The boundary a user cannot lift is a role without write
  privileges, and that is the server admin's to grant — the same line §1.4 draws between `Require` encrypting
  and the Verify modes checking identity.
- **A read-only connection refuses rather than confirms** (`WriteRefusal`, ahead of the write-guard prompt in
  `ExecutionViewModel`). This is a deliberate §1.2 posture change, approved: there is nothing to confirm when
  the answer is already no. The guard itself is **not** narrowed — the refusal reads the verdict it already
  produces, and `WriteGuard.Describe` is still what produces it.
- The client-side refusal is the *early* half, never the enforcement. The lexer misses a function that writes,
  dynamic SQL in a `DO` block, `COPY … TO`; the server catches those. Both halves are wanted.
- **The grid stops offering edits through the reason it already had.** `EditabilityResolver.ResolveWithReason`
  takes the connection's read-only state and reports it **first**, ahead of every reason about the result's
  shape — so all ~20 `IsEditable` gates and the existing lock chip follow from `EditTarget` being null. The
  reason's wording has to compose with the chip's own `"Read-only — {reason}"` prefix.
- **Read-only never touches the beacon.** §9.3a gives the beacon connection *state* in its silhouette and
  nothing else; a connection can be read-only and disconnected. It is marked in `StatusTip` and the tab
  tooltip instead — not in `StatusLabel`, whose width is reserved so the toggle beside it does not shift.
- **These settings are part of what defines a live pool, and nothing more.** `SameConnection` (pool reuse)
  and `SamePool` (evict on save) compare them, as neutral values rather than as the composed packet. Both are
  needed and for different reasons: `SameConnection` only rebuilds on the next full connect, while `TryGet` —
  which Fetch all rows, Count total, grid paging and FK navigation all reach through a lease — compares
  nothing at all. Found by review: without the evict, adding a timeout and then clicking Fetch all rows on
  the result already on screen ran unbounded off the old pool while the status bar reported the limit.
- **`SamePool` is separate from `SameNetwork`, and the split is the point.** `SameNetwork` answers "is this
  the same server, reached the same way", and it has three jobs that are not about the pool: keeping a schema
  tree node and its expansion, keeping the cached completion snapshot, and matching an incoming import to an
  existing connection. Folding the pool question into it — the first attempt, caught by review — made
  changing a statement timeout collapse the tree, throw away the snapshot, and re-import as a duplicate. A
  changed *network target* invalidates the catalog; a changed *pool setting* does not.
- **The timeout is clamped, not just validated.** `statement_timeout` is milliseconds in an `int`, so
  `SessionPolicy.MaxTimeoutSeconds` is that ceiling in seconds. `project.json` is hand-editable and a value
  like 3,000,000 overflowed the conversion negative, which Postgres refuses *at startup* — so a silly number
  in a settings file stopped the connection opening at all rather than merely being ignored.
- **`57014` has three causes and they are told apart in the App layer** (`Results/QueryErrorText`), because
  attribution needs the `ConnectionInfo` the executor does not have: the user's own Esc (already reported by
  the run path from its cancellation token), this connection's timeout, and a cancel from outside the app.
  Matching the server's message text instead would break under `lc_messages`. `25006` names the connection's
  read-only setting only when it is *ours* — a read-only server, role or enclosing transaction produces it
  too, and nothing here checked which (§1.1).
- **Every route that carries a result has to carry the connection**, or the attribution silently inverts:
  the FK-navigation path omitted it and reported a configured timeout as "nothing in Bearing asked for this".
- **Copy/paste and the project file must carry these fields.** `ConnectionClipboard.Entry` had been omitting
  `Tls` since #23 — copying a Verify Full connection pasted one on the driver default — and the test named
  `Every_non_secret_field_survives_the_round_trip` could not catch it, because it enumerated ten fields by
  hand and the omitted one was omitted from the assertions too. It reflects over `ConnectionInfo` now, so the
  next field fails until it is carried.

### §1.9a — On SQL Server only one of the two halves exists, and the wording says so

The mechanism above is Postgres': a startup packet the **server** applies to every physical connection. SQL
Server has one of the two and not the other, and pretending otherwise is the failure mode this note exists
to prevent — the setting was offered for both engines from the moment the second one landed, while
`StartupOptionsFor` was the only thing applying either.

- **The statement timeout is `SqlConnectionStringBuilder.CommandTimeout`**, applied in
  `SqlServerConnectionFactory` **after** the `Options` bag so the typed field outranks a `Command Timeout`
  entry in a shared `project.json` (§1.4's precedence). It is the **client** cancelling, not the server
  refusing — the server may still be finishing the statement for a moment after — so the dialog's note
  no longer says which side does it. 0 is "no limit" in both engines, which is what makes the shared
  spelling work. Before this it was inert: the status bar reported a limit over a connection that ran
  unbounded, the same shape as the Fetch-all pool bug below.
- **There is no session read-only to ask SQL Server for.** `ApplicationIntent=ReadOnly` chooses a readable
  secondary; it refuses nothing. So there the client-side `WriteRefusal` is the **whole** of it, and a write
  the lexer cannot see — inside a procedure, or built as dynamic SQL — reaches the server. That is not
  fixable in the client, so it is **stated**: `IDbProvider.EnforcesReadOnlyOnServer` decides whether the
  dialog says "The server refuses writes on this connection" or "Bearing refuses writes on this connection
  before they run", and the second sentence names what still gets through. §1.1's rule, applied to a
  guarantee rather than to a cause: never assert an arrangement nobody made.
- WHEN a third engine arrives, answer that flag before shipping its connection dialog. The default is
  `true` because it was Postgres' behaviour, which means a provider that forgets it claims a server-side
  refusal it may not have.

## §1.10 — Manual commit is a connection Bearing holds open, and every path that could take it must say so (#131)

`ConnectionInfo.ManualCommit` makes a **write** open a transaction that stays open until the user presses
Commit or Rollback. It is the third per-connection safety setting and the only one that reaches no server:
§1.9's two ride the startup packet, this one is a pooled connection kept checked out and a `COMMIT` declined.
That is why `CommitPolicy` is its own class beside `SessionPolicy`, and why it is **not** part of
`SamePool`/`SameConnection` — it does not define a pool.

- **A read opens nothing** — and the test for that is `StatementRisk.NamesAWrite`, **not** `IsRisky`. The
  guard's verdict is generous on purpose: on T-SQL anything whose lead word is not a known read is treated
  as a write so that it gets confirmed. That is the right answer to "must this be confirmed?" and the wrong
  one to "does this write?" — `declare @id int; select …` is ordinary T-SQL for a read, and opening a
  transaction on it makes browsing hold locks, which is the outcome this rule exists to prevent. A dialect
  the guard cannot read still counts as a write: not knowing is a reason to hold a write, never a reason to
  let one commit itself.
  The first *write* opens one; once open, everything that tab runs joins it, reads
  included. Opening on any statement is DBeaver's behaviour and is how a session ends up `idle in
  transaction` from nothing but browsing — the hazard #101's panel exists to make visible. Reads joining an
  already-open transaction is what makes the row-count preview (§1.5) and the grid agree with it.
- **The write guard is not relaxed.** A connection with both settings confirms the write *and* holds it.
  §1.2 is not narrowed because a write became undoable.

### The seam: a second `IQueryExecutor`, not a parameter on the first
Every executor method in both providers opened its own pooled connection and handed it back when the method
returned, so a transaction begun in one call was over before the next began. `IDbProvider.CreateTransactionScope`
hands out an `ITransactionScope` holding **one** connection, with an `IQueryExecutor` pinned to it — so
nothing downstream learned a second way to run a statement. The executors take an `IPgConnectionSource` /
`ISqlConnectionSource` (pooled: open and dispose; pinned: the held one, dispose nothing) and are written once
against both.
- **`ExecuteWriteAsync` commits only what it opened.** `own` is non-null exactly when the call began a
  transaction, and that is also what says whether to commit and whether disposal may roll back. On the pinned
  source an inline-grid save joins the user's transaction.
- **`ExecutorFor(tab, session)` in `ExecutionViewModel` is the only chooser**, and it re-checks the
  `SessionKey`. Every call site that runs the user's SQL goes through it, or it opens a fresh connection and
  misses the transaction.
- **EXPLAIN ANALYZE is refused while the tab holds one**, and both halves of why are the same fact.
  `ExplainSql.Measured` carries its own `BEGIN … ROLLBACK` — necessarily, since a plain SELECT can call a
  volatile function that writes — so on the held connection that rollback ends the *user's* transaction. Run
  on a pooled connection instead (the first attempt) it executes against rows this tab's transaction has
  locked and blocks; with no statement timeout by default it waits forever, and while it waits the tab counts
  as running, so Commit and Rollback are both out of reach — the hang removes the only action that would end
  it. A **plan-only** EXPLAIN has neither problem (`RolledBack: false`, executes nothing) and runs on the
  transaction like any other read.
- **Metadata, schema and the activity panel stay pooled.** A catalog read inside the user's transaction is
  wrong, and a panel polling it would pin it.
- **`CountAsync` is savepoint-protected, and it is the only call that is.** A shape the count wrapper cannot
  take is an error, and on Postgres any error dooms the transaction — so the row-impact probe Bearing runs on
  the user's behalf would destroy their work. The savepoint protects the transaction *from us*; it is not a
  general undo for statements they asked for. `TrackedExecutor` therefore exempts `CountAsync` from the abort
  rule, and does not count it as a statement either — every write confirmation runs one, and the chip counts
  what the user wrote.
  - **Every failure unwinds, not just the driver's own exception, and the unwind uses
    `CancellationToken.None`.** The probe runs under a deadline (§1.5), so the commonest way to leave it is a
    cancelled token — which arrives as `OperationCanceledException` *after* the server has raised 57014 and
    aborted the block, and which would make the recovery throw before reaching the server if it were issued
    on the same token. Catching only `PostgresException`/`SqlException` left the savepoint un-rolled-back,
    the scope still reporting `Active`, and Commit still on offer for a transaction Postgres would silently
    have turned into a rollback. Pinned live, both engines.

### One abort rule, two different truths
Any failure marks the transaction `Aborted`, and Commit is then refused. On Postgres that is simply what
happened (`25P02` thereafter — measured). On SQL Server it is **conservative**: a duplicate key there leaves a
transaction the server would still commit, also measured. The cost is a rollback of work that server would
have taken, against offering a `COMMIT` that Postgres turns silently into a `ROLLBACK`. Bearing does not offer
to commit what it cannot promise to commit. Do not "fix" the SQL Server side by probing `XACT_STATE()`
without deciding that trade again.

### Typed `BEGIN`/`COMMIT`/`ROLLBACK` are refused, on a manual-commit connection only
`ISqlDialect.TransactionControl` classifies them and `WriteRefusal.ReasonForTransactionControl` refuses the
whole batch. An ordinary connection keeps running a script that manages its own transactions, so nothing is
narrowed. The classification is per dialect and **not a translation**: in T-SQL a bare `BEGIN` opens a
`BEGIN … END` *block* and `END` closes it, while in Postgres `BEGIN` is a transaction and `END` is a commit.
Getting that backwards refuses every T-SQL block or lets a raw `COMMIT` end the held transaction behind the
driver's back. `PREPARE` needs its second word too.

### A transaction and its statement share one connection
`ExecutionViewModel.EndTransactionAsync` refuses while `tab.IsRunning`, and `CanCommit`/`CanRollback` follow
it, so neither the buttons nor the palette can reach them mid-statement. A commit issued on a connection with
a command in flight throws from the driver, and the teardown behind it would dispose that connection under a
live reader — ending the transaction on the server while the app believed it had committed. Esc first.

The idle sweep obeys the same rule twice over: `TrackedExecutor` stamps `LastActivityUtc` when a statement
**finishes** as well as when it starts (a sixteen-minute UPDATE used to age past the rollback threshold while
it ran), and the sweep skips a running tab outright. It is also not re-entrant — the timer is not awaited, and
a rollback slower than one tick would log a second `rollback` row and raise a second toast for one transaction.

### A retry may not re-apply what a failed attempt already did
SQL Server's Msg 334 makes `ExecuteWriteAsync` re-run the whole batch with the returning clauses dropped,
on the premise that the failed attempt left nothing behind. That is true when it owns the transaction —
disposing it rolls back — and **false when it joined the user's**, where a DELETE that succeeded before a
sibling INSERT raised 334 would be applied twice: its triggers fired again, its affected count reported as 0
the second time. `RunBatchAsync` therefore takes its own savepoint (`bearing_write_batch`, distinct from the
count probe's — both can be in flight in one transaction) and unwinds to it before letting the failure reach
the retry. Pinned live against a trigger-bearing table, which is the only shape that shows it.

### Per tab, and capped
A pool is shared by every tab on a (connection, database), so a session-owned transaction would be one two
tabs wrote into without either saying so. `TabTransactions` keys on the tab. Each open transaction holds a
`SessionLease` — which exempts it from the idle sweep for free, and means `EvictConnectionAsync` only
*retires* the session, so **Disconnect must roll back itself** or the transaction survives it invisibly.
`CommitPolicy.MaxOpenPerPool` (4 of `DefaultMaxPoolSize` 10) refuses the next one with a sentence, because
without it tabs that have each written once exhaust the pool and every other operation on that database
blocks with nothing on screen to say why.

### The guard table — the list *is* the feature
| Path | What it does |
|---|---|
| Switch a tab's connection or database | **Refuse** — another pool (§9.4a); there is no version that keeps it. No-ops early when nothing would change, so re-selecting what the tab is already on is not reported as a switch |
| Disconnect, refresh server metadata | **Ask and act** (`TransactionGuard.ConfirmAsync`) — nothing can intervene between the two |
| Quit, close the tab | **Ask, then act later** (`AskAsync` … `RollbackAsync`) — see below |
| Connection edited into a pool rebuild, **manual commit turned off**, connection deleted, **project removed**, script deleted | **Roll back and report** — the user already confirmed something that subsumes it |
| Idle sweep | Skips a running tab; otherwise rolls back past the threshold |

**A forced rollback cancels rather than refuses.** The two verbs refuse outright while the tab is
mid-statement (above); a guard path cannot — the disconnect, close or quit is already happening — so
`RollbackWhereAsync` cancels the statement first. Without that it disposed the pinned connection under a
live reader, which is the thing the verbs refuse by hand.

**Asking is not acting, and where a third thing can intervene they must be separate steps.** Quit asks about
transactions *and* about running queries; a tab close asks about the transaction *and* about unsaved text.
Rolling back as soon as the first question was answered lost the work for an action the second question then
cancelled — and unlike a cancelled query, a rolled-back transaction cannot be re-run. `AskAsync` and
`RollbackAsync` are split for exactly this; `ConfirmAsync` is the convenience for the paths where nothing
comes between.

**Turning the mode off ends the transaction.** `ManualCommit` is not part of `SamePool` (it reaches no
server), so nothing else on the save path would have noticed — and the transaction would have gone on taking
that tab's writes uncommitted while the refusal that stops a typed `COMMIT` reaching it disappeared with the
setting.

**Every route that removes a tab has to end its transaction**, not only `CloseTabAsync`. There are two, and
they are not the same as a project *switch*: deleting a script removes its tabs directly, and removing a
project drops every tab it owned (`WorkspaceContext.Close` returns them, which is what
`TabTransactions.RollbackForTabsAsync` is handed). A **switch** is safe on its own — `Park` keeps the tab
view-models alive on the `ProjectWorkspace` and `AllTabs` still sees them, so the transaction is off screen
rather than lost, and switching back makes it reachable again. A transaction left on a dropped tab is
unreachable: nothing can select it, so neither the chip nor either command can ever reach it, while it goes on
holding a pooled connection and a lease that stops the session being disposed — and it still counts against
the per-pool cap.

WHEN adding a path that disposes, retires or re-keys a session, or that removes a tab, decide what it does to
an open transaction before the server decides for you. A severed connection rolls back on the server's
schedule, silently, which is the exact failure this feature exists to prevent.

### The mode is flippable without the dialog, for this session only
A toolbar pill (`ModePill`, beside Commit/Rollback and always visible when the tab has a connection) switches
the connection between auto and manual commit; `transaction.toggleMode` is the same thing from the palette.
- **It does not write the connection.** `project.json` is shared, and turning manual commit on for one
  dangerous `UPDATE` is not a statement about how everyone else should work. `CommitModes` holds the
  override in memory, per connection, for the session.
- **`WorkspaceContext.EffectiveConnection` is the only place it is applied** — the same seam that substitutes
  the tab's active database. Everything downstream reads `ManualCommit` off a plain `ConnectionInfo` and
  never learns an override exists, which is why the execution path, the write refusal, the status tip and
  every guard needed no call site.
- **`CommitModes` takes an *id* everywhere except `IsManualCommit`, and that is a guard.** Most records the
  app holds are *effective* ones, which already have the override applied — comparing an override against
  one compares it with itself and reports that nothing is overridden, which is exactly what the first
  version did.
- **Saving the connection clears the override**, because the dialog is the authoritative statement of what
  the connection is; without that, unticking the box would appear to do nothing. The rollback check on save
  therefore compares the mode *in force* (override included) against the saved one — an override that had a
  transaction open still has to end it.
- **Switching to auto-commit is refused while any tab on that connection holds a transaction.** The mode is
  per connection and the transactions are per tab, so there is no coherent "and that one keeps waiting"; and
  a toolbar pill may not be a thing that discards data or commits to production, which are the only other
  two answers.
- The pill marks an override with a dot, the mark the tab strip already uses for a buffer that differs from
  its file. It was italics first: italic glyphs render past the advance width the content presenter measured,
  so the last letter clipped at every pill width. A marker that changes metrics belongs in the text.

### Two clocks, and the query log
`AppSettings.IdleTransactionWarnMinutes` (5) turns the status chip amber; `IdleTransactionRollbackMinutes`
(15, raised to the warning if set below it) rolls it back and reports it as a **toast**, because the premise
is that nobody was watching the status bar. 0 disables either. Idle is measured from the last statement, not
from when it opened.

The chip lives beside the beacon and **never on it** — §9.3a gives the beacon connection *state* in its
silhouette, and a transaction is not one. Commit/Rollback are commands (§9.2) with a toolbar home and no
default binding: every plausible gesture is taken, and `Ctrl+Shift+C` belongs to `select.connection`.

`ConfirmDiscardTransactionDialog` gives its **"Keep it open"** button both `IsCancel` and `IsDefault` — the
§9.13 treatment, so no single keystroke throws away uncommitted work. That is one keystroke more than
`ConfirmCancelRunningDialog` asks for, deliberately: a cancelled query can be re-run.

`query_log` is at `user_version = 3`: `transaction_id` is written on each statement, and the commit or
rollback is logged as an entry of its own carrying the same id — it *is* SQL the user caused to run, which is
the line §9.13 draws. Null means auto-commit, which is not the same as "rolled back", so a report says how
many rows it could not place. **The logging lives in `TabTransactions`, not at the two buttons**, because
every end has to be in the record: a history that only recorded the ones the user pressed could not tell
"committed" from "discarded on the way out by quit, a disconnect or a deleted connection". It is written in
the `finally`, so an end that **failed** is recorded too (`Success = false` with the message) — the scope has
released its connection either way, and leaving it out would put the statements in the log with a transaction
id nothing matches, which reads exactly like one still open.

## §1.11 — A connection is reachable from outside Bearing only if it says so, and only for reads

`ConnectionInfo.ExternalAccess` (`None` by default) is what lets a host that is not the app see a connection
at all. Today that host is **`bearing`** (`src/Bearing.Cli`), a console exe run by a person, a script or
an agent with a shell. `ExternalAccessPolicy` is what the setting *means*, and the
split is the point: **the flag grants nothing by itself.** `ForExternalHost` is the only route to an exposed
connection, and it is what forces the settings the exposure is safe under. A host that read the saved record
directly would be running with the user's own settings, which is the bug this shape exists to prevent.

**The mark gates discovery, not access.** It lives in `project.json` and the keychain is keyed by
`ConnectionInfo.Id`, so anything running as the user can copy that file, set `ExternalAccess` on connections
nobody exposed, and point `--project` at the copy. Read-only still holds — `ForExternalHost` applies the
settings when the connection is opened, not when the file is read — so this widens *which servers* a caller
can reach, never what it may do to them. Inside the stated model below, but the docs said "a real gate" and
had to say which kind.

**The boundary is "anything running as you", and it must never be described as more.** An external host runs
as the user and can therefore read the same keychain the app reads; what stops it querying a server is that
nobody marked the connection. That is a real gate and it is the only one this layer has — it is not a
sandbox, and §1.1's rule against asserting an arrangement nobody made applies to the docs, the dialog and the
tool descriptions alike.

### Three settings are decided for the host, not read from the record
- **`ReadOnly` is forced on**, whatever the connection is for the user. The two are deliberately independent:
  the usual reason to expose a connection is that you still write to it yourself. On an engine with
  `IDbProvider.EnforcesReadOnlyOnServer` this rides the startup packet and the **server** refuses (§1.9);
  where it does not, the client-side refusal is the whole of it and a write hidden from the lexer still
  reaches the server, so the host has to say which it is (§1.9a). Exposing a SQL Server connection is a
  weaker arrangement than exposing a Postgres one, and the wording is where that difference lives.
- **`ManualCommit` is forced off.** Inert while nothing writes, since it is the first *write* that opens a
  transaction (§1.10) — but an external host has no Commit button, no chip and no quit guard, so a
  transaction opened on one would be precisely the unreachable transaction that rule is about. A setting
  that cannot be honoured is cleared rather than left inert for a reason that could stop being true.
- **A missing statement timeout is filled in** with `SessionPolicy.PresetTimeoutSeconds`. A timeout the user
  *set* is kept, however long: the fill stops a runaway outliving the caller, it does not second-guess a
  connection pointed at slow analytical work. `RequireWriteConfirmation` is carried across untouched — there
  is nobody to confirm to and the write is already refused, and clearing a safety flag to tidy up a record is
  how one stops being set when it starts mattering again.

`ExternalAccessTests.Nothing_but_the_three_forced_settings_differs_from_the_saved_connection` pins that list
by reflection, so a fourth forced setting has to be an argued addition rather than a quiet one.

### It travels with the project and not with the clipboard
The field is in `project.json`, which is right: "this connection may be queried by tooling" is a fact about
the connection, and a team sharing a project already shares its connections' settings. `ConnectionClipboard`
**drops it**, and is the one deliberate omission in that payload besides the id.

Every other setting there preserves a *restriction* when carried, so losing one downgrades the pasted
connection — which is the whole reason they were added (#23 / #99 / #105). Exposure runs the other way: it
would carry a *permission* onto a machine whose owner never granted it. The asymmetry in being wrong settles
it — a lost restriction announces itself the first time a write is refused, while an arrived-with permission
is invisible until something uses it. Opening a shared project is taking that project's configuration whole,
exposure visible in the connection list with it; a paste mints a fresh id and a fresh connection in the
recipient's own project, which is authoring one rather than accepting one.

### `ReadWrite` is absent, and that is the decision
Not because writes from outside are unthinkable, but because what makes a write safe here does not exist. In
the app a guarded write is confirmed by a human reading the row count (§1.5); an external host has nobody to
ask, so the level would have to raise a dialog on a screen the caller cannot see — blocking an unattended
agent on a prompt nobody answers — or drop the confirmation, which is §1.2 narrowed for the caller least able
to notice it went wrong. Adding the enum member is the easy half.

### `bearing` is the command and `bearing-app` is the window
The console exe takes the plain name; the GUI apphost is renamed. A command **has** to be the console one —
a WinExe cannot write to a pipe or return a useful exit code — and the name a person already knows is the
one they will type, so `bearing` with no arguments launches `bearing-app` (`AppLauncher`). Both are
published into one directory by `build/velopack.sh`, so "beside me" is how the CLI finds the app; on macOS
it opens the enclosing `.app` rather than the binary, because Launch Services is what gives it a Dock entry.

DO NOT "tidy" the GUI apphost back to `bearing`: the two would collide in one directory, and the one that
lost would be the one people type. **The rename reaches every script that names the published binary**, and
`build/release.sh` was missed — it looked for `$PUBDIR/bearing` and died at its own existence check, so the
kept archive path was broken by a change that only touched `velopack.sh`. It has an `APP_EXE` separate from
`APP_ID` now: that archive ships the window and nothing else, so what it *installs* is still `bearing`. The update identity is `$PACK_ID`, not an exe name (§9.6), so the rename
does not orphan an installed client — but it does change the Start Menu shortcut's target, so the first
update across it is worth watching.

### The front end is a CLI, and an MCP server was built and dropped
Both were written over the same `IBearingHost`; the protocol skin was deleted rather than shipped beside it.
A CLI is reachable from a shell, a script, CI and any agent that can run a command, costs nothing in context
until it is invoked, and shows up in shell history where a refusal can be read afterwards. The MCP server
reached Claude Desktop and Cursor, which have no shell, and kept one pooled connection and one catalog read
for a whole session — which the CLI cannot, since **every invocation is a fresh process and therefore a
fresh connection**, and `tables`/`describe` re-read the catalog each time. That cost is the known trade, and
it is the right one at a few calls a minute; it would be the wrong one inside a loop.

WHEN an MCP server is wanted again, it is another skin over `IBearingHost` and not a second implementation.

### An external host owns its own session manager
`SessionKey` is (connection id, database), and the exposed `ConnectionInfo` differs from the saved one only
in settings the key does not cover. `ConnectionSessionManager` reuses a live session only when
`SameConnection` matches, and that compares `ReadOnly` and the timeout (§1.9) — so a manager serving both the
UI and an external host would tear down and rebuild the same pool on every alternate use, silently, each
rebuild costing a handshake. Today they are separate processes and the question does not arise. WHEN an
external host is ever hosted **inside** the app, give it its own manager rather than sharing the UI's, or
give `SessionKey` a principal.

### A refusal has to give advice the caller can act on
`WriteRefusal` decides whether a batch runs, and `BearingHost` never re-implements that verdict (§2.6) — but
it writes its **own sentence**. `WriteRefusal.Reason` ends "Turn read-only off for this connection to write
to it", which is right for the person at the keyboard and wrong twice through here: read-only is not this
connection's setting, and turning it off changes nothing because exposure forces it back on. Found by running
the built binary rather than by reading it. Telling a caller to do something that cannot work is §1.1's
failure in its plainest form, so the exposed path says "exposed for reads only … its owner can widen that".

### §1.11a — The exposed path is an allow-list, because the deny-list was measured and failed

`ExternalSqlPolicy.Refuse` runs a statement only when its leading keyword is on **this engine's**
`ISqlDialect.ExternalReadVerbs`. Everything else is refused, including shapes nobody has thought about.
`WriteGuard` is unchanged and still runs first — it names the verb of an outright write in the sentence a
caller most needs — but it is a **deny-list**, and a deny-list is the wrong default for a caller who is not
the person at the keyboard.

**This is not theoretical.** Against the live test server, on a connection exposed read-only:

```
begin read write; select nextval('film_film_id_seq')          -> 1002, ran
set transaction read write; select nextval('film_film_id_seq') -> 1001, ran
```

Neither `BEGIN` nor `SET` is a risky verb, so the guard passed them, and Postgres honoured them — the
sequence really advanced. §1.9 says this in words ("a user who types `SET default_transaction_read_only =
off` or `BEGIN READ WRITE` turns it off"); what it means for an untrusted caller is that the server-side
read-only session is **not** a barrier on its own. `LiveExposureTests.The_read_only_barrier_cannot_be_lifted_from_outside`
pins all four spellings and asserts the sequence did not move, because "the command failed" is a weaker
claim than "nothing happened".

Two things that did *not* work, recorded so nobody re-derives them: `set default_transaction_read_only =
off` alone changes the GUC but not the transaction already in progress, so it achieves nothing by itself;
and `SELECT … INTO` *is* caught by the dialect scanner, which the risky-verb list alone does not suggest.

- **The tables are per engine and not translations** (§5.4a's rule): Postgres reads `SHOW`, `TABLE` and
  `VALUES`, T-SQL does not, and T-SQL deliberately omits `DECLARE` although it opens many ordinary read
  scripts — it is also how dynamic SQL is staged, and an ambiguous shape defaults to refusal.
- **A dialect whose scanner cannot be trusted gets an empty allow-list.** `HasDialectAwareGuard == false`
  already means "assume every statement writes"; the mirror of it is "vouch for none". Borrowing another
  engine's verbs would be vouching on its behalf.

### §1.11a-bis — T-SQL needs no separator, so the leading word is not the statement

`TSqlScanner.Split` breaks on `;` and standalone `GO`, but **T-SQL does not require either between
statements**. So one span can be a whole batch, and its leading word says nothing about what the text runs:

```
select 1 drop table t            -> statements=1  verb=SELECT  risky=false
select 1 begin drop table t end  -> statements=1  verb=SELECT  risky=false
```

Found by code review and measured: sent to a live SQL Server, the first returned its row **and dropped the
table**. Postgres is unaffected — it requires the semicolon, so its splitter sees two statements.

**This was a `WriteGuard` hole first and an exposed-path hole second**, which is why the fix is in
`TSqlWriteGuard.Describe` rather than in `ExternalSqlPolicy`: on a guarded T-SQL connection the *editor*
did not prompt for it either (§1.2). A risky verb anywhere in the statement's **top-level** words now counts.

- `TSqlToken.Depth` is *parenthesis* depth, so a verb inside a `begin … end` block still counts — it is a
  statement there too — while a word inside a string literal or square brackets is not a word at all and
  cannot false-positive.
- What can false-positive is a column genuinely named after a verb (`select copy from t`), which now asks
  for a confirmation it does not need. §1.2's stance is that this is the right side to be wrong on, and
  `[copy]` is the escape. Widening the guard is always allowed; narrowing it is what needs an argument.
- `ExternalSqlPolicy` asks `NamesAWrite`, not `IsRisky`, before saying a statement writes: the T-SQL
  verdict is generous by design, and "'DECLARE' writes" would assert a cause nobody checked (§1.1). Such a
  statement is still refused — by the allow-list, in its own words.
- `VALUES` was removed from T-SQL's `ExternalReadVerbs`. A standalone `VALUES (1)` is not a T-SQL statement
  (it is a table constructor inside INSERT or FROM); the entry was Postgres' vocabulary carried across,
  which is precisely what §5.4a says not to do, and it was dead anyway.

### §1.11d — What an external host runs is in the record, refusals included

`query_log` is at `user_version = 4`: `origin` names the host that ran the statement — null for Bearing,
`QueryOrigin.Cli` for the `bearing` command. Stepwise and additive like every migration before it (§1.6),
with the write lock taken up front and the per-column guard, which matter **more** here than ever because
two processes now open this file and the CLI may well be the one that migrates it first.

- **A null origin is not ambiguous**, unlike #113's null `connection_id`. Nothing but the app could write to
  this log until the column and the second host arrived together, so no historical row could have been
  external. A report does not have to hedge about old rows the way the connection id's does.
- **Bearing writes no name of its own.** It is the overwhelming majority of rows, and null for "us" against
  a name for "not us" is what reads correctly in a history list at a glance.
- **A refusal is recorded**, with `Success = false` and the reason as the error — the shape a failed
  execution already has. Strictly nothing ran, and §1.3 calls the log the record of SQL the user *ran*; the
  exception is deliberate, because the caller here is not the person at the keyboard and "an agent tried to
  lift read-only and was stopped" is the single most useful line this log can hold. The statement is stored
  verbatim, since an audit that recorded only "refused" could not say what of.
- **The CLI honours the user's own settings** for retention and redaction rather than defaulting them:
  §1.3 makes redaction a property of *when a row was written*, and someone who asked for literals to be
  stripped did not ask for that to stop applying to what an agent ran.
- **`tables` and `describe` are not logged.** They read the catalog, and §9.13's precedent is that a panel's
  own reads are not the record of what someone ran.
- **The audit export carries an `origin` column, and its notes had to change with it.** The report used to
  state "Every execution here was run by the person using this Bearing installation … there is nothing to put
  in [a user column]", which after this feature is a compliance document *denying a second actor exists* —
  handed to an auditor, about a period that may contain an agent's statements. The account claim is still
  true and still made; what it may no longer do is stand in for "a person typed this". Bearing's own rows
  read `bearing` rather than blank, because a blank cell beside `cli` reads as a missing value rather than
  as an answer. Found by review, after `docs/CLI.md` had already claimed the export carried it.
- The log is held and disposed by the command, not merely handed to the host: `Append` returns before the
  row is written, so exiting without disposing loses the entry the command just produced.

### §1.11b — Read-only bounds writes; it does not bound reads or signals

Measured on a superuser connection with read-only fully in force, every one of these a plain `SELECT`:

```
select pg_read_file('/etc/passwd')   -> the file
select pg_ls_dir('/etc')             -> the directory
```

`pg_terminate_backend` needs no write at all (§9.13 says so for the activity panel). So
`ISqlDialect.ExternalDeniedFunctions` refuses the obvious ones — other sessions, the server's filesystem,
large-object file I/O, and `set_config`, which is `SET` in a `SELECT`'s clothing and therefore invisible to
the verb allow-list.

**That list stops accidents, not a determined caller, and must never be described as more.** A name reaches
through a wrapper function, a search_path that resolves elsewhere, or SQL built at runtime. The boundary
that holds is a role without the privilege. WHEN someone asks for exposure to be "safe", the answer is a
role, not a longer list.

**But a different *spelling of the same name* is not one of those, and has to be caught.** The scan reads the
statement's **tokens** (`SqlNameScan`, off each dialect's own lexer) rather than its text, which settles
three things at once. The previous text match was
`\b<name>["\]]?\s*\(` — the optional delimiter is the point. `select "pg_read_file"('/etc/passwd')` is
ordinary Postgres and went straight through, because the closing quote sits between the name and the `(`,
while the qualified `pg_catalog.pg_read_file(…)` was caught: an asymmetry with no reason behind it, in a
list whose whole job is the obvious cases. `]` is T-SQL's spelling of the same thing.

**And a comment defeated it with four characters.** `select pg_read_file/**/('/etc/passwd')` is valid
Postgres, is a plain SELECT the allow-list admits, and skipped the denied list entirely — a comment is
whitespace to the engine and is not `\s`. That is *not* one of the evasions this list openly accepts (a
wrapper, a `search_path`, runtime SQL), every one of which needs prior write access on the server. Tokens
fix it because comments are on ANTLR's hidden channel before the scan sees anything, and they fix the
converse too: a name inside a **string literal** is not a call, which the text match refused.

The text match is kept underneath as a fallback **only for a statement the lexer could not read at all**, so
a lexer that threw cannot quietly widen what is accepted. The distinction has to be typed — `Scan` returns
null for "could not read" and an empty set for "read it, found nothing", and the first version collapsed
them, which brought the string-literal false positive straight back (§1.7's rule, in a new place).

**Three catalogs are refused as well as the functions** (`ExternalDeniedRelations`). §1.8 forbids Bearing's
own catalog reads from selecting `pg_authid.rolpassword`, `pg_subscription.subconninfo` and
`pg_user_mappings.umoptions` — and an exposed connection reintroduced all three, because each is a plain
SELECT the allow-list admits. `pg_user_mappings` is the one that does not need an over-privileged
connection: it shows `umoptions` in full to the mapping's **owner**, so an agent handed a "read-only, safe"
connection could read a foreign server's password in cleartext. Whole relations rather than columns, because
`select *` names no column. The `pg_roles` view stays readable — masking the hash is what it is for (§1.7),
and the list is about what a catalog *holds*, not about the subject being sensitive.

### §1.11c — One invocation is one connection; concurrency is the server's to bound

Measured: `select count(*) from pg_stat_activity where usename = current_user and datname =
current_database()` returns **1** during a CLI run. A command runs its batch sequentially, so the pool's
ceiling of 10 is never approached and capping it client-side would buy nothing — which is why there is no
such cap. What is *not* bounded is how many invocations run at once, and no client-side setting can bound
it. `ALTER ROLE agent_ro CONNECTION LIMIT n` can, which is the same answer as everything else here.

### §1.11e — The command's output is records, and the flags that shape it

Every command answers with a record implementing `ICliResponse`, serialised once in `CliRunner`. They were
hand-built `JsonObject` literals first, which is the wrong default in a codebase this record-heavy: a key is
a string nobody checks, an omitted field fails silently, and the CLI's public output contract was readable
only by reading the code that emitted it.

- **Serialise by the runtime type.** `JsonSerializer.Serialize(response, options)` resolves the contract
  from the *declared* type, and `ICliResponse` has no properties — so every response went out as `{}`. The
  tests caught it and nothing else would have: an empty object parses fine, and every "does not contain"
  assertion passes against it. `CliRunner.Serialize` passes `response.GetType()`.
- `--table` switches on the response **type**, so a command that gains a shape is a compile error rather
  than a silently unhandled case. It used to switch on JSON keys, where a renamed field degraded quietly.
- **An export is unlimited by default**, and the small row cap applies only when nothing was asked for. A
  capped export is a silently truncated *file*, which is the failure the app guards against with "a
  workbook missing a sheet is worse than no workbook". An explicit `--max-rows` is honoured at any size —
  clamping it would give a result the caller cannot tell from the whole answer.
- **xlsx takes a sheet per result set; CSV holds one table** and refuses a batch that returned more. RFC
  4180 cannot express two tables and the app's own Export-run is xlsx-only. Refusing beats writing
  `report.1.csv`: the caller named a path, and a script that finds no file at it is broken in a way an
  error is not.
- **`--timeout` may only lower** what the connection allows. Raising it would let a caller lift a limit its
  owner set (§1.9). Measured, and now pinned live in both directions plus the equal case, which is where an
  off-by-one would hide: `--timeout 5` gives `statement_timeout = 5s`, `--timeout 600` leaves it 30s.
- **An option that means nothing for the command it was typed on is refused by name**, including
  `--max-rows`, which was accepted and silently ignored on `connections`, `tables`, `describe` and `explain`.
  The principle was already written into `Validate` for every other flag; the one that predated it was the
  one that did not follow it. A caller who typed it believes it is doing something.
- **Every CLR type a driver produces needs an arm, and the fallback cannot be trusted to be sane.**
  `CellValue.For` had none for `Array`, and an array is neither `IConvertible` nor `IFormattable` — so the
  invariant `Convert.ToString` fallback answered **`"System.String[]"`** and every `text[]`/`int[]` column
  handed a caller the type name where the value should have been. Lost data wearing the shape of data, in
  the output a program parses, while `--table` rendered the same cell as a list: the two disagreed. An
  array is a JSON array (elements converted the same way, so they nest), hstore is a JSON object, and
  `byte[]` keeps its base64 arm *above* both because a bytea is not a list of 200 numbers.
- **A blank option value is a missing one.** A shell that expanded a variable to nothing hands over an
  empty argument rather than no argument, and `--project ""` reached `Path.GetFullPath("")` and came out as
  an unhandled `ArgumentException` and a stack trace — on the ordinary path, since `bearing --project
  "$PROJ" query …` with `$PROJ` unset is how anybody would write it. `Next` treats blank as absent for
  every option, and `ResolveProject` catches what a path can still throw.
- **A failure to write the file is not a failure of the statement.** Only the streamed export can hit both
  inside one call, so `ResultExport` throws `ExportWriteException` from the file operations specifically and
  the host reports that as `Could not write …`. It is also **not logged**: the statement ran, and an audit
  row saying it failed with "Could not find a part of the path" is a false record of what happened on the
  server (§1.11d). A refusal is logged and a failed read is logged; a full disk is neither.
- **`explain` is PostgreSQL's only, and says so before sending.** `ExplainSql`'s text is
  `EXPLAIN (FORMAT JSON)` and `ExplainPlanParser` reads Postgres' plan JSON back, with nothing checking the
  engine — so on a SQL Server connection it could only ever hand the caller `Incorrect syntax near
  'EXPLAIN'` for a command the help advertised without a caveat. `ISqlDialect.SupportsExplainPlan` gates it
  in **both** hosts; the app had the same gap. A second plan format is a feature (SQL Server's showplan is
  different XML with different measurements), not a translation — §5.4a's rule again.
- **`explain` validates the caller's statement and sends its own.** `EXPLAIN ANALYZE` carries a
  `BEGIN … ROLLBACK` that the allow-list would rightly refuse if it saw it — so what is judged is what the
  caller asked to run, and what is sent is what we wrapped it in. The wrapper is not optional: a plain
  SELECT can call a volatile function that writes. It is also not a promise that nothing happened.

### §1.11f — `bearing` reaches PATH from the installer, on Windows

`VelopackApp`'s `OnAfterInstallFastCallback` / `OnAfterUpdateFastCallback` / `OnBeforeUninstallFastCallback`
add and remove the install directory in the user's `Environment` registry key. Per user, because Bearing installs to
`%LocalAppData%` without elevation and the machine PATH is neither ours nor reachable.

- **Read unexpanded, write back the same kind.** `Environment.GetEnvironmentVariable(…, User)` expands
  `%SystemRoot%`, so reading with it and writing the result back bakes today's values into the user's PATH
  permanently — the classic way an installer corrupts one. `RegistryValueOptions.DoNotExpandEnvironmentNames`
  on the way in, and the existing `RegistryValueKind` on the way out, since a REG_SZ rewrite of a
  REG_EXPAND_SZ PATH breaks every variable in it.
- **Read the value before asking for its kind.** `RegistryKey.GetValueKind` reports a missing value by
  *throwing*, so asking it first turned a profile with no `HKCU\Environment\Path` — a clean Windows install —
  into a silent no-op through the best-effort catch: `bearing` never became a command for that user, with
  nothing said anywhere. A missing value is created as `ExpandString`, since a PATH is the canonical
  REG_EXPAND_SZ. `WindowsPath.Apply` takes the key so this is testable against a scratch one rather than
  against the developer's own PATH.
- **The editing is pure and the I/O is thin** (`WindowsPathEntry` / `WindowsPath`), because the editing is
  where the corruption comes from: empty entries dropped (an empty PATH entry means the current directory
  to the loader), duplicates removed, trailing separators and quotes and case all treated as the same
  directory, and "nothing to do" reported as null so an update does not rewrite a PATH it agrees with.
- Appended rather than prepended: nothing here should shadow an existing command.
- Best-effort (§5.2). A PATH that cannot be edited costs a full path typed once; an install that failed
  over it costs the install.

### What a pure check may and may not answer
`ExternalAccessPolicy.UnavailableReason` reports only what the `CredentialKind` settles — a kind whose whole
mechanism is asking the user cannot work where there is no user. It deliberately does **not** report whether
the credential can be obtained *right now*: an unreachable keyring, an expired `az` session, a password never
stored are runtime facts, and a pure function claiming them would be asserting a cause nobody checked (§1.1).
A host reports those when the connect fails, with what the attempt actually said.

