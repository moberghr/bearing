# 🐿 Squirrel

A fast desktop SQL query tool and script manager for PostgreSQL, with schema-aware
autocomplete as its headline feature — table/column/alias completion and foreign-key
smart-joins that go well beyond a plain keyword list.

Built with .NET 10 and [Avalonia](https://avaloniaui.net/) (cross-platform desktop UI).

## Highlights

- **Schema-aware completion** — ANTLR4 PostgreSQL grammar + [antlr4-c3](https://github.com/mike-lischke/antlr4-c3)
  compute candidates at the caret: tables, columns, resolved aliases, and FK-derived `JOIN … ON …` predicates.
- **Projects as directories** — a shareable `project.json` (connections, no passwords) plus a `scripts/` folder
  of `.sql` files. Commit it to git and share it with the team; per-user session state stays local.
- **Inline result editing** — edit cells in a result grid and Squirrel generates parameterized
  UPDATE/DELETE/INSERT keyed by the detected primary key, committed as one transaction.
- **FK navigation** — click a foreign-key cell to follow it to the referenced row, with a back stack.
- **Query history** — every execution is logged to a searchable local SQLite database (full-text over the SQL).
- **Connection environments** — tag connections (local / staging / production) with a color that threads
  through the whole UI so you always know what you're pointed at.
- **Customizable keybindings** and a command palette (`Ctrl+Shift+P`).

## Project layout

The solution (`Squirrel.slnx`) is layered so the SQL engine is testable in isolation from the UI:

| Project | Responsibility | External deps |
|---|---|---|
| `Squirrel.Core` | Domain vocabulary + provider/persistence interfaces | none |
| `Squirrel.Sql` | SQL parsing + completion (ANTLR grammar, antlr4-c3, DML generation) | ANTLR runtime |
| `Squirrel.Data` | PostgreSQL implementation of the provider interfaces | Npgsql |
| `Squirrel.Persistence` | Project files, session state, query log (SQLite), secret store | Microsoft.Data.Sqlite |
| `Squirrel.App` | Avalonia UI — views, view models, input pipeline | Avalonia |
| `Squirrel.Desktop` | Thin `WinExe` entry point (kept separate so `App` stays test-referenceable) | Avalonia.Desktop |

`vendor/` holds the antlr4-c3 C# port and the grammars-v4 PostgreSQL grammar base classes; the parser is
generated at build time by `Antlr4BuildTasks` (which downloads a JRE + the ANTLR tool jar on first build).

## Build & run

```bash
dotnet build
dotnet run --project src/Squirrel.Desktop
```

On first run, Squirrel opens a default project at `$XDG_DATA_HOME/squirrel/projects/default` and seeds a
demo connection pointing at a local `pagila` database.

## Tests

```bash
dotnet test
```

Pure unit tests (SQL/completion, persistence, formatting) run with no external services. The `Squirrel.Data`
integration tests need a live PostgreSQL loaded with [pagila](https://github.com/devrimgunduz/pagila); they
are `SkippableFact` and silently skip when no database is reachable. To run them, point the app at a container:

```bash
SQUIRREL_TEST_PG_PORT=5434 dotnet test
```

The tests read `SQUIRREL_TEST_PG_*` env vars (host/port/db/user/password) and default to a local pagila
container. Bring one up with e.g.:

```bash
docker run -d --name squirrel-pg-test -p 5434:5432 \
  -e POSTGRES_PASSWORD=squirrel -e POSTGRES_DB=pagila postgres:17
# then load the pagila schema + data into it
```

## Where your data lives

Squirrel separates data by *shareability*:

| Tier | Location | Contents | Shared? |
|---|---|---|---|
| **Project** | `<project>/project.json` + `scripts/*.sql` | Connection settings (**no passwords**), SQL scripts | Yes — meant to be committed |
| **Session** | `<project>/.squirrel/session.json` | Open tabs, caret, active connection, pane layout | No — gitignored |
| **App-global** | `$XDG_DATA_HOME/squirrel/` | `query-log.sqlite`, `secrets/`, default project | No |
| | `$XDG_CONFIG_HOME/squirrel/` | recent projects, `keybindings.json`, `settings.json` | No |

### Profiles — isolating dev from real data

Set `SQUIRREL_PROFILE=<name>` and the app-global directory name becomes `squirrel-<name>` (e.g.
`~/.config/squirrel-dev`, `~/.local/share/squirrel-dev`), plus a matching keychain namespace. This keeps
one instance's connections, history, and secrets fully separate from another's.

Running from source (`dotnet run`) automatically uses the **`dev`** profile via
`src/Squirrel.Desktop/Properties/launchSettings.json`, so development never touches the installed app's real
projects and settings. The published/installed binary has no profile and uses the plain `squirrel` dirs.

### Secrets

Connection passwords are **never** written to `project.json`. They are stored keyed by the connection's GUID
(which does travel with the project) so a shared project prompts each user for their own password.

- **Preferred:** the OS keychain. On Linux this is the freedesktop Secret Service via `secret-tool` (libsecret).
- **Fallback:** when no keychain is reachable, passwords are written to per-connection files under
  `$XDG_DATA_HOME/squirrel/secrets/` (mode `0600`). **This is not encrypted storage** — the app surfaces a
  warning when it falls back, and you should prefer a machine with a working keyring for sensitive credentials.

### Query log

Every query you run is recorded in `query-log.sqlite` (append-only, full-text searchable). History is pruned to
a configurable retention window (default 180 days) — see `settings.json`.
