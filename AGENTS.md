# AGENTS.md

> Portable guidance for AI coding tools (Codex, Cursor, Gemini CLI, etc.). Claude Code reads `CLAUDE.md` +
> `.claude/rules/` instead; this mirrors their essentials. **Source of truth:** `CLAUDE.md`,
> `.claude/rules/*.md`, `.claude/references/*`. Sections marked `## Custom:` are preserved if regenerated.

## What this is

Bearing is a cross-platform **Avalonia 12.1 desktop** SQL query tool & script manager for PostgreSQL, SQL Server, MySQL/MariaDB and Amazon DynamoDB
(.NET 10). Layered solution:

```
Bearing.Core        abstractions + records only — zero dependencies
  ↑     ↑      ↑
Sql   Data   Persistence     (SQL parse/completion · Postgres via Npgsql · SQLite local state)
        ↑
Bearing.App         Avalonia MVVM shell (ViewModels/Views/Controls/Input/Connections/Results)
        ↑
Bearing.Desktop     thin entry point
```

- **UI:** Avalonia + AvaloniaEdit + Avalonia DataGrid; MVVM via CommunityToolkit.Mvvm.
- **Data:** raw ADO.NET — Npgsql (Postgres), Microsoft.Data.SqlClient (SQL Server), MySqlConnector (MySQL/MariaDB), the AWS SDK for .NET (Amazon DynamoDB, PartiQL `ExecuteStatement`) + Microsoft.Data.Sqlite (local). **No ORM, no MediatR.**
- **SQL intelligence:** ANTLR4 grammars + antlr4-c3 (`Bearing.Sql`). DynamoDB's PartiQL (`PartiQlDialect`) has no grammar of its own: it is read with the PostgreSQL lexer and completes through the PostgreSQL rule set.
- **DI:** manual composition root in `src/Bearing.App/App.axaml.cs` (no container).

## Build / test

- Build: `dotnet build`
- Test: `dotnet test` (live database tests skip cleanly with no DB, and a skip is not a pass; `./build/test-db.sh` provisions Postgres on 55434 and then they run live with no env vars. Siblings: `./build/test-db-mssql.sh`, `./build/test-db-mysql.sh`, `./build/test-db-dynamodb.sh` — DynamoDB Local on 18000)
- Format: `dotnet format --verbosity quiet`

## Critical rules

1. **NEVER add a package/project reference to `Bearing.Core`** — it stays dependency-free abstractions; everything depends on it, not the reverse.
2. **NEVER claim UI/visual behavior is verified** — Wayland blocks headless GUI testing; build + tests are the automated ceiling, visual changes need human eyeball QA.
3. **NEVER log passwords or weaken the secret-store / `WriteGuard` posture** without explicit approval.
4. **Do NOT grow the god objects** — `Controls/ResultView.cs`, `Views/MainWindow.axaml.cs`, `ViewModels/MainWindowViewModel.cs` are oversized; extract into a View/Control, a coordinator, or a pure helper (`Results/`, `Input/`).
5. **No DB/connection/SQL logic in Views or code-behind** — it belongs in a ViewModel or the `Core`/`Sql`/`Data` layers.

## Conventions

- **Style:** file-scoped namespaces, `var` for locals, early return over `else`, braces on all control flow. Full guide: `.claude/references/dotnet/coding-guidelines.md`.
- **Testing:** xUnit only (no NUnit/MSTest). No mocking library — use hand-rolled fakes (`tests/**/Fakes.cs`). Postgres tests use `Xunit.SkippableFact` + `BEARING_TEST_PG_*`.
- **Input:** keyboard handling goes through the unified `src/Bearing.App/Input/` pipeline (keymap + command registry); register a command, don't hand-roll `OnKeyDown`.
- **Data:** propagate `CancellationToken` through async DB calls; keep DML parameterized; respect `SessionLease` on long reads.
- **Git:** commits are opt-in (don't commit/push unless asked); short lowercase imperative messages.

## Reference docs

- `.claude/references/architecture-principles.md` — layering + known debt
- `.claude/references/dotnet/coding-guidelines.md` — full C# style guide
- `.claude/references/security-checklist.md` — security checklist
- `.claude/references/pre-commit-review-list.md` — fast pre-commit checks
