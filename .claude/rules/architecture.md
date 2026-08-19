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
Core  ←  Sql, Data, Persistence, Updates  ←  App  ←  Desktop
```
- `Sql` (SQL parsing/completion), `Data` (Postgres/Npgsql), `Persistence` (SQLite), `Updates` (Velopack
  release feed / self-update) each depend on `Core` only.
- `App` (Avalonia MVVM) composes them; `Desktop` is the thin entry point.
- DO NOT reference `App`/Avalonia types from `Core`/`Sql`/`Data`/`Persistence`.

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
