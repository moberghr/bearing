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

## §2.6 — `Bearing.Sessions`: everything before a statement runs
Credentials (`CredentialResolver`, `EntraTokenProvider`), the one connect recipe (`ConnectionFactoryBuilder`),
pooled sessions and leases (`ConnectionSessionManager`, `SessionKey`, `SessionLease`), the schema browser,
`ProviderTraits` and `WriteRefusal`. Shared by both hosts — never a second copy of the connect recipe or the
refusal in a host (§1.9).
- No Avalonia and no `Persistence` reference; stores arrive through `Core` interfaces.
- WHEN a change decides **whether or how a statement may run**, it goes here. `App` decides what to ask the
  user and what to show.
- Deliberately in `App`: `TabTransactions`, `CommitModes` (tab-keyed, §1.10) and the connection panel's
  helpers. `SqlLiteralStyle` lives in `Bearing.Sql`.

## §2.7 — `Bearing.Results`: result set → text, table or file
`CellFormat`, `TableFormats`, `XlsxWriter`, `SqlValue`, `SheetNames`, `ResultExport`, `DisplayZone`. Both
hosts share these writers so their files are byte-identical by construction.
- WHEN adding an output format, it goes here, not in a host.
- What needs a view model stays in `App` (`Results/ResultBlocks`).
- `CellFormat.Zone` is process-wide: **every host must set it** (`DisplayZone.Resolve`).
