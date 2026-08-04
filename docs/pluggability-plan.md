# Pluggability architecture — engines, dialects, and a LINQ frontend

> Design notes from the 2026-07 architecture discussion. Nothing here is committed except the
> **Core-neutralization refactor** (in progress on branch `review-fixes`). The rest is direction, not
> a promise — captured so the seams are designed before the first second engine/frontend lands.

## The two seams

Adding capability to Bearing splits along two independent axes. Keeping them separate is the whole
point.

### Seam 1 — the runtime provider (`Bearing.Data`)
One `IDbProvider` per engine → `{ IDbConnectionFactory, IMetadataReader, IQueryExecutor }`, registered
in `ProviderRegistry`. This seam already exists. A new engine implements it and plugs in.

**Prerequisite (doing now):** `Core` must expose only engine-neutral types so a provider isn't forced to
hand Postgres notions back up. See *Core neutralization* below.

### Seam 2 — the SQL dialect / intelligence (`Bearing.Sql`)
Today `Bearing.Sql` is a **PostgreSQL** layer: ANTLR PG grammar (completion, folding, statement-split,
write-guard, alias/FROM extraction), `"…"` identifier quoting (`DmlGenerator`/`TableDdlGenerator`), and
`LIMIT/OFFSET` paging (`PageSql`). Making it pluggable = an **`ISqlDialect`** supplied per engine:

- `QuoteIdent`, `PageSql`, `CountSql` — text generation.
- a grammar/parse handle — for completion/folding/guard.

**Not needed now.** It only matters when a second engine with a *divergent* dialect arrives.

## Engine cost is not uniform

| | SQLite | SQL Server |
|---|---|---|
| Seam 1 (provider) | new `Data` folder, bounded | new `Data` folder, bounded |
| Paging | `LIMIT/OFFSET` works as-is | needs `OFFSET…FETCH` (+ mandatory ORDER BY) |
| Quoting | `"…"` works | `[…]` — generators diverge |
| Completion/folding/guard | PG grammar mostly tolerates it | T-SQL — PG grammar won't parse it |
| Column origin (edit/FK-nav) | different reader API | different API + OID model doesn't fit |

**SQLite** reuses almost all of `Sql` (shared dialect) → mostly seam-1 work. **SQL Server** needs its own
dialect (`ISqlDialect` + a T-SQL grammar) or ships with degraded/no SQL intelligence.

## Core neutralization — DONE (2026-07-24, branch `review-fixes`)

`Core` no longer leaks Postgres notions. Renamed to neutral types; the **pure resolvers stay in `Core`**;
the Postgres provider maps its OIDs → neutral ids when building the snapshot. Behavior unchanged — build
clean, 309 tests green (Data live, 0 skipped). Verified: no `pg`/`oid`/`attnum`/`relkind` tokens left in
`Core` (one doc line names the Postgres provider by way of explaining the seam), and `Core` still has
zero project/package references.

| Postgres notion | Neutral replacement |
|---|---|
| `PgTable(uint Oid, …)` | `TableInfo(long Id, …)` |
| `PgColumn(uint TableOid, short AttNum, …)` | `ColumnInfo(long TableId, int Ordinal, …)` |
| `PgForeignKey(uint …Oid, IReadOnlyList<short> …AttNums)` | `ForeignKeyInfo(long …Id, IReadOnlyList<int> …Ordinals)` |
| `PgRoutine(uint Oid, …)` | `RoutineInfo(long Id, …)` |
| `PgRelKind` / `PgRoutineKind` | `RelationKind` / `RoutineKind` |
| `ColumnDescriptor.BaseTableOid`/`BaseColumnAttNum` (`uint`/`short`) | `BaseTableId`/`BaseColumnOrdinal` (`long`/`int`) |
| `IMetadataReader.GetViewDefinitionAsync(uint relOid)` / `…(uint routineOid)` / `pg_get_*` docs | `(long tableId)` / `(long routineId)` / neutral docs |

Identity stays a plain numeric `(long tableId, int ordinal)` pair — engine-neutral (SQL Server has exactly
`object_id`/`column_id`; a provider whose native ids don't fit assigns its own when building the snapshot).
No opaque wrapper struct — more ceremony than "minimize bleed" needs.

## A LINQ frontend (separate axis — a query *frontend*, not a backend)

Goal: LINQPad-style. Reference the user's project/DLL containing a `DbContext`, supply a connection
string, write LINQ in C#; **EF Core** translates and runs it.

This is orthogonal to both seams and flips the earlier "write a compiler" estimate:

- **LINQ→SQL translation** → EF does it (the user's own `DbContext`). Not our problem.
- **C# / type-directed completion** → host **Roslyn** (`CompletionService`) — entity types, `DbSet`
  tables, columns, extension methods, all for free. Prior art: **RoslynPad** = AvaloniaEdit + Roslyn.

### What it reuses vs. needs
- **Reuses:** results grid (read-only), editor host, tab shell.
- **Bypasses entirely:** `Bearing.Sql`, and our provider/dialect seams — EF owns both the SQL *and* the
  driver. LINQ mode therefore "supports" whatever engine the user's EF provider targets, for free.
- **Loses:** inline edit + FK-nav for LINQ results — they're materialized objects, not a live cursor with
  column origin (like LINQPad's read-only dumps).

### The three pieces
1. **Roslyn completion + C# editing** — medium; largely off-the-shelf (`RoslynPad.Roslyn`).
2. **Executing the user's C#/EF** — the real hard part, and it's *dependency isolation*, not language:
   must run against the user's **exact EF + provider versions**. Solve with an **out-of-process runner**
   (`Bearing.ScriptRunner`) that references the user's assembly, executes, returns results over IPC —
   also sandboxes crashes and enables cancel-by-kill. In-process collectible `AssemblyLoadContext` is
   fragile once native provider deps appear.
3. **Result marshalling** — reflect over EF's result element type → `ColumnDescriptor[]` + rows (read-only).

Plus a new **"C# workspace" connection type**: (assembly path + `DbContext` type + connection string)
instead of host/port/db/user — touches the connect dialog + workspace model + `DbContext` discovery
(`DbContextOptionsBuilder` / `IDesignTimeDbContextFactory`).

### Where it lives
New `Bearing.Scripting` (Roslyn hosting + runner protocol) + `Bearing.ScriptRunner` (executable),
both off `Core`. App gains a per-tab "C#/LINQ" language mode routing completion to Roslyn and execution
to the runner; results into the existing read-only grid.

### Bottom line
Medium overall, front-loaded on the runner/dependency-isolation plumbing and the connection-model UX —
**not** the multi-month compiler risk of hand-rolling translation. De-risk by spiking RoslynPad first.
If the real goal is just "a friendlier query language" (not C# LINQ specifically), a smaller DSL or PRQL
gets ~80% of the ergonomics without the EF-hosting machinery.
