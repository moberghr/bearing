# Spec — Amazon DynamoDB as a fourth engine (read-focused v1)

Date: 2026-11-18 · Scope: **new-feature, multi-file (Core seams + Data + Sql + Sessions + App + Cli)** ·
Spec mode: standard · Status: **approved 2026-11-18 (revision 2: `bearing` command included)**

Decisions settled with the user (interview, three rounds, plus the approval answer):

| # | Question | Decision |
|---|---|---|
| Q1 | What must v1 do with a table's data? | **Query only.** No grid editing. Typed PartiQL writes still run, through the write guard. |
| Q2 | How does a connection sign in to AWS? | **AWS profile only.** Bearing stores no AWS secret. |
| Q3 | What happens before a full table scan? | **Per-connection setting** "Confirm full table scans", on by default. |
| Q4 | Is the `bearing` command in v1? | **Yes, for reads.** It refuses any `SELECT` it cannot prove is keyed — nobody is there to confirm a scan. |

Everything else below is a design choice made in this spec and listed under *Risks and assumptions* for
approval.

## Summary

Add `dynamodb` ("Amazon DynamoDB") as a fourth provider. A user can create a connection from an AWS profile
and a region, browse tables and their indexes in the schema tree, and run PartiQL in the editor. Results load
a page at a time and "load more" continues from DynamoDB's own continuation token. A statement that would
read a whole table asks first when the connection's "Confirm full table scans" setting is on.

DynamoDB is not a relational engine, so several Bearing features are **withheld on this engine rather than
approximated** (§1.1: never offer a setting or make a claim nobody arranged): grid editing, manual commit,
query plans, the Activity panel, roles, routines, session time zone, the TLS-mode picker, total row counts,
SQL formatting, script variables and table DDL. An exposed connection can be read by the `bearing` command,
which runs keyed `SELECT`s only.

## Behaviour

1. **Connection editor.** Engine picker gains "Amazon DynamoDB" (last). Its fields:
   - **Region** (required) — stored in `ConnectionInfo.Host`.
   - **AWS profile** (optional; blank = the AWS SDK's default credential chain) — stored in
     `ConnectionInfo.User`.
   - **Endpoint** (optional, for DynamoDB Local or a VPC endpoint) — stored in `Options["Endpoint"]`.
   - *Added 2026-11-19 (user request):* Region and AWS profile are **editable dropdowns** — free text stays
     possible. Region suggests the AWS SDK's built-in named regions shown as `eu-west-1 — Europe (Ireland)`; only the
     code is saved. AWS profile suggests the profiles in the AWS **config** file (`AWS_CONFIG_FILE` or
     `~/.aws/config`; never the credentials file, §1.9c), and picking one fills an **empty** Region with its `region`
     (a region the user typed is never overwritten; one the pick wrote follows the pick). Under Endpoint: "Leave blank
     for AWS; set only for DynamoDB Local or a VPC/FIPS endpoint." Core gained `ConnectionField.Suggestions` /
     `ConnectionField.Hint` and `ConnectionSuggestion` (additive); Data gained `AwsConfigProfiles` and `AwsRegions`.
   - `Port` is saved as `0`, so the endpoint text reads `profile@region` with no port.
   - Credential picker offers exactly one kind, **AWS profile**. No password box, no keychain warning.
   - Hidden on this engine: TLS mode, Manual commit, Session time zone. External access is shown.
   - Shown on this engine only: **Confirm full table scans** (checked for a new connection).
   - Read-only, Confirm writes, statement timeout, environment and folder work as on other engines. The
     read-only note uses the "Bearing refuses, the server does not" wording (`EnforcesReadOnlyOnServer` =
     false) and names IAM as the real boundary.
2. **Sign-in.** The AWS SDK resolves the profile (SSO, assumed role, or static keys in `~/.aws`). It does
   so inside Bearing's process (a named profile when the client is built); Bearing's own code never reads, stores,
   prompts for or logs an AWS secret (corrected after review, §1.9c). When the SDK cannot produce credentials (expired
   SSO session, unknown profile) the error is classified `Authentication` and the message names the profile
   and the command to run (`aws sso login --profile <name>`). Bearing does not launch that command.
3. **Endpoint rule.** An `http://` endpoint is accepted only for a loopback host; `http://` to any other
   host is refused at connect with a sentence. With a loopback endpoint and a blank profile the factory
   signs with fixed placeholder keys (DynamoDB Local ignores the signature) so local use needs no AWS setup.
4. **Schema tree.** Server → one database node named after the region → tables. A table shows its declared
   attributes (the table's and its indexes' key attributes — DynamoDB declares no others) and an Indexes
   folder: the primary key, each global and each local secondary index, with partition/sort key and
   projection. Sizes show DynamoDB's approximate byte size and item count. "Select top 100 rows" produces
   `select * from "Table"`. "Show definition" is not offered on tables. Roles, routines, tablespaces and
   the long-tail object kinds return empty and their groups are omitted (existing behaviour).
   **Attribute sample (2026-11-19, user request).** Expanding a table reads one sample — a single
   `ExecuteStatement` of `select * from "Table"` with `Limit` 20, no continuation — and lists the attributes
   found after the key columns, each with its type (string, number, boolean, binary, map, list, string / number /
   binary set, null; `mixed` when items disagree) and marked `sampled`, under the line "Seen in a sample of N
   items — other items may have other attributes." Declared attributes are left out (already listed). Nothing is
   read before the expand, never on connect; not logged; not put to the scan check (bounded by construction). A
   refused read (an IAM denial, a dropped table, a throttle, the connection's timeout) shows one row with the
   scrubbed reason, never an empty list; a sign-in failure shows its sentence. A sample is kept per table until
   the tree's refresh; a refused one is not kept. The sample feeds neither completion nor the schema snapshot
   (whose key ordinals drive the scan check). Seam: `IMetadataReader.SampleAttributesAsync` (default null = the
   engine does not sample), `ISchemaBrowser.SampleAttributesAsync`, `AttributeSample` / `SampledAttribute` in
   Core.
5. **Running PartiQL.** One or more `;`-separated statements; each is sent with `ExecuteStatement`. Results:
   - Columns are the attributes found in the first page: the table's key attributes first, then the rest
     alphabetically.
   - `S` → text, `N` → number (kept as exact text when it does not fit `decimal`), `BOOL` → boolean,
     `B` → binary, map / list / sets → JSON text. A missing attribute and a DynamoDB `NULL` both show as the
     grid's null mark (known limitation).
   - A write (`INSERT` / `UPDATE` / `DELETE`) reports its verb and no row count — the API returns none.
6. **Paging.** A page ends when it holds the page size (`ResultPageSize`) in rows, when DynamoDB reports no
   more data, or after **10 requests** (each evaluates at most 1 MB), whichever comes first — so one page of
   a selective filter can never read a whole table. "Load more" and "Fetch all" continue from the
   continuation token; rows already on screen are never re-read. Whether more rows exist comes from the
   token, not from the row count. A short page from the request budget says so in the status line.
7. **Attributes that first appear on a later page.** Grid columns are fixed by the first page (§9.10: never
   rebuild the grid). When a `select *` result has more to load, it carries a trailing
   **`(other attributes)`** column holding, as JSON, any attribute of a row that has no column. A result that
   fits in one page has no such column. Nothing is dropped silently.
8. **Full-scan confirmation.** With the setting on, before a run Bearing classifies each `SELECT`:
   - **Keyed** — the `WHERE` pins the partition key of the table (or of the index named in
     `"Table"."Index"`) with `=` or `IN`, in a top-level `AND`, or in every branch of a top-level `OR`.
     Runs without asking.
   - **Full scan** — anything else Bearing can read. Asks, naming the table (and index), its approximate
     item count when known, and that rows load a page at a time.
   - **Unfiltered full scan** (adopted 2026-11-19 at the user's request) — a full scan with **no `WHERE` at
     all** (an `ORDER BY`, the projection or an index target do not change that). Every item it evaluates is
     returned, so its first page reads about one page of items: it **runs without asking**. Its result is still
     marked as a scan, so "Fetch all" and Export ask. Any `WHERE` that does not pin the key still asks (one page
     may evaluate up to 10 MB); so does every "could not check". A batch mixing an unfiltered `SELECT` with
     others that need asking asks once, listing only those. The `bearing` command is unchanged (*Behaviour 12*).
   - **Could not check** — unreadable statement, unknown table, schema not loaded within the time limit.
     Asks, and says it could not check (never "this is a scan").
   - Cancel is the default button and runs nothing. "Fetch all" and Export on a result confirmed as a scan
     (or from an unfiltered scan) ask once more, naming the Fetch-all row limit. "Load more" does not ask.
   - The check may connect and load the table list (the run would anyway), bounded by a 4 s budget.
   - The dialog and the setting's note say this stops mistakes and that the boundary is an IAM policy
     (`dynamodb:FullTableScan`).
9. **Write guard and read-only.** `PartiQlDialect` flags `INSERT`, `UPDATE`, `DELETE`, and — conservatively —
   any statement whose lead verb is not `SELECT`. Read-only connections refuse them through the existing
   `WriteRefusal`; "Confirm writes" confirms them through the existing dialog (row-impact count shows
   "could not count").
10. **Withheld features and what the user sees.**

    | Feature | On a DynamoDB connection |
    |---|---|
    | Grid editing | Lock chip: "Read-only — editing isn't available on Amazon DynamoDB connections yet." |
    | Count total | Existing "Count unavailable for this query on this engine." |
    | Manual commit | Checkbox and mode pill hidden; a hand-edited `ManualCommit: true` is ignored |
    | Explain plan | Existing `SupportsExplainPlan` = false path |
    | Activity panel | Existing `SupportsServerActivity` = false path |
    | Format SQL | Existing `FormatRefusal` path |
    | `:name` variables / `@set` | Not recognised; the text is sent as typed |
    | `bearing explain` | Existing `SupportsExplainPlan` = false refusal |
    | Copy as SQL INSERT | Unchanged generic SQL with the `«table»` placeholder (not PartiQL — known limitation) |

11. **Statement timeout.** `StatementTimeoutSeconds` is honoured as a client-side cancel of the page read
    (the SQL Server arrangement, §1.9a). The error names the connection's timeout and classifies `Canceled`.
12. **`bearing` command (exposed connections only).** `connections`, `tables`, `describe`, `query` and
    `query --export` work as on other engines, under the existing forced settings (read-only on, manual commit
    off, a timeout filled in — §1.11). On top of them:
    - The allow-list (`ExternalReadVerbs`) is `SELECT` only, so writes and anything else are refused by the
      existing policy sentences.
    - Every `SELECT` is classified with the same `ScanGuard`, **whatever the connection's "Confirm full table
      scans" setting**. Only **Keyed** statements run. A full scan is refused with "'{name}': this SELECT
      would read the whole of "{table}" — the WHERE has no = or IN on its partition key "{pk}". The bearing
      command runs keyed reads only; add a partition-key condition, or run it in Bearing." That includes an
      unfiltered `SELECT` (no `WHERE`), whose first page only the app runs unasked (*Behaviour 8*). An
      **Undetermined** statement is refused saying Bearing could not check it, never that it scans.
    - Refusals are logged like every other CLI refusal (§1.11d).
    - `query` without `--export` returns the first page (`ResultPageSize`) and says when more rows exist;
      `--export` follows the continuation to the end, capped by `--max-rows` when given.
    - `explain` is refused by the existing `SupportsExplainPlan` = false check.

## Success criteria

Live criteria (SC7–SC10) need the DynamoDB Local container; a skip is not a pass (§4.2).

| ID | Outcome | Verification | Channel | Observable |
|---|---|---|---|---|
| SC1 | `dynamodb` is registered fourth, named "Amazon DynamoDB", and paired with its traits and dialect | `ProviderRegistryTests`, `ProviderTraitsTests` | test-run | Both classes pass; registry ids equal `postgres, sqlserver, mysql, dynamodb` |
| SC2 | The connection editor shows Region / AWS profile / Endpoint, offers only the AWS-profile credential, hides TLS / Manual commit / Session time zone, keeps External access, and shows "Confirm full table scans" checked; the other three engines show none of the DynamoDB-only items | `Ui/ConnectionEditorTests`, `CredentialKindOptionsTests` | test-run | New DynamoDB editor tests pass; existing editor tests pass unchanged apart from the engine list |
| SC3 | A saved DynamoDB connection has `Host` = region, `User` = profile, `Port` = 0, `CredentialKind.AwsProfile`; `ConfirmFullScans` survives project.json and the clipboard; endpoint text is `profile@region` | `ConnectionFieldModelTests`, `ConnectionEndpointTests`, `ConnectionManagementTests` | test-run | New cases pass |
| SC4 | Resolving an `AwsProfile` credential reads no secret, writes none and never prompts | `CredentialResolverTests` | test-run | Fake store and fake prompt record 0 calls; test passes |
| SC5 | SDK "no credentials / expired SSO / unknown profile" failures classify `Authentication` and the message names the profile and `aws sso login --profile <name>`; access-denied classifies `Unknown` with the service message; no key or token text appears | `ProviderClassificationTests`, `DynamoDbErrorTextTests` | test-run | Theories pass |
| SC6 | `http://` to a non-loopback endpoint is refused at connect; loopback `http://` and any `https://` are accepted | `DynamoDbConnectionFactoryTests` | test-run | Theories pass |
| SC7 | Live: a table with partition + sort key, one GSI and one LSI appears in the snapshot with key columns flagged (partition key ordinal 1); table details list primary key, GSI and LSI with their keys; a size entry is returned; routines, roles, tablespaces and object kinds are empty | `DynamoDbLiveTests` (schema) | test-run | Pass with 0 skipped while the container is up |
| SC8 | Live: a keyed `SELECT` returns rows with key columns first then alphabetical; S / N / BOOL / B map to string / decimal / bool / byte[]; map, list and sets come back as JSON text; a missing attribute is null | `DynamoDbLiveTests` (values) | test-run | Pass with 0 skipped while the container is up |
| SC9 | Live: over 250 items with page size 100, the first result has 100 rows, `Truncated` and a continuation; two continuation calls return 100 then 50 rows, the last with no continuation; 250 distinct keys in total | `DynamoDbLiveTests` (paging) | test-run | Pass with 0 skipped while the container is up |
| SC10 | Live: an attribute first seen on page two arrives in `(other attributes)` as JSON; a one-page result has no such column | `DynamoDbLiveTests` (overflow) | test-run | Pass with 0 skipped while the container is up |
| SC11 | A filter matching nothing stops after 10 requests with 0 rows, `Truncated` true and a continuation; a 1 s statement timeout cancels a slow read with an error naming the timeout | `DynamoDbQueryExecutorTests` (hand-rolled fake API) | test-run | Fake records exactly 10 requests; both tests pass |
| SC12 | In the App, a result carrying a continuation loads more and fetches all through `ExecuteContinuationAsync` only (never `ExecutePageAsync` / `StreamRowsAsync`), `HasMore` follows the continuation, and Fetch all stops at the cap with the ceiling report | `ContinuationPagingTests` | test-run | Tests pass; fake records 0 offset-page calls |
| SC13 | The scan classifier returns Keyed / FullScan / Undetermined for the statement table in *Behaviour 8*, including index queries and the documented AWS examples | `PartiQlScanTests`, `ScanGuardTests` | test-run | Theories pass |
| SC14 | With the setting on, a FullScan or Undetermined statement asks once and Cancel runs nothing; Keyed never asks; setting off never asks; other engines never ask; Fetch all / Export on a scan result asks again | `ScanConfirmationTests` | test-run | Fake dialog counts and executor call counts match per case |
| SC15 | `PartiQlDialect`: `INSERT`/`UPDATE`/`DELETE` and unknown lead verbs are risky, `SELECT` is not, keywords inside strings and quoted names do not trip; paging/count hooks return null; `SelectTopRows` is `select * from "T"`; `FormatRefusal` is set; `SupportsExplainPlan` is false; statements split on `;` outside strings, quoted names and comments | `PartiQlDialectTests`, `PartiQlBatchTests` | test-run | Theories pass |
| SC16 | Withheld features: DynamoDB results carry the editing lock reason; `EffectiveConnection` never reports manual commit and `TabTransactions.OpenAsync` refuses; the mode pill is hidden; table nodes do not offer Show definition | `DynamoDbResultTests`, `ManualCommitGuardTests`, `SchemaNodeTests` | test-run | New cases pass |
| SC19 | `bearing` on an exposed DynamoDB connection: listed as available; a keyed `SELECT` runs; a full scan and an undetermined `SELECT` are refused with *Behaviour 12*'s sentences **even with "Confirm full table scans" off**, and logged as refusals; `INSERT`/`UPDATE`/`DELETE` and non-`SELECT` verbs are refused; `--export` follows the continuation to the last row; `explain` is refused | `DynamoDbExposureTests`, `ConnectionListingTests` | test-run | Tests pass; fake executor records 0 calls for every refused statement |
| SC17 | Nothing regresses and layering holds: solution builds, the full suite is green, `Bearing.Core.csproj` still has no package or project reference, `Bearing.Sql` and `Bearing.Data` still do not reference each other | `dotnet build`; `dotnet test`; csproj grep | build-output | Build exit 0; test exit 0; grep finds 0 `Reference` items in `Bearing.Core.csproj` |
| SC18 | Docs and rules name the fourth engine | grep for `DynamoDB` in `README.md`, `CLAUDE.md`, `AGENTS.md`, `docs/CLI.md`, `.claude/rules/{architecture,data-layer,security,testing}.md` | script-output | Every listed file has ≥ 1 match |

UI criteria (SC2, SC14, SC16) are headless assertions. Per §4.3 nothing here claims the dialog or tree
*looks* right; that needs eyeball QA after implementation.

## Architecture and design

### Layering (unchanged, §2.2)

```
Core  ←  Sql (PartiQlDialect)      Data (DynamoDb/*, AWSSDK)
              ↖                   ↗
               Sessions (ProviderTraits arm, CredentialResolver arm, ScanGuard)
                        ↑
                       App (editor gating, continuation paging, scan dialog)      Cli (refusal only)
```

`Bearing.Core` gains members and one enum value, no references. `Sql` and `Data` still do not see each
other, which is why the executor carries its own small statement splitter (below).

### Core seams (all additive)

| Change | Why |
|---|---|
| `CredentialKind.AwsProfile = 4` | A kind with no secret to obtain. Not `Integrated`: that one's dialog copy and SQL Server branches are about the OS identity. |
| `ConnectionInfo.ConfirmFullScans` (bool, default `true`) | Typed field, not an `Options` entry — it is a safety setting (the `Tls` / `ExternalAccess` rule). Inert on other engines. Client-side, so not part of `SamePool` / `SameConnection` (like `ManualCommit`). |
| `QueryResult.Continuation` (`string?`, init) | Opaque, provider-owned. Non-null means "this read can be continued". Holds the statement, DynamoDB's `NextToken` and the column layout. In memory only — never logged or persisted. |
| `IQueryExecutor.ExecuteContinuationAsync(string continuation, int maxRows, CancellationToken)` | Default member that throws `NotSupportedException`, the shape the existing default overloads on this interface use. Only the DynamoDB executor overrides it. |
| `IDbProvider.SupportsPasswordCredentials`, `.SupportsAwsProfile`, `.SupportsManualCommit`, `.SupportsTlsModes` | Explicit flags answered by every provider, following the existing `Supports…` members (§1.9a: a new engine answers the flag, the dialog never offers what the factory cannot honour). Postgres / SQL Server / MySQL / Demo answer `true, false, true, true`; DynamoDB answers `false, true, false, false`. |

### `Bearing.Sql`

- **`PartiQlDialect`** (`Id` = `dynamodb`). Reads with the vendored PostgreSQL lexer: PartiQL shares its
  lexical forms (`'…'` with `''`, `"…"` with `""`, `--` and `/* */` comments). `HasDialectAwareGuard` is
  true on that argument, pinned by a corpus test that includes `[ ]`, `{ }`, `<< >>` and `?` (§1.2).
  `ParseRules` is `PgParseRules.Instance`, so table names and declared key attributes complete; keyword
  completion is Postgres' (known limitation). `TryAppendPage` / `Wrap` / `CountWrap` return null.
  `FindVariables` / `FindDirectives` return empty. `TransactionControl` is always null.
  `ExternalReadVerbs` is `{ SELECT }`; `ExternalDeniedFunctions` / `ExternalDeniedRelations` are empty
  (PartiQL has no function or catalog that reaches past the data). `FormatRefusal` is set.
- **`ISqlDialect.ScanTargets(string sql)`** — default member returning null ("this engine has no scan
  question"). `PartiQlDialect` returns, per `SELECT`, a `ScanTarget(Statement, Table, Index, PinnedAttributes,
  Readable)`: a lexical reduction only, no schema. Implemented in a pure `PartiQlScan` helper.

### `Bearing.Data/DynamoDb`

- **`IDynamoDbApi`** — a three-method internal seam (`ExecuteStatement`, `ListTables`, `DescribeTable`) over
  the SDK client, so the executor and reader are unit-tested with a hand-rolled fake (§4.1: no mocking
  library; the SDK's own interface is far too wide to fake).
- **`DynamoDbConnectionFactory`** — builds the client from profile + region (+ endpoint), applies the
  endpoint rule, caches `DescribeTable` results per session. `TestConnectionAsync` = `ListTables(Limit 1)`.
- **`DynamoDbMetadataReader`** — `ListTables` (paged) + `DescribeTable` with bounded concurrency. Ids are
  derived (stable hash of the table name, the MySQL reader's approach). Schema name is empty so generated
  statements are not schema-qualified. Partition key is ordinal 1 and sort key ordinal 2 by construction.
- **`DynamoDbQueryExecutor`** — `ExecuteAsync` splits the batch with `PartiQlBatch` (a quote- and
  comment-aware `;` scan; `Data` may not use `Sql`'s splitter), runs each statement, sets
  `StatementIndex`, applies the page rules of *Behaviour 6–7*, and honours the statement timeout.
  `ExecutePageAsync`, `CountAsync` and `ExecuteWriteAsync` return an error result saying the operation is
  not available on this engine; the App never calls them here because the dialect returns null first.
  `StreamRowsAsync` follows tokens from the start (kept for contract completeness).
- **`DynamoDbProvider`** — flags as above; `CreateTransactionScope` throws `NotSupportedException` naming
  `SupportsManualCommit`; `CreateServerActivity` returns the existing `UnsupportedServerActivity`.
- Packages (central versions): `AWSSDK.DynamoDBv2`, `AWSSDK.SSO`, `AWSSDK.SSOOIDC`,
  `AWSSDK.SecurityToken` — AWS SDK for .NET V4, latest 4.0.x at implementation time.

### `Bearing.Sessions`

- `ProviderTraits.DynamoDb` arm + `For` branch. Two new members on the record: `GridEditUnavailable`
  (`string?`, the lock sentence; null on the other three) and `OffersTableDdl` (bool; true on the other
  three). Entra / Azure members carry placeholders that are never read on this engine.
- `CredentialResolver`: `AwsProfile` returns `Credential(null, null)` ahead of any store or prompt code.
- **`ScanGuard`** (pure): combines a `ScanTarget` with the partition key looked up from the snapshot (table)
  or `TableDetails.Indexes` (index) into `Keyed` / `FullScan` / `Undetermined`, and writes the sentences.
  It lives here because it decides whether a statement may run (§2.6).

### `Bearing.App`

- `CredentialKindOptions` offers kinds from the provider flags. `ConnectionFieldModel.Apply` saves `Port` 0
  for a provider that declares a `Host` field and no `Port` field (the existing "no fields keeps the
  template" rule is untouched). `ConnectionDialog` hides rows by flag and adds the scan checkbox.
- **`Results/ContinuationPaging`** — new helper holding the load-more and fetch-all loops for a result with
  a continuation. `ExecutionViewModel` gets thin delegating branches only (§9.1: do not grow it).
  `ResultSetViewModel` gains `Continuation` and `IsFullScan`.
- `ScanConfirmation` (pure model) + `ConfirmScanDialog` + `IDialogService.ConfirmScanAsync`. Asked in
  `ExecutionViewModel.ExecuteAsync` after the write-guard confirmation and before `beforeRun`.
- `ResultSetBuilder` sets `LockReason` from `ProviderTraits.GridEditUnavailable` before asking the resolver.
- `WorkspaceContext.EffectiveConnection` forces `ManualCommit` off when the provider cannot hold a
  transaction; `TabTransactions.OpenAsync` refuses as a backstop; `CommitModeVisible` follows the flag.
- `SchemaNodes`: `CanShowDefinition` is false for tables when `OffersTableDdl` is false.

### `Bearing.Cli`

`BearingHost.PrepareAsync`, after `ExternalSqlPolicy.Refuse`, asks `traits.Dialect.ScanTargets(sql)`; when
non-null it classifies each target with `ScanGuard` (snapshot for tables, `TableDetails` for indexes, same 4 s
budget) and throws the existing `Refused(…)` for anything not Keyed. `QueryAsync` / `StreamCsvAsync` follow
a result's continuation for `--export`; plain `query` returns the first page with its `Truncated` flag, as
today.

## Security and compliance impact

**`security_impact`: `new-auth-path`.**

- New credential kind and a new way for Bearing to obtain credentials (AWS SDK profile resolution). No
  secret enters `ISecretStore`, project.json, the query log or any message (§1.1). The profile name and
  region travel in project.json; neither is a secret.
- New outbound dependency family (`AWSSDK.*`) that reads `~/.aws/config`, `~/.aws/credentials` and the SSO
  token cache.
- Read-only is client-side only on this engine (`EnforcesReadOnlyOnServer` = false). Dialog copy must say
  so and name IAM as the boundary (§1.9, §1.9a).
- The scan confirmation is accident prevention, not a boundary (§1.11b's posture). Copy must not describe it
  as cost control or a guarantee.
- Write guard is not narrowed for any engine (§1.2). PartiQL gets a conservative guard of its own.
- External access is a **new unattended read path**: an exposed DynamoDB connection can be read by anything
  running as the user (§1.11's boundary, unchanged). It is held to `SELECT` only and to keyed reads only, so
  an agent cannot start a table scan; both are accident prevention, and the boundary is still an IAM policy.
- Plain-HTTP endpoints are accepted for loopback only (§1.4).
- The query log stores PartiQL text as it does SQL; literal redaction uses the same lexer.

## Change manifest

**Core**
- `src/Bearing.Core/Data/CredentialKind.cs` — modify — add `AwsProfile`
- `src/Bearing.Core/Data/ConnectionInfo.cs` — modify — add `ConfirmFullScans`
- `src/Bearing.Core/Data/QueryResult.cs` — modify — add `Continuation`
- `src/Bearing.Core/Data/IDbProvider.cs` — modify — five provider flags; `ExecuteContinuationAsync` default

**Data**
- `src/Bearing.Data/DynamoDb/DynamoDbProvider.cs` — create
- `src/Bearing.Data/DynamoDb/DynamoDbConnectionFactory.cs` — create
- `src/Bearing.Data/DynamoDb/IDynamoDbApi.cs` — create — narrow SDK seam + real implementation
- `src/Bearing.Data/DynamoDb/DynamoDbMetadataReader.cs` — create
- `src/Bearing.Data/DynamoDb/DynamoDbQueryExecutor.cs` — create
- `src/Bearing.Data/DynamoDb/DynamoDbValues.cs` — create — attribute values → cells, column layout
- `src/Bearing.Data/DynamoDb/DynamoDbContinuation.cs` — create — opaque token encode / decode
- `src/Bearing.Data/DynamoDb/DynamoDbErrors.cs` — create — classification + sign-in sentences
- `src/Bearing.Data/DynamoDb/PartiQlBatch.cs` — create — statement split, lead verb, target table
- `src/Bearing.Data/ProviderRegistry.cs` — modify — register fourth
- `src/Bearing.Data/Postgres/PostgresProvider.cs`, `src/Bearing.Data/SqlServer/SqlServerProvider.cs`,
  `src/Bearing.Data/MySql/MySqlProvider.cs` — modify — answer the four flags
- `src/Bearing.Data/Bearing.Data.csproj`, `Directory.Packages.props` — modify — AWS SDK packages
- `src/Bearing.Demo/DemoProvider.cs` — modify — answer the four flags

**Sql**
- `src/Bearing.Sql/PartiQlDialect.cs` — create
- `src/Bearing.Sql/PartiQlScan.cs` — create — `ScanTarget` + lexical reduction
- `src/Bearing.Sql/ISqlDialect.cs` — modify — `ScanTargets` default member

**Sessions**
- `src/Bearing.Sessions/ProviderTraits.cs` — modify — DynamoDB arm, `GridEditUnavailable`, `OffersTableDdl`
- `src/Bearing.Sessions/CredentialResolver.cs` — modify — `AwsProfile` arm
- `src/Bearing.Sessions/ScanGuard.cs` — create

**App**
- `src/Bearing.App/Connections/CredentialKindOptions.cs` — modify
- `src/Bearing.App/Connections/ConnectionFieldModel.cs` — modify — port rule
- `src/Bearing.App/Connections/TabTransactions.cs` — modify — refuse when unsupported
- `src/Bearing.App/Views/ConnectionDialog.axaml`, `src/Bearing.App/Views/ConnectionDialog.axaml.cs` — modify
- `src/Bearing.App/Results/ContinuationPaging.cs` — create
- `src/Bearing.App/Results/ResultSetBuilder.cs` — modify
- `src/Bearing.App/ViewModels/ResultSetViewModel.cs` — modify
- `src/Bearing.App/ViewModels/ExecutionViewModel.cs` — modify — delegating branches, scan confirmation call
- `src/Bearing.App/ViewModels/SchemaNodes.cs` — modify — `CanShowDefinition`
- `src/Bearing.App/Workspace/WorkspaceContext.cs` — modify — manual commit forced off
- `src/Bearing.App/Services/ScanConfirmation.cs` — create
- `src/Bearing.App/Services/IDialogService.cs`, `src/Bearing.App/Views/DialogService.cs` — modify
- `src/Bearing.App/Views/ConfirmScanDialog.cs` — create

**Cli**
- `src/Bearing.Cli/Tools/BearingHost.cs` — modify — keyed-reads-only refusal; follow continuation on export

**Build, CI, docs**
- `build/test-db-dynamodb.sh` — create — `bearing-dynamodb-test`, `amazon/dynamodb-local`, port 18000
- `.github/workflows/ci.yml` — modify — "Provision DynamoDB Local" step on the database leg
- `README.md`, `CLAUDE.md`, `AGENTS.md`, `docs/CLI.md` — modify
- `.claude/rules/architecture.md`, `.claude/rules/data-layer.md` (new §5.1b), `.claude/rules/security.md`
  (new §1.9c), `.claude/rules/testing.md` — modify

## Test manifest

| Path | Action | Covers |
|---|---|---|
| `tests/Bearing.Data.Tests/ProviderRegistryTests.cs` | modify | SC1 |
| `tests/Bearing.App.Tests/ProviderTraitsTests.cs` | modify | SC1, SC2 |
| `tests/Bearing.App.Tests/Ui/ConnectionEditorTests.cs` | modify | SC2 |
| `tests/Bearing.App.Tests/ConnectionFieldModelTests.cs` | modify | SC3 (and its three fake providers answer the flags) |
| `tests/Bearing.App.Tests/ConnectionEndpointTests.cs` | modify | SC3 |
| `tests/Bearing.App.Tests/ConnectionManagementTests.cs` | modify | SC3 |
| `tests/Bearing.App.Tests/CredentialResolverTests.cs` | modify | SC4 |
| `tests/Bearing.Data.Tests/ProviderClassificationTests.cs` | modify | SC5 |
| `tests/Bearing.Data.Tests/DynamoDbErrorTextTests.cs` | create | SC5 |
| `tests/Bearing.Data.Tests/DynamoDbConnectionFactoryTests.cs` | create | SC6 |
| `tests/Bearing.Data.Tests/DynamoDbLiveTests.cs` | create | SC7, SC8, SC9, SC10 |
| `tests/Shared/DynamoDbTestServer.cs` | create | SC7–SC10 (skip plumbing, `BEARING_TEST_DYNAMODB_ENDPOINT`) |
| `tests/Bearing.Data.Tests/DynamoDbQueryExecutorTests.cs` | create | SC11 |
| `tests/Bearing.Data.Tests/PartiQlBatchTests.cs` | create | SC11, SC15 |
| `tests/Bearing.App.Tests/ContinuationPagingTests.cs` | create | SC12 |
| `tests/Bearing.Sql.Tests/PartiQlScanTests.cs` | create | SC13 |
| `tests/Bearing.App.Tests/ScanGuardTests.cs` | create | SC13 |
| `tests/Bearing.App.Tests/ScanConfirmationTests.cs` | create | SC14 |
| `tests/Bearing.Sql.Tests/PartiQlDialectTests.cs` | create | SC15 |
| `tests/Bearing.App.Tests/ManualCommitGuardTests.cs` | modify | SC16 |
| `tests/Bearing.App.Tests/SchemaNodeTests.cs` | modify | SC16 |
| `tests/Bearing.App.Tests/DynamoDbResultTests.cs` | create | SC16 (lock reason, no edit target) |
| `tests/Bearing.Cli.Tests/ConnectionListingTests.cs` | modify | SC19 |
| `tests/Bearing.Cli.Tests/DynamoDbExposureTests.cs` | create | SC19 |
| `tests/Bearing.App.Tests/Fakes.cs`, `tests/Bearing.App.Tests/Ui/ChoiceFieldTests.cs` | modify | SC17 (fakes answer the flags) |

## Implementation batches

1. **Core seams, no behaviour change.** The four Core edits; every `IDbProvider` implementer (three
   providers, Demo, five test fakes) answers the flags; `ISqlDialect.ScanTargets` default. Build + full suite
   green with nothing new reachable.
2. **`PartiQlDialect` and `PartiQlScan`** with `PartiQlDialectTests`, `PartiQlScanTests`.
3. **Data provider, unit level.** Packages, `IDynamoDbApi`, factory, values, continuation, errors,
   `PartiQlBatch`, executor, metadata reader, registry entry. Unit tests with the fake API (SC5, SC6, SC11).
4. **Live test plumbing.** `build/test-db-dynamodb.sh`, `DynamoDbTestServer`, `DynamoDbLiveTests`, CI step
   (SC7–SC10). Run the container before reporting (§4.2).
5. **Sessions.** `ProviderTraits` arm and new members, `CredentialResolver` arm, `ScanGuard` (SC1, SC4, SC13).
6. **Connection editor.** `CredentialKindOptions`, port rule, dialog gating and scan checkbox (SC2, SC3).
7. **Run path.** `ContinuationPaging`, result view-model and builder changes, scan confirmation dialog and
   flow, manual-commit gating, Show-definition gating (SC12, SC14, SC16).
8. **`bearing` command**: keyed-reads-only refusal and export continuation (SC19).
9. **Docs and rules** (SC18), then the full verification pass (SC17).

Batches 2, 3 and 5 depend only on 1. Batch 4 depends on 3; 6 on 5; 7 and 8 on 3 and 5.

## Risks and assumptions

**Verified against the code**
- [VERIFIED:src/Bearing.App/ViewModels/ExecutionViewModel.cs] The grid pages by "load more" from an offset
  (`LoadMoreAsync`, `PageSql.Page(…, rs.Loaded, PageSize)`), a null from the dialect retires paging, and a
  null `CountWrap` already reports "Count unavailable". So an engine with no `LIMIT`/`OFFSET` needs a seam
  rather than a dialect trick.
- [VERIFIED:src/Bearing.App/Connections/ConnectionFieldModel.cs] `Host`, `Port`, `Database`, `User` are the
  only keys mapped to `ConnectionInfo` properties; any other key lands in `Options`; an undeclared intrinsic
  keeps the template's value. `SameNetwork` compares the intrinsics and not `Options`
  (`ConnectionsViewModel.cs`), which is why region and profile use `Host` and `User`.
- [VERIFIED:src/Bearing.Sessions/EntraTokenProvider.cs] `ProviderTraits.EntraResource`, `EntraTakesUser` and
  `AzureServers` are read only behind an `EntraToken` check, so placeholders on this engine are never read.
  `Literals` and `EndpointHint` are read on every engine and get real values.
- [VERIFIED:src/Bearing.Core/Schema/EditabilityResolver.cs] A result with no base columns is labelled "a
  column is a computed expression, not a table column." That would be a false claim here, hence
  `GridEditUnavailable`.
- [VERIFIED:src/Bearing.App/Views/ConnectionDialog.axaml] TLS, Manual commit, statement timeout and
  External access rows are unconditional today; nothing per-provider says an engine cannot hold a transaction.
- [COVERAGE:1] `TabTransactions.OpenAsync` is the only production caller of `CreateTransactionScope`
  (`src/Bearing.App/Connections/TabTransactions.cs:140`), so one refusal there backstops manual commit.
- [COVERAGE:5] User SQL reaches an executor from five sites in `ExecutionViewModel.cs`: `:750` run,
  `:1022` explain, `:1083` fetch all, `:1283` foreign-key navigation, `:1351` grid save. On this engine
  explain, FK navigation and grid save are unreachable (no plan support, no foreign keys, no editing), so
  confirming in `ExecuteAsync` and in fetch all covers every scan.

**Cited**
- [CITED:https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ExecuteStatement.html] `Limit`
  is "the maximum number of items to evaluate (not necessarily the number of matching items)"; a read stops
  at 1 MB; `NextToken` continues it; statement length is at most 8192 characters.
- [CITED:https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.select.html] A
  `SELECT` is a full scan unless the `WHERE` has an equality or `IN` condition on the partition key; an index
  is addressed as `"Table"."Index"`; `pk = 1 OR other = 2` scans.
- [CITED:https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/sso.html] SSO profile resolution needs
  `AWSSDK.SSO` and `AWSSDK.SSOOIDC` referenced, or it fails at run time.

**Assumed — each is an open decision until its test or measurement lands**
- [ASSUMED] DynamoDB Local honours `Limit` and `NextToken` on `ExecuteStatement` and accepts `--` and
  `/* */` comments. `DynamoDbLiveTests` pins both; if comments are refused, `PartiQlBatch` strips them.
- [ASSUMED] Assumed-role profiles need `AWSSDK.SecurityToken` referenced.
- [ASSUMED] After `aws sso login`, the SDK picks up the new token on the next call without Bearing
  rebuilding the client. If not, an `Authentication` failure evicts the session so the next run rebuilds it.
  Cannot be covered in CI; needs one manual check against a real SSO profile.
- [ASSUMED] A `NextToken` stays valid for as long as a result is on screen. A rejected token surfaces as
  "Load more failed: …" and leaves the loaded rows untouched (existing behaviour for a failed page).
- [ASSUMED] The PostgreSQL lexer reads every PartiQL statement DynamoDB accepts without mis-splitting or
  mis-reading a string. Pinned by the corpus in `PartiQlDialectTests`; a counter-example means the guard
  flips to `HasDialectAwareGuard` = false (every statement confirmed) until a scanner exists.
- [ASSUMED] Completion and "Select top 100 rows" emit an unqualified table name when the snapshot's schema
  name is empty.
- [ASSUMED] Opening the definition view is the only tree action that generates SQL for a table besides
  "Select top 100 rows" (checked: `SidebarView.axaml` menu items).

**Risks**
- `ExecutionViewModel` is a god object (§9.1). Mitigation: logic lands in `ContinuationPaging`, `ScanGuard`
  and `ScanConfirmation`; the view model gets delegating calls only.
- Four new `IDbProvider` flags touch every implementer and five test fakes. Mechanical, but wide.
- Installer size grows with the AWS SDK. Measure the Release package before and after.
- `CentralPackageTransitivePinningEnabled` may require an explicit `AWSSDK.Core` version entry.
- A decorator over `IQueryExecutor` (`TrackedExecutor`, Demo's `CountingExecutor`) does not forward
  `ExecuteContinuationAsync`. Harmless today (no DynamoDB transaction scope); a trap if one is ever added.
- Keyword completion offers Postgres keywords PartiQL lacks (`JOIN`, `LIMIT`). Accepted for v1.
- Wayland blocks headless GUI verification (§4.3): the dialog rows, the scan dialog and the tree need
  eyeball QA.
- dirty-worktree: none — `git status --porcelain` is empty on `main` at `c4a55d6`.

**Conditional descope**
- If DynamoDB Local does not honour `Limit` on `ExecuteStatement`, SC9 is asserted against the fake API
  instead of live, and the live test asserts only that all 250 keys arrive exactly once.

## Out of scope

- Grid editing, inserting and deleting items (Q1).
- Access keys stored in the keychain; launching `aws sso login` from Bearing (Q2).
- Scans from the `bearing` command, even when asked for explicitly (Q4).
- Manual commit / `ExecuteTransaction`; batch statements (`BatchExecuteStatement`).
- Query plans, Activity panel, roles, routines, session time zone.
- Script variables (`:name`, `@set`) and PartiQL `?` parameters.
- A PartiQL grammar, PartiQL-aware keyword completion and SQL formatting.
- Total row counts; consumed-capacity reporting.
- Distinguishing a missing attribute from a DynamoDB `NULL` in the grid.
- A PartiQL form of "Copy as SQL INSERT".
- Creating, altering or deleting tables and indexes; table DDL display.
- Multi-region browsing in one connection; DynamoDB Streams, TTL, backups, global-table settings.
- Release notes (written at release, §9.6a).

## Rejected alternatives

- **Page by re-running and skipping rows.** Re-reads, and re-bills, every row already on screen.
- **Offset encoded in a SQL comment with a token cache in the executor.** Hidden state keyed by text, and
  it breaks "the executor only runs the SQL".
- **Reuse `CredentialKind.Integrated`.** Its meaning, hint text and SQL Server branches are the OS identity.
- **Region and profile in the `Options` bag.** `SameNetwork` would not see a region change and the endpoint
  text would read `localhost:5432`.
- **Indexes as relations under a schema named after the table.** Makes `"Table"."Index"` resolve for free,
  but the tree would show a phantom schema per table.
- **Default interface members for the provider flags.** Smaller diff, but a silent default is what §1.9a
  exists to prevent.
- **Emulate manual commit by queueing writes.** Reads would not see the queued writes; out of scope anyway.
- **Rebuild grid columns when a new attribute appears.** §9.10 forbids rebuilding the grid.
- **A vendored PartiQL ANTLR grammar.** The MySQL grammar alone was ~4,900 lines; not needed for query-only.

Formerly rejected, since adopted:

- **Skip the confirmation for an unfiltered first page** (it reads only one page of items) — adopted
  2026-11-19 at the user's request; Fetch all / Export still ask; the `bearing` command still refuses. Now
  *Behaviour 8*'s "Unfiltered full scan". (Rejected at first because Load more and Fetch all continue the
  same scan; Fetch all keeps its question, and Load more never asked.)

## Open questions

None blocking. The database node under a DynamoDB connection is labelled with the **region** (default,
accepted at approval).

## Prior Work Check

Run once on 2026-11-18 against the draft. **Verdict: clear — nothing to reuse or duplicate.**

| Keyword | Result |
|---|---|
| `dynamo`, `partiql`, `AWSSDK`, `aws profile` | No match in `src/`, `tests/`, `docs/`, `build/` |
| `NextToken`, continuation paging | No match (hits for "continuation" are unrelated task continuations) |
| Branches / issues / PRs | No `*dynamo*` or `*aws*` branch; no issue or PR mentioning DynamoDB |

Existing pieces this spec **extends rather than re-implements**: `WriteGuard` / `WriteRefusal` (writes and
read-only), `ConfirmWriteDialog`'s conventions (the scan dialog), `UnsupportedServerActivity`,
`MySqlMetadataReader.StableId`'s derived-id approach, `MySqlTestServer` + `build/test-db-mysql.sh` (live-test
plumbing), `SupportsExplainPlan` / `FormatRefusal` / `CountWrap` null paths (withheld features).
