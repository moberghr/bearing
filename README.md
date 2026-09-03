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
| `Bearing.Updates` | Release feed + self-update (installers, delta updates) | Velopack |
| `Bearing.App` | Avalonia UI — views, view models, input pipeline | Avalonia |
| `Bearing.Desktop` | Thin `WinExe` entry point (kept separate so `App` stays test-referenceable) | Avalonia.Desktop |

`vendor/` holds the antlr4-c3 C# port and the grammars-v4 PostgreSQL grammar base classes; the parser is
generated at build time by `Antlr4BuildTasks` (which downloads a JRE + the ANTLR tool jar on first build).

## Install

Packaged builds carry their own updater: the app checks once per launch, downloads a new version in the
background, and offers a restart. Nothing is installed while you work.

| Platform | Download | Notes |
|---|---|---|
| Windows | `BearingSql-win-Setup.exe` | Per-user install to `%LOCALAPPDATA%\BearingSql`, Start Menu entry. No admin needed. |
| Linux | `BearingSql.AppImage` | `chmod +x` and run it. Updates itself in place. |

Releases are on the repository's [Releases page](https://github.com/moberghr/bearing/releases). Nothing
needs configuring for updates to work; the installers are unsigned, so Windows SmartScreen warns on first
run.

`build/release.sh` remains as the no-updater alternative: a single-file `.tar.gz` (Linux, with a per-user
installer and `.desktop` entry) or `.zip` (Windows, with PowerShell install/uninstall scripts). Updating one
of those means downloading the next one. Cutting a release is publishing a GitHub Release: CI takes the
version from the tag and builds both platforms — see [docs/RELEASING.md](docs/RELEASING.md).

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
are `SkippableFact` and silently skip when no database is reachable. One script provisions one:

```bash
./build/test-db.sh          # start it, load pagila, verify (idempotent)
dotnet test                 # no env vars needed — the defaults point at it
./build/test-db.sh stop     # remove the container
```

It creates the `squirrel-pg-test` container on **55434** with pagila loaded, which is exactly what
`tests/Shared/PgTestServer.cs` defaults to. Deliberately not 5434: that port sits in the range other developer
tooling claims, and on one machine here it was an SSM tunnel to a *real remote* database that these defaults
reached and were refused by. The script refuses to bind over a listener it did not create, and the tests that
run DDL check for a marker row it writes before doing so.

The tests read `BEARING_TEST_PG_*` env vars (host/port/db/user/password) if you would rather point them
somewhere else.

The default password is `squirrel` — a throwaway dev credential for the local test container, nothing to do
with the app. Override it, and every other default, with `BEARING_TEST_PG_*` (`tests/Shared/PgTestServer.cs`).

## Where your data lives

Bearing separates data by *shareability*:

| Tier | Location | Contents | Shared? |
|---|---|---|---|
| **Project** | `<project>/project.json` + `scripts/*.sql` | Connection settings (**no passwords**), SQL scripts | Yes — meant to be committed |
| **Session** | `<project>/.bearing/session.json` | Open tabs, caret, active connection, pane layout | No — gitignored |
| **App-global — data** | platform data dir (below) | `query-log.sqlite`, default project | No |
| **App-global — config** | platform config dir (below) | recent projects, `keybindings.json`, `settings.json` | No |

The two app-global directories follow each platform's own convention:

| Platform | Config dir | Data dir |
|---|---|---|
| Linux | `$XDG_CONFIG_HOME/bearing` (`~/.config/bearing`) | `$XDG_DATA_HOME/bearing` (`~/.local/share/bearing`) |
| Windows | `%APPDATA%\bearing` (roaming) | `%LOCALAPPDATA%\bearing` (local — the query log and secrets must not sync) |
| macOS | `~/Library/Application Support/bearing` | `~/Library/Application Support/bearing` |

`XDG_CONFIG_HOME` / `XDG_DATA_HOME` take precedence on every platform when set, so redirecting state for
tests or a portable install works the same way everywhere.

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

- **Preferred:** the OS credential store, one per platform — the freedesktop Secret Service via `secret-tool`
  (libsecret) on **Linux**, the Credential Manager on **Windows** (visible under Control Panel ▸ Credential
  Manager ▸ Windows Credentials), and a login-keychain generic password on **macOS** (visible in Keychain
  Access). Which one you get is decided at startup by actually storing and reading back a throwaway secret, so
  an absent helper or a locked keyring falls back rather than failing later when you connect.
  *`dotnet test` on each platform runs the full store contract (`PlatformKeychainTests`).*
- **No credential store reachable?** Then the password is **not saved at all** — the connection keeps it in
  memory for the session and asks again next time. There is no on-disk fallback and nothing to opt into: the
  status bar and the connection dialog say so, and the dialog shows what the credential store actually
  reported (a locked keyring and a missing helper want different fixes). Because the check can fail simply by
  running before the keyring is serving, it is re-run every time the connection dialog opens — an upgrade
  only, so a working keychain is never dropped mid-session.

### Query log

Every query you run is recorded in `query-log.sqlite` (append-only, full-text searchable). History is pruned to
a configurable retention window (default 180 days) — see `settings.json`.
