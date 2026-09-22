# Architecture Rules (§2.x)

Layered clean architecture. Reference: `.claude/references/architecture-principles.md`.

## §2.1 — `Bearing.Core` is dependency-free
`Core` holds only abstractions and records: `Data/`, `Schema/`, `Workspace/`, `Logging/`, `Completion/`,
`Updates/`.
- NEVER add a `PackageReference` or `ProjectReference` to `Bearing.Core`.
- Every other project references `Core`; `Core` references nothing. Provider/impl types (Npgsql, SQLite,
  Avalonia) live in the outer projects and are injected behind `Core` interfaces
  (`IDbProvider`, `IProjectStore`, `IQueryLog`, `ISchemaSnapshot`, …).

## §2.2 — Dependency direction (never invert)
```
Core  ←  Sql, Data, Persistence, Updates  ←  Sessions  ←  Results  ←  App  ←  Desktop
                                                                    ↖  Cli   (the `bearing` command)
```
- `Sql` (SQL parsing/completion), `Data` (Postgres/Npgsql, SQL Server), `Persistence` (SQLite), `Updates`
  (Velopack release feed / self-update) each depend on `Core` only.
- `Sessions` depends on `Core`, `Sql` and `Data` — see §2.6.
- `Results` depends on those plus `Sessions` — see §2.7.
- `App` (Avalonia MVVM) composes them; `Desktop` is the thin entry point.
- `Cli` is a **second host** over the same `Sessions`, not a layer under `App`: a windowless console exe
  with its own composition root (§1.11). It must never reference `App`.
- DO NOT reference `App`/Avalonia types from `Core`/`Sql`/`Data`/`Persistence`/`Sessions`/`Results`.

## §2.3 — MVVM boundaries
- Business logic (DB access, connection lifecycle, SQL execution, editing) lives in ViewModels or the
  service layer (`Connections/`, `Results/`, `Formatting/`), never in `Views/*.axaml.cs` code-behind or
  `Controls/`. Code-behind wires events and builds visuals only.
- ViewModels use `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `ObservableObject`). Keep the public
  binding surface stable when refactoring; extract helpers behind thin delegating members.

## §2.4 — Composition root
- All wiring is manual `new` in `src/Bearing.App/App.axaml.cs`. There is no DI container despite the
  `Microsoft.Extensions.DependencyInjection` reference — construct dependencies explicitly and pass them in.

## §2.5 — Pure logic extraction
- Prefer pulling pure, testable logic (SQL building, result shaping, key/gesture parsing, fuzzy ranking)
  into stateless helpers under `Results/`, `Input/`, or the `Sql` project so it can be unit-tested without
  a UI or a live connection. This is the established pattern (`ResultSetBuilder`, `ResultEditModel`,
  `WriteGuard`, `PaletteFilter`, `GestureParser`).

## §2.6 — `Bearing.Sessions` is everything that must happen before a statement runs
Credential resolution (`CredentialResolver`, `EntraTokenProvider`), the one connect recipe
(`ConnectionFactoryBuilder`), pooled sessions and their leases (`ConnectionSessionManager`,
`ConnectionSession`, `SessionKey`, `SessionLease`), the schema browser, the per-engine text facts
(`ProviderTraits`) and the read-only refusal (`WriteRefusal`). Extracted from `Bearing.App` for the outside
invoke work: a second host — the headless MCP process — runs SQL through the same machinery, and **a second
copy of the connect recipe or of `WriteRefusal` is exactly the drift §1.9 exists to prevent**. A refusal that
holds in the editor and not in the other host is worse than no refusal, because it reads as enforced.

- It has **no Avalonia and no `Persistence` reference**, and both are load-bearing: a host with no window has
  to be able to reference it, and its stores arrive through `Core`'s interfaces (`ISecretStore`,
  `IQueryLog`, …) rather than as concrete SQLite/keychain types.
- WHEN a change decides **whether or how a statement may run** — a refusal, a credential, a pool, which
  dialect shapes the text — it belongs here, not in `App`. `App` decides what to *ask the user*
  (`WriteConfirmation`, the dialogs) and what to *show*.
- What deliberately stayed in `App`: `TabTransactions` and `CommitModes` (both keyed on a tab, which is a
  UI concept — an agent has no tabs and never opens a manual-commit transaction, §1.10), and the connection
  panel's own helpers (`ConnectionTree`, `ConnectionClipboard`, `ConnectionFieldModel`, `ConnectionState`,
  `CredentialKindOptions`, `FolderPath`, `SecretStorageAdvice`).
- `SqlLiteralStyle` moved to `Bearing.Sql` in the same pass — it is a fact about an engine's SQL *text*, and
  `ProviderTraits` (which pairs it with the dialect) is no longer in the App layer. The renderer that reads
  it, `Results.SqlValue`, stayed where it was.

## §2.7 — `Bearing.Results` is how a result set becomes text, a table or a file
`CellFormat` (the cell renderer), `TableFormats` (CSV / Markdown / JSON / SQL / HTML), `XlsxWriter`,
`SqlValue`, `SheetNames` and `ResultExport`. Extracted from `Bearing.App` when a second host started
writing these files: the app's Export menu and the `bearing` command's export flag.

**The reason is §2.6's, and it is not effort.** A second CSV writer in the CLI would be a second definition
of what an export *is*, and the two would drift — the app's xlsx writes numbers as typed cells so they still
sum in Excel, and its CSV carries a BOM so Excel does not mangle non-ASCII (§9.10c). Sharing the writers
makes both hosts produce byte-identical files by construction rather than by review.

- **What stayed in `App` is what needs a view model**: `Results/ResultBlocks` — building a block from a live
  result or a grid selection, and the names an export suggests. A command line has a result, no tab to name
  a file after, and no selection at all.
- `CellFormat.Zone` is process-wide, so **every host has to set it**. The app does at startup and on change
  (#77); a host that forgets renders timestamps in UTC while the app beside it renders them in the user's
  zone, and nothing would say so.
- WHEN adding an output format, it goes here and not in either host — that is the whole point of the tier.
