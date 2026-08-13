# ⚙ Bearing

A fast desktop SQL query tool and script manager for PostgreSQL, with schema-aware
autocomplete as its headline feature — table/column/alias completion and foreign-key
smart-joins that go well beyond a plain keyword list.

Built with .NET 10 and [Avalonia](https://avaloniaui.net/) (cross-platform desktop UI).

## Highlights

- **Schema-aware completion** — ANTLR4 PostgreSQL grammar + [antlr4-c3](https://github.com/mike-lischke/antlr4-c3)
  compute candidates at the caret: tables, columns, resolved aliases, and FK-derived `JOIN … ON …` predicates.
- **Projects as directories** — a shareable `project.json` (connections, no passwords) plus a `scripts/` folder
  of `.sql` files. Commit it to git and share it with the team; per-user session state stays local.
- **Inline result editing** — edit cells in a result grid and Bearing generates parameterized
  UPDATE/DELETE/INSERT keyed by the detected primary key, committed as one transaction.
- **FK navigation** — click a foreign-key cell to follow it to the referenced row, with a back stack.
- **Query history** — every execution is logged to a searchable local SQLite database (full-text over the SQL).
- **Connection environments** — tag connections (local / staging / production) with a color that threads
  through the whole UI so you always know what you're pointed at.
- **Customizable keybindings** and a command palette (`Ctrl+Shift+P`).

## Project layout

The solution (`Bearing.slnx`) is layered so the SQL engine is testable in isolation from the UI:

| Project | Responsibility | External deps |
|---|---|---|
| `Bearing.Core` | Domain vocabulary + provider/persistence interfaces | none |
| `Bearing.Sql` | SQL parsing + completion (ANTLR grammar, antlr4-c3, DML generation) | ANTLR runtime |
| `Bearing.Data` | PostgreSQL implementation of the provider interfaces | Npgsql |
| `Bearing.Persistence` | Project files, session state, query log (SQLite), secret store | Microsoft.Data.Sqlite |
| `Bearing.App` | Avalonia UI — views, view models, input pipeline | Avalonia |
| `Bearing.Desktop` | Thin `WinExe` entry point (kept separate so `App` stays test-referenceable) | Avalonia.Desktop |

`vendor/` holds the antlr4-c3 C# port and the grammars-v4 PostgreSQL grammar base classes; the parser is
generated at build time by `Antlr4BuildTasks` (which downloads a JRE + the ANTLR tool jar on first build).

## Build & run

```bash
dotnet build
dotnet run --project src/Bearing.Desktop
```

On startup Bearing reopens the project you last used. On first run (or if that project is gone) it opens a
default project at `projects/default` inside the app data directory (see
[Where your data lives](#where-your-data-lives)) and seeds a demo connection pointing at a local
`pagila` database.

## Tests

```bash
dotnet test
```

Pure unit tests (SQL/completion, persistence, formatting) run with no external services. The `Bearing.Data`
integration tests need a live PostgreSQL loaded with [pagila](https://github.com/devrimgunduz/pagila); they
are `SkippableFact` and silently skip when no database is reachable. To run them, point the app at a container:

```bash
BEARING_TEST_PG_PORT=5434 dotnet test
```

The tests read `BEARING_TEST_PG_*` env vars (host/port/db/user/password) and default to a local pagila
container. Bring one up with e.g.:

```bash
docker run -d --name bearing-pg-test -p 5434:5432 \
  -e POSTGRES_PASSWORD=squirrel -e POSTGRES_DB=pagila postgres:17
# then load the pagila schema + data into it
```

The default password is still `squirrel` (the pre-rename value) so existing local test containers keep
working — it is a throwaway dev credential, not app identity. Override it with `BEARING_TEST_PG_PASSWORD`.

## Where your data lives

Bearing separates data by *shareability*:

| Tier | Location | Contents | Shared? |
|---|---|---|---|
| **Project** | `<project>/project.json` + `scripts/*.sql` | Connection settings (**no passwords**), SQL scripts | Yes — meant to be committed |
| **Session** | `<project>/.bearing/session.json` | Open tabs, caret, active connection, pane layout | No — gitignored |
| **App-global — data** | platform data dir (below) | `query-log.sqlite`, `secrets/`, default project | No |
| **App-global — config** | platform config dir (below) | recent projects, `keybindings.json`, `settings.json` | No |

The two app-global directories follow each platform's own convention:

| Platform | Config dir | Data dir |
|---|---|---|
| Linux | `$XDG_CONFIG_HOME/bearing` (`~/.config/bearing`) | `$XDG_DATA_HOME/bearing` (`~/.local/share/bearing`) |
| Windows | `%APPDATA%\bearing` (roaming) | `%LOCALAPPDATA%\bearing` (local — the query log and secrets must not sync) |
| macOS | `~/Library/Application Support/bearing` | `~/Library/Application Support/bearing` |

`XDG_CONFIG_HOME` / `XDG_DATA_HOME` take precedence on every platform when set, so redirecting state for
tests or a portable install works the same way everywhere.

### Upgrading from Squirrel

The app was called **Squirrel** before, and read its data from `squirrel`-named directories. There is
**no automatic migration** — nothing is deleted, but a fresh Bearing install will not see your old data
until you move it. Your **projects and scripts are unaffected**: they live wherever you put them, and
`project.json` is unchanged.

Move the app-global directories — on Linux (fish); on Windows/macOS rename the same two directories in the
platform locations from the table above:

```fish
mv ~/.config/squirrel ~/.config/bearing
mv ~/.local/share/squirrel ~/.local/share/bearing
```

That carries over recent projects, `keybindings.json`, `settings.json`, the query log, and any
file-fallback secrets. Then, **in each project directory**, move the per-project session folder so your
open tabs and pane layout come back:

```fish
mv path/to/project/.squirrel path/to/project/.bearing
```

**Keychain passwords do not carry over.** Secrets are keyed by an `app` attribute that matches the app
directory name, so entries stored under `app=squirrel` are invisible to a build looking up `app=bearing`.
The simplest fix is to re-enter each connection's password in the connection dialog. To move them instead,
for each connection GUID in your `project.json`:

```fish
secret-tool lookup app squirrel connection <guid> \
  | secret-tool store --label "Bearing connection <guid>" app bearing connection <guid>
```

Or scripted across a whole project, with `jq`:

```fish
for id in (jq -r '.connections[].id' path/to/project/project.json)
    secret-tool lookup app squirrel connection $id \
      | secret-tool store --label "Bearing connection $id" app bearing connection $id
end
```

Old `app=squirrel` entries are left in place; clear them with
`secret-tool clear app squirrel connection <guid>` once you have confirmed Bearing connects.

### Profiles — isolating dev from real data

Set `BEARING_PROFILE=<name>` and the app-global directory name becomes `bearing-<name>` (e.g.
`~/.config/bearing-dev`, `~/.local/share/bearing-dev`), plus a matching keychain namespace. This keeps
one instance's connections, history, and secrets fully separate from another's.

Running from source (`dotnet run`) automatically uses the **`dev`** profile via
`src/Bearing.Desktop/Properties/launchSettings.json`, so development never touches the installed app's real
projects and settings. The published/installed binary has no profile and uses the plain `bearing` dirs.

### Secrets

Connection passwords are **never** written to `project.json`. They are stored keyed by the connection's GUID
(which does travel with the project) so a shared project prompts each user for their own password.

- **Preferred:** the OS keychain. On Linux this is the freedesktop Secret Service via `secret-tool` (libsecret).
  There is **no** keychain backend on Windows or macOS yet — those platforms always take the fallback below.
- **Fallback:** when no keychain is reachable, passwords are written to per-connection files under
  `secrets/` in the data dir (mode `0600` where the OS supports it). **This is not encrypted storage** — the
  app surfaces a warning when it falls back, and you should prefer a machine with a working keyring for
  sensitive credentials.

### Query log

Every query you run is recorded in `query-log.sqlite` (append-only, full-text searchable). History is pruned to
a configurable retention window (default 180 days) — see `settings.json`.
